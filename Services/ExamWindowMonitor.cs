using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ExamKiosk.Services;

/// <summary>
/// Hides top-level windows created by applications that are not part of the
/// ExamKiosk/WebView2 session on the isolated exam desktop.
/// </summary>
internal sealed class ExamWindowMonitor : IDisposable
{
    private const uint DesktopReadObjects = 0x0001;
    private const uint DesktopEnumerate = 0x0040;
    private const int SwHide = 0;

    private static readonly HashSet<string> AllowedProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ExamKiosk",
        "msedgewebview2",
        "tabtip",
        "textinputhost"
    };

    private readonly object pollLock = new();
    private readonly EnumWindowsProcedure callback;
    private IntPtr desktop;
    private Timer? timer;
    private int currentProcessId;

    public ExamWindowMonitor(string desktopName)
    {
        callback = InspectWindow;
        desktop = OpenDesktop(desktopName, 0, false, DesktopReadObjects | DesktopEnumerate);
        if (desktop == IntPtr.Zero)
        {
            throw new InvalidOperationException($"The exam desktop could not be opened for window monitoring (Windows error {Marshal.GetLastWin32Error()}).");
        }

        currentProcessId = Environment.ProcessId;
    }

    public void Start()
    {
        timer ??= new Timer(_ => Poll(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(250));
    }

    public void Dispose()
    {
        var currentTimer = timer;
        timer = null;
        if (currentTimer is not null)
        {
            using var callbacksComplete = new ManualResetEvent(false);
            if (currentTimer.Dispose(callbacksComplete))
            {
                callbacksComplete.WaitOne();
            }
        }

        lock (pollLock)
        {
            if (desktop != IntPtr.Zero)
            {
                CloseDesktop(desktop);
                desktop = IntPtr.Zero;
            }
        }
    }

    private void Poll()
    {
        if (!Monitor.TryEnter(pollLock))
        {
            return;
        }

        try
        {
            if (desktop != IntPtr.Zero)
            {
                EnumDesktopWindows(desktop, callback, IntPtr.Zero);
            }
        }
        catch
        {
            // A process can close a window while the desktop is being enumerated.
            // A later poll checks the remaining windows again.
        }
        finally
        {
            Monitor.Exit(pollLock);
        }
    }

    private bool InspectWindow(IntPtr window, IntPtr parameter)
    {
        if (!IsWindowVisible(window))
        {
            return true;
        }

        GetWindowThreadProcessId(window, out var processId);
        if (processId == currentProcessId || IsAllowedProcess((int)processId))
        {
            return true;
        }

        ShowWindow(window, SwHide);
        return true;
    }

    private static bool IsAllowedProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return AllowedProcessNames.Contains(process.ProcessName);
        }
        catch
        {
            return false;
        }
    }

    private delegate bool EnumWindowsProcedure(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenDesktop(string desktop, uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint desiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDesktopWindows(IntPtr desktop, EnumWindowsProcedure callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);
}
