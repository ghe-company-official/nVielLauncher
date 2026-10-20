using System.Windows;
using System.Windows.Threading;
using nVielLauncher.Services;

namespace nVielLauncher;

public partial class App : Application
{
    public App()
    {
        AppServices.Initialize();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("FATAL: " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Write("TASK: " + e.Exception);
            e.SetObserved();
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Write("UI ERROR: " + e.Exception);
        MessageBox.Show(
            "Произошла ошибка:\n\n" + e.Exception.Message,
            "nViel Launcher",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // A downloaded copy installs itself into %APPDATA%\.nviel\app and starts from there.
        if (SelfInstaller.TryInstall(e.Args))
        {
            Shutdown(0);
            return;
        }

        base.OnStartup(e);
    }
}
