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

        var reMode = e.Args.Any(a => string.Equals(a, "--re", StringComparison.OrdinalIgnoreCase));
        var mode = reMode ? "re" : "main";

        // One window per mode: the normal launcher and nVielRE may run side by side,
        // but repeated launches wake the existing window instead of duplicating it.
        if (!SingleInstance.TryAcquire(mode))
        {
            SingleInstance.SignalExisting(mode);
            Shutdown(0);
            return;
        }

        base.OnStartup(e);

        Window window = reMode ? new ReWindow() : new MainWindow();
        MainWindow = window;

        SingleInstance.StartListening(() => Dispatcher.BeginInvoke(() => ActivateWindow(window)));
        window.Show();
    }

    private static void ActivateWindow(Window window)
    {
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;

        window.Activate();
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SingleInstance.Release();
        base.OnExit(e);
    }
}
