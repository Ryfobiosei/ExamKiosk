using System.Configuration;
using System.Data;
using System.Threading;
using System.Windows;

namespace ExamKiosk.Configurator;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private const string SingleInstanceMutexName = "Global\\ExamKioskConfiguratorSingleton";
    private static Mutex? singleInstanceMutex;
    private static bool mutexOwned;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!CreateSingleInstanceGuard())
        {
            Shutdown();
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            MessageBox.Show($"Unhandled exception:{Environment.NewLine}{ex}", "ExamKiosk Configurator", MessageBoxButton.OK, MessageBoxImage.Error);
        };

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show($"Dispatcher exception:{Environment.NewLine}{args.Exception}", "ExamKiosk Configurator", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (mutexOwned)
        {
            singleInstanceMutex?.ReleaseMutex();
        }

        singleInstanceMutex?.Dispose();
        singleInstanceMutex = null;
        mutexOwned = false;

        base.OnExit(e);
    }

    private static bool CreateSingleInstanceGuard()
    {
        singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out var createdNew);
        mutexOwned = createdNew;

        if (!createdNew)
        {
            MessageBox.Show("ExamKiosk Configurator is already running. Close the existing instance before launching a new one.", "ExamKiosk Configurator", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        return true;
    }
}

