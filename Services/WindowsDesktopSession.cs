using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace ExamKiosk.Services;

/// <summary>
/// Runs the exam UI on a separate Windows desktop and keeps that desktop
/// selected if the UI process closes or crashes. The configured exit flow in
/// the UI returns the user to the normal desktop.
/// </summary>
internal sealed class WindowsDesktopSession : IDisposable
{
    private const uint DesktopCreateWindow = 0x0002;
    private const uint DesktopEnumerate = 0x0040;
    private const uint DesktopReadObjects = 0x0001;
    private const uint DesktopSwitchDesktop = 0x0100;
    private const uint DesktopWriteObjects = 0x0080;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint HandleFlagInherit = 0x00000001;
    private const int ProcThreadAttributeHandleList = 0x00020002;
    private const uint WaitObject0 = 0x00000000;
    private const uint WaitTimeout = 0x00000102;
    private const uint WaitFailed = 0xFFFFFFFF;
    private const int UserObjectName = 2;
    private static readonly TimeSpan MaximumRestartDelay = TimeSpan.FromSeconds(5);

    private readonly Action<string?> onEnded;
    private readonly string desktopName = $"ExamKiosk-{Guid.NewGuid():N}";
    private readonly object sync = new();
    private readonly ManualResetEvent shutdownSignal = new(false);
    private IntPtr defaultDesktop;
    private IntPtr examDesktop;
    private IntPtr childProcess;
    private IntPtr exitRequestReadPipe;
    private Thread? supervisor;
    private bool stopping;
    private bool disposed;

    public WindowsDesktopSession(Action<string?> onEnded)
    {
        this.onEnded = onEnded;
    }

    public void Start()
    {
        defaultDesktop = OpenDesktop("Default", 0, false, DesktopSwitchDesktop);
        if (defaultDesktop == IntPtr.Zero)
        {
            throw LastWin32Exception("Open the normal Windows desktop");
        }

        examDesktop = CreateDesktop(
            desktopName,
            null,
            IntPtr.Zero,
            0,
            DesktopCreateWindow | DesktopEnumerate | DesktopReadObjects | DesktopSwitchDesktop | DesktopWriteObjects,
            IntPtr.Zero);
        if (examDesktop == IntPtr.Zero)
        {
            throw LastWin32Exception("Create the isolated exam desktop");
        }

        childProcess = StartChildProcess();
        if (childProcess == IntPtr.Zero)
        {
            throw LastWin32Exception("Start ExamKiosk on the isolated desktop");
        }

        if (!SwitchDesktop(examDesktop))
        {
            var exception = LastWin32Exception("Switch to the exam desktop");
            TerminateProcess(childProcess, 1);
            throw exception;
        }

        try
        {
            supervisor = new Thread(Supervise)
            {
                IsBackground = false,
                Name = "ExamKiosk desktop supervisor"
            };
            supervisor.Start();
        }
        catch
        {
            SwitchDesktop(defaultDesktop);
            TerminateProcess(childProcess, 1);
            throw;
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            stopping = true;
            disposed = true;
        }

        shutdownSignal.Set();
        if (supervisor is not null && supervisor != Thread.CurrentThread)
        {
            supervisor.Join();
        }

        if (childProcess != IntPtr.Zero)
        {
            CloseHandle(childProcess);
            childProcess = IntPtr.Zero;
        }

        CloseExitRequestPipe();

        if (examDesktop != IntPtr.Zero)
        {
            CloseDesktop(examDesktop);
            examDesktop = IntPtr.Zero;
        }

        if (defaultDesktop != IntPtr.Zero)
        {
            CloseDesktop(defaultDesktop);
            defaultDesktop = IntPtr.Zero;
        }

        shutdownSignal.Dispose();
    }

