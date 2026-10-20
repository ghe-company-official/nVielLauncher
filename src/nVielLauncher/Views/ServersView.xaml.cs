using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
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

    public ServersView()
    {
        InitializeComponent();
        ServersList.ItemsSource = _rows;
    }

    public void Refresh()
    {
        _rows.Clear();

        foreach (var server in AppServices.Settings.Current.Servers)
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

        CountBadge.Text = _rows.Count.ToString();
        EmptyState.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SubHeader.Text = _rows.Count == 0
            ? "Сохрани любимые сервера и заходи в один клик"
            : $"{_rows.Count} сервер(ов) сохранено";
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
        settings.Servers.Add(new ServerEntry
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

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ServerRow row }) return;
        (Window.GetWindow(this) as MainWindow)?.PlayOnServer(row.Entry);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ServerRow row }) return;

        AppServices.Settings.Current.Servers.RemoveAll(s => ReferenceEquals(s, row.Entry));
        AppServices.Settings.Save();
        Refresh();
        Notify.Show("Сервер удалён", row.Name);
    }
}
