using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using ExamKiosk.Configuration;
using ExamKiosk.Services;

namespace ExamKiosk;

public partial class App : Application
{
    private const string ChildDesktopArgument = "--exam-desktop-child";
    internal const uint ExitRequestSignature = 0x45584B53;

    private WindowsDesktopSession? desktopSession;
    private KeyboardShortcutFilter? keyboardFilter;
    private ExamWindowMonitor? windowMonitor;
    private static IntPtr exitAuthorizationPipe;

    internal static bool IsExamDesktopChild { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        if (e.Args.Length == 3
            && e.Args[0] == ChildDesktopArgument
            && long.TryParse(e.Args[2], System.Globalization.NumberStyles.HexNumber, null, out var pipeHandle))
        {
            exitAuthorizationPipe = new IntPtr(pipeHandle);
            StartExamDesktopChild(e.Args[1]);
            return;
        }

        try
        {
            EnsureAuthorizedExitConfigured();
            desktopSession = new WindowsDesktopSession(OnDesktopSessionEnded);
            desktopSession.Start();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"ExamKiosk could not start the kiosk session: {exception.Message}",
                "ExamKiosk",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        keyboardFilter?.Dispose();
        windowMonitor?.Dispose();
        if (exitAuthorizationPipe != IntPtr.Zero)
        {
            CloseHandle(exitAuthorizationPipe);
            exitAuthorizationPipe = IntPtr.Zero;
        }
        desktopSession?.Dispose();
        base.OnExit(e);
    }

    private void StartExamDesktopChild(string desktopName)
    {
        try
        {
            IsExamDesktopChild = true;
            keyboardFilter = new KeyboardShortcutFilter();
            keyboardFilter.Start();
            windowMonitor = new ExamWindowMonitor(desktopName);
            windowMonitor.Start();

            var window = new MainWindow { Topmost = true };
            MainWindow = window;
            window.Show();
        }
        catch (Exception exception)
        {
            ShowInitializationFailure(exception);
        }
    }

    internal static bool RequestAuthorizedExit()
    {
        if (!IsExamDesktopChild || exitAuthorizationPipe == IntPtr.Zero)
        {
            return false;
        }

        var request = BitConverter.GetBytes(ExitRequestSignature);
        if (!WriteFile(exitAuthorizationPipe, request, (uint)request.Length, out var written, IntPtr.Zero)
            || written != request.Length)
        {
            return false;
        }

        CloseHandle(exitAuthorizationPipe);
        exitAuthorizationPipe = IntPtr.Zero;
        if (Current?.MainWindow is MainWindow window)
        {
            window.PrepareAuthorizedApplicationShutdown();
        }
        Current?.Shutdown(0);
        return true;
    }

    private void OnDesktopSessionEnded(string? error)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!string.IsNullOrWhiteSpace(error))
            {
                MessageBox.Show(error, "ExamKiosk", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            Shutdown(error is null ? 0 : 1);
        });
    }

    private void ShowInitializationFailure(Exception exception)
    {
        var errorWindow = new Window
        {
            Title = "ExamKiosk could not start",
            Width = 620,
            Height = 250,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            Background = System.Windows.Media.Brushes.White,
            Content = new System.Windows.Controls.TextBlock
            {
                Text = $"ExamKiosk could not initialize the exam session.\n\n{exception.Message}\n\nContact the exam administrator. Do not restart the exam without authorization.",
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(36),
                FontSize = 20
            }
        };

        MainWindow = errorWindow;
        errorWindow.Show();
    }

    private static void EnsureAuthorizedExitConfigured()
    {
        var profilePath = KioskProfile.FindProjectProfilePath("kiosksettings.json")
            ?? Path.Combine(AppContext.BaseDirectory, "kiosksettings.json");
        var profile = KioskProfile.Load(profilePath);
        profile.ValidateForSecureSession();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteFile(IntPtr file, byte[] buffer, uint bytesToWrite, out uint bytesWritten, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
