using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using nVielLauncher.Services;

namespace nVielLauncher.Views;

public sealed class ServerRow
{
    public string Name { get; init; } = "";
    public string Address { get; init; } = "";
    public string Meta { get; init; } = "";
    public ServerEntry Entry { get; init; } = new();
}

public partial class ServersView : UserControl, IRefreshable
{
    private readonly ObservableCollection<ServerRow> _rows = new();
    private Storyboard? _ambientStoryboard;
    private bool _ambientStarted;

    public ServersView()
    {
        InitializeComponent();
        ServersList.ItemsSource = _rows;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Refresh();
        SyncAmbientAnimations();
        AnimateEntrance();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _ambientStoryboard?.Stop(this);
        _ambientStoryboard = null;
        _ambientStarted = false;
    }

    private void SyncAmbientAnimations()
    {
        if (!AppServices.Settings.Current.AnimationsEnabled)
        {
            _ambientStoryboard?.Stop(this);
            _ambientStoryboard = null;
            _ambientStarted = false;
            return;
        }

        if (_ambientStarted) return;
        _ambientStarted = true;
        if (Resources["ServersAmbient"] is Storyboard storyboard)
        {
            _ambientStoryboard = storyboard;
            storyboard.Begin(this, true);
        }
    }

    private void AnimateEntrance()
    {
        if (!AppServices.Settings.Current.AnimationsEnabled)
        {
            ServersHeroCard.Opacity = 1;
            ServersHeroCard.RenderTransform = Transform.Identity;
            return;
        }

        ServersHeroCard.Opacity = 0;
        ServersHeroCard.RenderTransform = new TranslateTransform(0, 8);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ServersHeroCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = ease
        });
        ((TranslateTransform)ServersHeroCard.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
    }

    public void Refresh()
    {
        _rows.Clear();

        var settings = AppServices.Settings.Current;
        var instance = settings.ActiveInstance ?? settings.Instances.FirstOrDefault(i => i.IsDefault);
        var servers = instance?.Servers ?? settings.Servers;

        foreach (var server in servers)
        {
            var meta = server.LastPlayedUtc is { } last
                ? "был " + last.ToLocalTime().ToString("dd.MM HH:mm")
                : "ещё не заходил";

            _rows.Add(new ServerRow
            {
                Name = string.IsNullOrWhiteSpace(server.Name) ? server.Host : server.Name,
                Address = server.Address,
                Meta = meta,
                Entry = server
            });
        }

        CountBadge.Text = _rows.Count + " " + Plural(_rows.Count, "сервер", "сервера", "серверов");
        EmptyState.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SubHeader.Text = instance is null
            ? "Сохрани любимые серверы и заходи в один клик"
            : _rows.Count == 0
                ? $"Список принадлежит инстансу «{instance.Name}»"
                : $"{_rows.Count} {Plural(_rows.Count, "сервер", "сервера", "серверов")} · инстанс «{instance.Name}»";
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var host = (HostBox.Text ?? "").Trim();
        var portText = (PortBox.Text ?? "").Trim();

        if (string.IsNullOrWhiteSpace(host))
        {
            AddStatus.Text = "Укажи адрес сервера.";
            return;
        }

        // allow "host:port" as well
        var port = 25565;
        var colon = host.LastIndexOf(':');
        if (colon > 0 && int.TryParse(host[(colon + 1)..], out var inlinePort))
        {
            port = inlinePort;
            host = host[..colon];
        }

        if (int.TryParse(portText, out var explicitPort) && explicitPort > 0 && explicitPort <= 65535)
            port = explicitPort;

        host = host.Replace("minecraft://", "", StringComparison.OrdinalIgnoreCase)
                   .Replace("mc://", "", StringComparison.OrdinalIgnoreCase)
                   .TrimEnd('/');

        if (string.IsNullOrWhiteSpace(host))
        {
            AddStatus.Text = "Некорректный адрес.";
            return;
        }

        var settings = AppServices.Settings.Current;
        var instance = settings.ActiveInstance
                       ?? settings.Instances.FirstOrDefault(i => i.IsDefault);
        if (instance is null)
        {
            AddStatus.Text = "Сначала выбери инстанс.";
            return;
        }

        instance.Servers.Add(new ServerEntry
        {
            Name = string.IsNullOrWhiteSpace(NameBox.Text) ? host : NameBox.Text!.Trim(),
            Host = host,
            Port = port
        });
        AppServices.Settings.Save();
        NameBox.Text = "";
        HostBox.Text = "";
        PortBox.Text = "";
        AddStatus.Text = "Адрес можно вставлять как «host» или «host:port»";

        Refresh();
        Notify.Show("Сервер добавлен", host + ":" + port, "success");
    }

    private static string Plural(int count, string one, string few, string many)
    {
        var mod100 = Math.Abs(count) % 100;
        var mod10 = mod100 % 10;
        if (mod100 is >= 11 and <= 14) return many;
        return mod10 switch
        {
            1 => one,
            2 or 3 or 4 => few,
            _ => many
        };
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ServerRow row }) return;
        (Window.GetWindow(this) as MainWindow)?.PlayOnServer(row.Entry);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ServerRow row }) return;

        var settings = AppServices.Settings.Current;
        var instance = settings.ActiveInstance
                       ?? settings.Instances.FirstOrDefault(i => i.IsDefault);
        instance?.Servers.RemoveAll(s => ReferenceEquals(s, row.Entry));
        AppServices.Settings.Save();
        Refresh();
        Notify.Show("Сервер удалён", row.Name);
    }
}