    private void Supervise()
    {
        var restartDelay = TimeSpan.FromMilliseconds(250);
        Stopwatch? childUptime = null;

        while (!IsStopping())
        {
            if (childProcess == IntPtr.Zero)
            {
                if (WaitForRestart(restartDelay))
                {
                    return;
                }

                try
                {
                    childProcess = StartChildProcess();
                    if (childProcess == IntPtr.Zero)
                    {
                        throw LastWin32Exception("Restart ExamKiosk on the isolated desktop");
                    }

                    childUptime = Stopwatch.StartNew();
                }
                catch
                {
                    restartDelay = IncreaseRestartDelay(restartDelay);
                    continue;
                }
            }

            var waitResult = WaitForSingleObject(childProcess, 200);
            if (waitResult == WaitObject0)
            {
                var authorizedExit = GetExitCodeProcess(childProcess, out var exitCode)
                    && exitCode == 0
                    && ReadAuthorizedExitRequest();

                CloseHandle(childProcess);
                childProcess = IntPtr.Zero;
                CloseExitRequestPipe();
                childUptime = null;

                var inputDesktop = GetInputDesktopName();
                if (authorizedExit
                    && IsNormalSessionDesktop(inputDesktop)
                    && SwitchDesktop(defaultDesktop))
                {
                    NotifyEnded(null);
                    return;
                }

                // A crash or ordinary close is not an exit authorization.
                // Keep the exam desktop selected and restore its UI process.
                restartDelay = IncreaseRestartDelay(restartDelay);
            }
            else if (waitResult == WaitFailed)
            {
                CloseHandle(childProcess);
                childProcess = IntPtr.Zero;
                CloseExitRequestPipe();
                childUptime = null;
                restartDelay = IncreaseRestartDelay(restartDelay);
            }
            else if (waitResult != WaitTimeout)
            {
                restartDelay = IncreaseRestartDelay(restartDelay);
            }

            if (childProcess != IntPtr.Zero && childUptime?.Elapsed > TimeSpan.FromSeconds(30))
            {
                restartDelay = TimeSpan.FromMilliseconds(250);
            }

            // Do not fight Winlogon, the lock screen, or an unreadable secure
            // desktop. Let Windows handle its own sign-in and recovery screens.
            EnsureExamDesktopSelected();
        }
    }

