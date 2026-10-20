namespace nVielLauncher.Services;

/// <summary>
/// Keeps one launcher process per mode (normal / nVielRE). A second launch wakes the
/// already running window instead of creating a duplicate.
/// </summary>
public static class SingleInstance
{
    private const string Prefix = "nVielLauncher.Instance.";

    private static Mutex? _mutex;
    private static EventWaitHandle? _wake;
    private static Thread? _listener;
    private static string _mode = "main";

    /// <summary>Returns false when another instance of the same mode already owns the mutex.</summary>
    public static bool TryAcquire(string mode)
    {
        _mode = mode;

        try
        {
            var mutex = new Mutex(initiallyOwned: true, Prefix + mode, out var createdNew);
            if (!createdNew)
            {
                mutex.Dispose();
                return false;
            }

            _mutex = mutex;
            return true;
        }
        catch
        {
            // If the mutex cannot be created (unlikely on Windows) just continue without it.
            return true;
        }
    }

    /// <summary>Starts the background thread that brings the window to front on a repeated launch.</summary>
    public static void StartListening(Action onActivate)
    {
        try
        {
            _wake = new EventWaitHandle(false, EventResetMode.AutoReset, Prefix + _mode + ".wake");

            _listener = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        _wake.WaitOne();
                    }
                    catch
                    {
                        return;
                    }

                    try
                    {
                        onActivate();
                    }
                    catch
                    {
                        // a failed activation must never kill the listener
                    }
                }
            })
            {
                IsBackground = true,
                Name = "nViel single-instance listener"
            };

            _listener.Start();
        }
        catch
        {
            // ignored — duplicate windows are not critical
        }
    }

    /// <summary>Signals the first instance to show itself. Retries while it is still starting.</summary>
    public static void SignalExisting(string mode)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            try
            {
                using var wake = EventWaitHandle.OpenExisting(Prefix + mode + ".wake");
                wake.Set();
                return;
            }
            catch
            {
                Thread.Sleep(120);
            }
        }
    }

    public static void Release()
    {
        try
        {
            _wake?.Dispose();
            _wake = null;

            if (_mutex is not null)
            {
                try { _mutex.ReleaseMutex(); } catch { }
                _mutex.Dispose();
                _mutex = null;
            }
        }
        catch
        {
            // process exit releases the kernel mutex anyway
        }
    }
}
