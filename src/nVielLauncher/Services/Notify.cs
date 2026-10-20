namespace nVielLauncher.Services;

/// <summary>Lightweight, thread-safe notification bus rendered by the main window as toasts.</summary>
public sealed record ToastMessage(string Title, string Text, string Kind = "info");

public static class Notify
{
    public static event Action<ToastMessage>? Raised;

    public static void Show(string title, string text, string kind = "info")
    {
        try
        {
            Raised?.Invoke(new ToastMessage(title, text, kind));
        }
        catch
        {
            // notifications must never break a flow
        }
    }
}