    private IntPtr StartChildProcess()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            throw new InvalidOperationException("Windows did not provide the ExamKiosk executable path.");
        }

        var isDotnetHost = string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase);
        var entryAssembly = Assembly.GetEntryAssembly()?.Location;
        if (isDotnetHost && string.IsNullOrWhiteSpace(entryAssembly))
        {
            throw new InvalidOperationException("The ExamKiosk assembly path could not be determined.");
        }

        var securityAttributes = new SecurityAttributes
        {
            Length = Marshal.SizeOf<SecurityAttributes>(),
            InheritHandle = true
        };
        if (!CreatePipe(out var readPipe, out var writePipe, ref securityAttributes, 0))
        {
            throw LastWin32Exception("Create the authorized-exit channel");
        }

        IntPtr attributeList = IntPtr.Zero;
        IntPtr inheritedHandleList = IntPtr.Zero;
        var attributeListInitialized = false;
        try
        {
            if (!SetHandleInformation(readPipe, HandleFlagInherit, 0))
            {
                throw LastWin32Exception("Secure the authorized-exit channel");
            }

            var attributeListSize = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);
            if (attributeListSize == IntPtr.Zero)
            {
                throw LastWin32Exception("Prepare the child-process handle list");
            }

            attributeList = Marshal.AllocHGlobal(attributeListSize);
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
            {
                throw LastWin32Exception("Initialize the child-process handle list");
            }
            attributeListInitialized = true;

            inheritedHandleList = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(inheritedHandleList, writePipe);
            if (!UpdateProcThreadAttribute(
                attributeList,
                0,
                new IntPtr(ProcThreadAttributeHandleList),
                inheritedHandleList,
                new IntPtr(IntPtr.Size),
                IntPtr.Zero,
                IntPtr.Zero))
            {
                throw LastWin32Exception("Restrict inherited child-process handles");
            }

            if (!SetHandleInformation(writePipe, HandleFlagInherit, HandleFlagInherit))
            {
                throw LastWin32Exception("Allow the child to inherit its exit channel");
            }

            var handleArgument = unchecked((ulong)writePipe.ToInt64()).ToString("X", System.Globalization.CultureInfo.InvariantCulture);
            var commandLine = isDotnetHost
                ? $"\"{executable}\" \"{entryAssembly}\" --exam-desktop-child \"{desktopName}\" {handleArgument}"
                : $"\"{executable}\" --exam-desktop-child \"{desktopName}\" {handleArgument}";

            var startupInfo = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfoEx>(),
                    Desktop = $"WinSta0\\{desktopName}"
                },
                AttributeList = attributeList
            };

            if (!CreateProcess(
                executable,
                new StringBuilder(commandLine),
                IntPtr.Zero,
                IntPtr.Zero,
                true,
                CreateUnicodeEnvironment | ExtendedStartupInfoPresent,
                IntPtr.Zero,
                AppContext.BaseDirectory,
                ref startupInfo,
                out var processInformation))
            {
                throw LastWin32Exception("Create the exam UI process");
            }

            CloseHandle(processInformation.Thread);
            CloseHandle(writePipe);
            writePipe = IntPtr.Zero;
            CloseExitRequestPipe();
            exitRequestReadPipe = readPipe;
            readPipe = IntPtr.Zero;
            return processInformation.Process;
        }
        finally
        {
            if (attributeListInitialized)
            {
                DeleteProcThreadAttributeList(attributeList);
            }
            if (attributeList != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(attributeList);
            }
            if (inheritedHandleList != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(inheritedHandleList);
            }
            if (readPipe != IntPtr.Zero)
            {
                CloseHandle(readPipe);
            }
            if (writePipe != IntPtr.Zero)
            {
                CloseHandle(writePipe);
            }
        }
    }

    private bool ReadAuthorizedExitRequest()
    {
        if (exitRequestReadPipe == IntPtr.Zero
            || !PeekNamedPipe(exitRequestReadPipe, IntPtr.Zero, 0, IntPtr.Zero, out var available, IntPtr.Zero)
            || available < sizeof(uint))
        {
            return false;
        }

        var request = new byte[sizeof(uint)];
        return ReadFile(exitRequestReadPipe, request, (uint)request.Length, out var read, IntPtr.Zero)
            && read == request.Length
            && BitConverter.ToUInt32(request) == App.ExitRequestSignature;
    }

    private void CloseExitRequestPipe()
    {
        if (exitRequestReadPipe != IntPtr.Zero)
        {
            CloseHandle(exitRequestReadPipe);
            exitRequestReadPipe = IntPtr.Zero;
        }
    }

    private bool WaitForRestart(TimeSpan delay)
    {
        return shutdownSignal.WaitOne(delay);
    }

    private void EnsureExamDesktopSelected()
    {
        var inputDesktop = GetInputDesktopName();
        if (IsNormalSessionDesktop(inputDesktop)
            && !string.Equals(inputDesktop, desktopName, StringComparison.OrdinalIgnoreCase))
        {
            SwitchDesktop(examDesktop);
        }
    }

    private bool IsNormalSessionDesktop(string? name)
    {
        return string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, desktopName, StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetInputDesktopName()
    {
        var inputDesktop = OpenInputDesktop(0, false, DesktopReadObjects);
        if (inputDesktop == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var name = new StringBuilder(256);
            if (!GetUserObjectInformation(
                inputDesktop,
                UserObjectName,
                name,
                name.Capacity * sizeof(char),
                out _))
            {
                return null;
            }

            return name.ToString();
        }
        finally
        {
            CloseDesktop(inputDesktop);
        }
    }

    private bool IsStopping()
    {
        lock (sync)
        {
            return stopping;
        }
    }

    private void NotifyEnded(string? error)
    {
        lock (sync)
        {
            stopping = true;
        }

        onEnded(error);
    }

    private static TimeSpan IncreaseRestartDelay(TimeSpan current)
    {
        var nextMilliseconds = Math.Min(current.TotalMilliseconds * 2, MaximumRestartDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(nextMilliseconds);
    }

    private static Win32Exception LastWin32Exception(string operation)
    {
        return new Win32Exception(Marshal.GetLastWin32Error(), operation);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Size;
        public IntPtr Reserved2;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool InheritHandle;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDesktop(string desktop, string? device, IntPtr devmode, uint flags, uint desiredAccess, IntPtr securityAttributes);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenDesktop(string desktop, uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint desiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint desiredAccess);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder information, int length, out uint needed);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SwitchDesktop(IntPtr desktop);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(
        string applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out IntPtr readPipe, out IntPtr writePipe, ref SecurityAttributes attributes, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr attributeList, uint attributeCount, uint flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr attributeList, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previousValue, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekNamedPipe(IntPtr pipe, IntPtr buffer, uint bufferSize, IntPtr bytesRead, out uint bytesAvailable, IntPtr bytesLeftThisMessage);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadFile(IntPtr file, byte[] buffer, uint bytesToRead, out uint bytesRead, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
