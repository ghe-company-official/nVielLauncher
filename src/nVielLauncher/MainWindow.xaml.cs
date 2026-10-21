using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using nVielLauncher.Services;
using nVielLauncher.Views;

namespace nVielLauncher;

public sealed class ProfileRow
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string Letter { get; init; } = "?";
    public Brush Background { get; init; } = Brushes.Transparent;
    public Brush BorderBrush { get; init; } = Brushes.Transparent;
}

public sealed class DiagnosticRow
{
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Suggestion { get; init; } = "";
    public Brush SeverityBrush { get; init; } = Brushes.Gray;
}

public partial class MainWindow : Window
{
    private readonly Dictionary<string, UserControl> _pages = new(StringComparer.Ordinal);
    private readonly List<(string Key, Button Button)> _navButtons = new();
    private readonly ObservableCollection<ProfileRow> _profiles = new();
    private readonly ObservableCollection<DiagnosticRow> _diagnostics = new();
    private string? _currentPage;
    private bool _started;
    private readonly HashSet<string> _promptedUpdateTags = new(StringComparer.OrdinalIgnoreCase);
    private bool _updateDialogOpen;
    private bool _migrationPromptShown;
    private DispatcherTimer? _updateTimer;

    private static readonly (string Title, string Body, string Target)[] TourSteps =
    {
        ("Добро пожаловать в nViel",
            "Лаунчер для Minecraft 1.21.4 на Fabric. Версии, ресурсы и Java скачиваются автоматически — ничего настраивать заранее не нужно. Пройдёмся по разделам.",
            ""),
        ("Профиль игрока",
            "Ты играешь под оффлайн-ником, лицензия не требуется. Сменить ник можно здесь, на главной или в настройках.",
            "profile"),
        ("Запуск игры",
            "Нажми большую кнопку на главной — лаунчер проверит файлы, поставит моды и запустит игру. Прогресс показывается прямо на кнопке.",
            "play"),
        ("Моды",
            "Свои .jar клади в папку mods, либо ищи готовые прямо здесь — на Modrinth. Обязательные зависимости подтянутся сами.",
            "mods"),
        ("Сборки",
            "Новый раздел: готовые сборки Modrinth (.mrpack) ставятся в один клик вместе с конфигами и ресурспаками.",
            "modpacks"),
        ("Сервера",
            "Сохраняй любимые сервера и заходи в один клик — игра сразу подключится к выбранному адресу.",
            "servers"),
        ("Настройки",
            "Память, Java, папка игры, акцентный оттенок и внешний вид. Всё сохраняется автоматически. Приятной игры!",
            "settings"),
    };

    private int _tourIndex;
    private bool _tourActive;
    private BlurEffect? _tourBlur;

    private const int SetupStepCount = 6;
    private int _setupStep;
    private bool _setupActive;
    private bool _setupJavaChecked;
    private double _cursorX;
    private double _cursorY;
    private double _cursorTargetX;
    private double _cursorTargetY;
    private bool _cursorHooked;

    public MainWindow()
    {
        InitializeComponent();

        _navButtons.Add(("home", NavHomeButton));
        _navButtons.Add(("mods", NavModsButton));
        _navButtons.Add(("modpacks", NavModpacksButton));
        _navButtons.Add(("servers", NavServersButton));
        _navButtons.Add(("settings", NavSettingsButton));

        Notify.Raised += OnToast;
        ProfilesList.ItemsSource = _profiles;
        DiagnosticList.ItemsSource = _diagnostics;

        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            _updateTimer?.Stop();
            Log.Write("Лаунчер закрыт.");
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_started) return;
        _started = true;

        StartSplash();
        AnimateWindowIn();
        ApplyAppearance();
        RefreshProfile();

        var settings = AppServices.Settings.Current;
        if (!settings.SetupCompleted)
            ShowSetup();
        else if (string.IsNullOrWhiteSpace(settings.Nickname))
            ShowLoginPage();
        else
            ShowMain();

        await RunSplashAsync();
        if (AppServices.Settings.Current.SetupCompleted)
            await MaybeShowMigrationAsync();

        MaybeShowTour();
        _ = CheckForUpdatesAsync();
        StartUpdateTimer();
    }

    private async Task MaybeShowMigrationAsync()
    {
        var settings = AppServices.Settings.Current;
        if (settings.LauncherMigrationOffered || _migrationPromptShown) return;

        _migrationPromptShown = true;

        IReadOnlyList<DetectedLauncher> found;
        try
        {
            found = await Task.Run(LauncherDiscovery.Discover);
        }
        catch (Exception ex)
        {
            Log.Write("MIGRATION: поиск лаунчеров не удался: " + ex);
            return;
        }

        var actionable = found
            .Where(l => l.DataRoots.Count > 0 || l.CanUninstall)
            .ToList();
        if (actionable.Count == 0) return;

        // Only consume the one-time offer when the window is actually shown.
        // If no supported launcher was found yet, a later launch can offer it.
        settings.LauncherMigrationOffered = true;
        AppServices.Settings.Save();

        Log.Write("MIGRATION: найдено лаунчеров: " + actionable.Count);
        var window = new MigrationWindow(actionable, settings.EffectiveGameDirectory)
        {
            Owner = this
        };
        window.ShowDialog();

        if (window.DataImported)
        {
            Log.Write("MIGRATION: данные импортированы в " + settings.EffectiveGameDirectory);
            (PageHost.Content as IRefreshable)?.Refresh();
        }
    }

    private void StartUpdateTimer()
    {
        if (_updateTimer is not null) return;

        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _updateTimer.Tick += async (_, _) =>
        {
            try
            {
                await CheckForUpdatesAsync(force: true);
            }
            catch (Exception ex)
            {
                Log.Write("UPDATE: периодическая проверка не удалась: " + ex.Message);
            }
        };
        _updateTimer.Start();
    }

    private async Task CheckForUpdatesAsync(bool force = false)
    {
        if (_updateDialogOpen) return;

        try
        {
            var result = await AppServices.Updates.CheckAsync(CancellationToken.None, force);
            if (result.Status != UpdateStatus.Available || result.Update is not { } update) return;
            if (!_promptedUpdateTags.Add(update.Tag)) return;

            _updateDialogOpen = true;
            Log.Write("UPDATE: доступно " + update.Tag + ", открываю окно.");

            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;

            Activate();

            var prompt = new UpdatePromptWindow(update) { Owner = this };
            try
            {
                prompt.ShowDialog();
            }
            finally
            {
                _updateDialogOpen = false;
            }
        }
        catch (Exception ex)
        {
            _updateDialogOpen = false;
            Log.Write("UPDATE: окно обновления не открылось: " + ex);
        }
    }

    private void StartSplash()
    {
        if (SplashRing.RenderTransform is not RotateTransform)
        {
            SplashRing.RenderTransformOrigin = new Point(0.5, 0.5);
            SplashRing.RenderTransform = new RotateTransform();
        }

        if (SplashRing2.RenderTransform is not RotateTransform)
        {
            SplashRing2.RenderTransformOrigin = new Point(0.5, 0.5);
            SplashRing2.RenderTransform = new RotateTransform();
        }

        if (SplashRing.RenderTransform is RotateTransform spin)
            spin.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.4)) { RepeatBehavior = RepeatBehavior.Forever });

        if (SplashRing2.RenderTransform is RotateTransform spin2)
            spin2.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(360, 0, TimeSpan.FromSeconds(22)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    private async Task RunSplashAsync()
    {
        if (SplashLayer.Visibility != Visibility.Visible) return;

        SplashStatus.Text = string.IsNullOrWhiteSpace(AppServices.Settings.Current.Nickname)
            ? "Добро пожаловать"
            : "Профиль " + AppServices.Settings.Current.Nickname;

        var hold = AppServices.Settings.Current.ShowSplash ? 1150 : 0;
        if (hold > 0) await Task.Delay(hold);

        if (AppServices.Settings.Current.AnimationsEnabled)
        {
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(280))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            fade.Completed += (_, _) => { SplashLayer.Visibility = Visibility.Collapsed; SplashLayer.Opacity = 1; };
            SplashLayer.BeginAnimation(OpacityProperty, fade);
        }
        else
        {
            SplashLayer.Visibility = Visibility.Collapsed;
        }
    }

    private void AnimateWindowIn()
    {
        if (!AppServices.Settings.Current.AnimationsEnabled) return;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        RootGrid.Opacity = 0;
        RootScale.ScaleX = 0.985;
        RootScale.ScaleY = 0.985;

        RootGrid.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(330)) { EasingFunction = ease });
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.985, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease });
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.985, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease });
    }

    // ------------------------------------------------------------------ navigation

    public void ShowLoginPage()
    {
        Sidebar.Visibility = Visibility.Collapsed;
        _currentPage = null;
        Navigate("login", animate: true);
    }

    private void ShowMain()
    {
        Sidebar.Visibility = Visibility.Visible;
        Sidebar.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        Navigate("home", animate: false);
    }

    public void CompleteLogin()
    {
        RefreshProfile();
        Sidebar.Visibility = Visibility.Visible;
        Sidebar.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        Navigate("home", animate: true);
        MaybeShowTour();
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string key })
            Navigate(key);
    }

    private void Navigate(string key, bool animate = true)
    {
        if (animate && !AppServices.Settings.Current.AnimationsEnabled) animate = false;

        if (_currentPage == key)
        {
            (PageHost.Content as IRefreshable)?.Refresh();
            return;
        }

        var view = key switch
        {
            "login" => CreateLoginPage(),
            "home" => GetPage("home", () => new HomeView()),
            "mods" => GetPage("mods", () => new ModsView()),
            "modpacks" => GetPage("modpacks", () => new ModpacksView()),
            "servers" => GetPage("servers", () => new ServersView()),
            "settings" => GetPage("settings", () => new SettingsView()),
            _ => null
        };

        if (view is null) return;

        (view as IRefreshable)?.Refresh();

        if (animate)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            view.Opacity = 0;
            var translate = new TranslateTransform(18, 0);
            view.RenderTransform = translate;

            PageHost.Content = view;

            view.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
            translate.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
        }
        else
        {
            PageHost.Content = view;
        }

        _currentPage = key;
        SetActiveNav(key, animate);
    }

    private UserControl CreateLoginPage()
    {
        var login = new LoginView();
        login.Completed += (_, _) => CompleteLogin();
        return login;
    }

    private UserControl GetPage(string key, Func<UserControl> factory)
    {
        if (_pages.TryGetValue(key, out var page)) return page;
        page = factory();
        _pages[key] = page;
        return page;
    }

    private void SetActiveNav(string key, bool animate)
    {
        var index = key switch { "home" => 0, "mods" => 1, "modpacks" => 2, "servers" => 3, "settings" => 4, _ => -1 };

        var activeBrush = (Brush)FindResource("TextPrimaryBrush");
        var idleBrush = (Brush)FindResource("TextSecondaryBrush");

        foreach (var (navKey, button) in _navButtons)
            button.Foreground = navKey == key ? activeBrush : idleBrush;

        if (index < 0) return;

        var target = index * 50d;
        if (animate)
        {
            NavPillTransform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(target, TimeSpan.FromMilliseconds(300))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
        }
        else
        {
            NavPillTransform.BeginAnimation(TranslateTransform.YProperty, null);
            NavPillTransform.Y = target;
        }
    }

    public void RefreshProfile()
    {
        var nickname = AppServices.Settings.Current.Nickname;
        ProfileName.Text = string.IsNullOrWhiteSpace(nickname) ? "Steve" : nickname;
        ProfileLetter.Text = string.IsNullOrWhiteSpace(nickname)
            ? "S"
            : nickname[..1].ToUpperInvariant();
    }

    /// <summary>Switches to Home and immediately launches, joining the given server.</summary>
    public void PlayOnServer(ServerEntry server)
    {
        var settings = AppServices.Settings.Current;
        settings.QuickJoinHost = server.Host;
        settings.QuickJoinPort = server.Port > 0 ? server.Port : 25565;
        server.LastPlayedUtc = DateTime.UtcNow;
        AppServices.Settings.Save();

        Navigate("home", animate: true);
        (PageHost.Content as HomeView)?.BeginLaunch();
    }

    // ------------------------------------------------------------------ profiles

    private void SeedProfiles()
    {
        var settings = AppServices.Settings.Current;
        if (settings.Profiles.Count > 0) return;

        if (!string.IsNullOrWhiteSpace(settings.Nickname))
        {
            var profile = new PlayerProfile
            {
                Name = settings.Nickname,
                Nickname = settings.Nickname
            };
            settings.Profiles.Add(profile);
            settings.ActiveProfileId = profile.Id;
            AppServices.Settings.Save();
        }
    }

    private void ProfileBlock_Click(object sender, RoutedEventArgs e) => OpenProfiles();

    private void OpenProfiles()
    {
        SeedProfiles();
        RefreshProfileList();

        ProfileLayer.Visibility = Visibility.Visible;
        ProfileLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        ProfileCard.RenderTransformOrigin = new Point(0.5, 0.5);
        ProfileCard.RenderTransform = new ScaleTransform(0.96, 0.96);
        if (ProfileCard.RenderTransform is ScaleTransform scale)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        }
    }

    private void CloseProfiles()
    {
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150));
        fade.Completed += (_, _) => ProfileLayer.Visibility = Visibility.Collapsed;
        ProfileLayer.BeginAnimation(OpacityProperty, fade);
    }

    private void RefreshProfileList()
    {
        var settings = AppServices.Settings.Current;
        var inactiveBackground = new SolidColorBrush(Color.FromArgb(0x08, 0xFF, 0xFF, 0xFF));
        var inactiveBorder = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
        var activeBackground = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
        var activeBorder = new SolidColorBrush(Color.FromArgb(0x42, 0xFF, 0xFF, 0xFF));

        _profiles.Clear();
        foreach (var profile in settings.Profiles)
        {
            var isActive = profile.Id == settings.ActiveProfileId ||
                           (settings.ActiveProfileId is null && profile.Nickname == settings.Nickname);
            _profiles.Add(new ProfileRow
            {
                Id = profile.Id,
                Name = string.IsNullOrWhiteSpace(profile.Name) ? profile.Nickname : profile.Name,
                Subtitle = profile.Nickname + (isActive ? "  ·  активный" : ""),
                Letter = string.IsNullOrWhiteSpace(profile.Nickname) ? "?" : profile.Nickname[..1].ToUpperInvariant(),
                Background = isActive ? activeBackground : inactiveBackground,
                BorderBrush = isActive ? activeBorder : inactiveBorder
            });
        }
    }

    private void ProfileRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ProfileRow row }) return;
        SwitchToProfile(row.Id);
    }

    private void SwitchToProfile(string profileId)
    {
        var settings = AppServices.Settings.Current;
        var profile = settings.Profiles.FirstOrDefault(p => p.Id == profileId);
        if (profile is null) return;

        settings.Nickname = profile.Nickname;
        settings.ActiveProfileId = profile.Id;
        AppServices.Settings.Save();

        RefreshProfile();
        RefreshCurrentPage();
        Notify.Show("Профиль переключён", profile.Nickname, "success");
        CloseProfiles();
    }

    private void ProfileAdd_Click(object sender, RoutedEventArgs e)
    {
        var nickname = (ProfileNickBox.Text ?? "").Trim();
        var name = (ProfileNameBox.Text ?? "").Trim();

        if (!System.Text.RegularExpressions.Regex.IsMatch(nickname, "^[A-Za-z0-9_]{3,16}$"))
        {
            ProfileStatus.Text = "Ник: 3–16 символов, только A–Z, 0–9 и _.";
            return;
        }

        if (AppServices.Settings.Current.Profiles.Any(p => string.Equals(p.Nickname, nickname, StringComparison.OrdinalIgnoreCase)))
        {
            ProfileStatus.Text = "Профиль с таким ником уже есть.";
            return;
        }

        var settings = AppServices.Settings.Current;
        var profile = new PlayerProfile
        {
            Name = string.IsNullOrWhiteSpace(name) ? nickname : name,
            Nickname = nickname
        };
        settings.Profiles.Add(profile);
        settings.ActiveProfileId = profile.Id;
        settings.Nickname = nickname;
        AppServices.Settings.Save();

        ProfileNameBox.Text = "";
        ProfileNickBox.Text = "";
        ProfileStatus.Text = "Профиль добавлен и выбран активным.";

        RefreshProfileList();
        RefreshProfile();
        RefreshCurrentPage();
        Notify.Show("Профиль добавлен", nickname, "success");
    }

    private void ProfileDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ProfileRow row }) return;

        var settings = AppServices.Settings.Current;
        settings.Profiles.RemoveAll(p => p.Id == row.Id);

        if (settings.ActiveProfileId == row.Id)
        {
            var next = settings.Profiles.FirstOrDefault();
            settings.ActiveProfileId = next?.Id;
            if (next is not null) settings.Nickname = next.Nickname;
        }

        AppServices.Settings.Save();
        RefreshProfileList();
        Notify.Show("Профиль удалён", row.Name);
    }

    private void ProfileBackdrop_Click(object sender, MouseButtonEventArgs e) => CloseProfiles();

    private void ProfileCard_Click(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void ProfileClose_Click(object sender, RoutedEventArgs e) => CloseProfiles();

    private void RefreshCurrentPage()
    {
        if (PageHost.Content is IRefreshable refreshable)
            refreshable.Refresh();
    }

    // ------------------------------------------------------------------ diagnostics

    /// <summary>Runs crash diagnostics and shows the result overlay. Safe to call at any time.</summary>
    public void ShowDiagnostics()
    {
        var gameDirectory = AppServices.Settings.Current.EffectiveGameDirectory;

        _diagnostics.Clear();
        foreach (var finding in CrashDiagnostics.Analyze(gameDirectory))
        {
            _diagnostics.Add(new DiagnosticRow
            {
                Title = finding.Title,
                Detail = finding.Detail,
                Suggestion = finding.Suggestion,
                SeverityBrush = SeverityBrushFor(finding.Severity)
            });
        }

        var report = CrashDiagnostics.LatestCrashReport(gameDirectory);
        DiagnosticSubtitle.Text = report is null
            ? "Источник: logs/latest.log"
            : "Источник: " + System.IO.Path.GetFileName(report);

        DiagnosticLayer.Visibility = Visibility.Visible;
        DiagnosticLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        DiagnosticCard.RenderTransformOrigin = new Point(0.5, 0.5);
        DiagnosticCard.RenderTransform = new ScaleTransform(0.96, 0.96);
        if (DiagnosticCard.RenderTransform is ScaleTransform scale)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        }
    }

    private static Brush SeverityBrushFor(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Critical => Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B))),
        DiagnosticSeverity.Warning => Freeze(new SolidColorBrush(Color.FromRgb(0xF0, 0xC0, 0x6B))),
        _ => Freeze(new SolidColorBrush(Color.FromRgb(0x8F, 0xB8, 0xE8)))
    };

    private static Brush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    private void CloseDiagnostics()
    {
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150));
        fade.Completed += (_, _) => DiagnosticLayer.Visibility = Visibility.Collapsed;
        DiagnosticLayer.BeginAnimation(OpacityProperty, fade);
    }

    private void DiagnosticBackdrop_Click(object sender, MouseButtonEventArgs e) => CloseDiagnostics();

    private void DiagnosticCard_Click(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void DiagnosticClose_Click(object sender, RoutedEventArgs e) => CloseDiagnostics();

    private void DiagnosticRerun_Click(object sender, RoutedEventArgs e) => ShowDiagnostics();

    private void DiagnosticOpenLogs_Click(object sender, RoutedEventArgs e)
        => ShellHelper.OpenFolder(AppPaths.LogsDirectory);

    // ------------------------------------------------------------------ onboarding tour

    public void StartTour()
    {
        if (_tourActive) return;
        if (_currentPage is null or "login") return;

        _tourActive = true;

        TourLayer.Visibility = Visibility.Visible;

        if (AppServices.Settings.Current.AnimationsEnabled)
        {
            TourLayer.Opacity = 0;
            TourLayer.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
        }
        else
        {
            TourLayer.Opacity = 1;
        }

        if (AppServices.Settings.Current.BlurEnabled)
        {
            _tourBlur = new BlurEffect
            {
                Radius = 0,
                KernelType = KernelType.Gaussian,
                RenderingBias = RenderingBias.Performance
            };
            AppContent.Effect = _tourBlur;
            _tourBlur.BeginAnimation(BlurEffect.RadiusProperty,
                new DoubleAnimation(0, 7, TimeSpan.FromMilliseconds(260))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
        }

        ShowTourStep(0);
    }

    private void MaybeShowTour()
    {
        var settings = AppServices.Settings.Current;
        if (settings.HasSeenTour) return;

        // Never cover the welcome/login screen — the tour targets the main UI.
        if (string.IsNullOrWhiteSpace(settings.Nickname)) return;
        if (_currentPage is null or "login") return;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            UpdateLayout();
            StartTour();
        }));
    }

    private void ShowTourStep(int index)
    {
        _tourIndex = Math.Clamp(index, 0, TourSteps.Length - 1);
        var step = TourSteps[_tourIndex];

        TourTitle.Text = step.Title;
        TourBody.Text = step.Body;
        TourStepLabel.Text = $"Шаг {_tourIndex + 1} из {TourSteps.Length}";
        TourNextButton.Content = _tourIndex == TourSteps.Length - 1 ? "Готово" : "Далее";
        TourBackButton.Visibility = _tourIndex == 0 ? Visibility.Collapsed : Visibility.Visible;
        TourSkipButton.Visibility = _tourIndex == TourSteps.Length - 1 ? Visibility.Collapsed : Visibility.Visible;

        UpdateTourDots();
        UpdateSpotlight(ResolveTourTarget(step.Target));
        AnimateTourCard();
    }

    private void UpdateTourDots()
    {
        TourDotsPanel.Children.Clear();

        for (var i = 0; i < TourSteps.Length; i++)
        {
            var active = i == _tourIndex;
            TourDotsPanel.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = active ? 18 : 6,
                Height = 6,
                Margin = new Thickness(0, 0, 6, 0),
                Fill = active ? (Brush)FindResource("TextPrimaryBrush") : (Brush)FindResource("StrokeStrongBrush"),
                VerticalAlignment = VerticalAlignment.Center
            });
        }
    }

    private void AnimateTourCard()
    {
        if (!AppServices.Settings.Current.AnimationsEnabled) return;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        TourScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.97, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
        TourScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.97, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
    }

    private FrameworkElement? ResolveTourTarget(string key) => key switch
    {
        "profile" => ProfileName,
        "mods" => NavModsButton,
        "modpacks" => NavModpacksButton,
        "servers" => NavServersButton,
        "settings" => NavSettingsButton,
        "play" => (PageHost.Content as FrameworkElement)?.FindName("PlayButton") as FrameworkElement,
        _ => null
    };

    private void UpdateSpotlight(FrameworkElement? target)
    {
        var width = RootGrid.ActualWidth;
        var height = RootGrid.ActualHeight;
        if (width <= 1 || height <= 1) return;

        var full = new RectangleGeometry(new Rect(0, 0, width, height));
        Rect hole = default;
        var hasHole = false;

        if (target is not null && target.IsVisible && target.ActualWidth > 0 && target.ActualHeight > 0)
        {
            try
            {
                hole = target.TransformToAncestor(RootGrid).TransformBounds(new Rect(target.RenderSize));
                hole.Inflate(10, 10);
                hasHole = true;
            }
            catch
            {
                hasHole = false;
            }
        }

        if (hasHole)
        {
            TourSpotlight.Data = new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(hole, 16, 16));

            Canvas.SetLeft(TourGlow, hole.X);
            Canvas.SetTop(TourGlow, hole.Y);
            TourGlow.Width = hole.Width;
            TourGlow.Height = hole.Height;

            PositionCard(hole, width, height);
        }
        else
        {
            TourSpotlight.Data = full;
            TourGlow.Width = 0;
            TourGlow.Height = 0;
            PositionCard(null, width, height);
        }
    }

    private void PositionCard(Rect? hole, double width, double height)
    {
        TourCard.Measure(new Size(TourCard.Width, double.PositiveInfinity));
        var cardW = TourCard.Width;
        var cardH = TourCard.DesiredSize.Height;

        const double gap = 28;
        const double pad = 24;

        double x, y;

        if (hole is { } h)
        {
            if (h.Right + gap + cardW <= width - pad)
                x = h.Right + gap;
            else if (h.Left - gap - cardW >= pad)
                x = h.Left - gap - cardW;
            else
                x = Math.Clamp(h.Left, pad, Math.Max(pad, width - cardW - pad));

            y = Math.Clamp(h.Top + h.Height / 2 - cardH / 2, pad, Math.Max(pad, height - cardH - pad));
        }
        else
        {
            x = (width - cardW) / 2;
            y = (height - cardH) / 2;
        }

        TourTranslate.X = x + cardW / 2 - width / 2;
        TourTranslate.Y = y + cardH / 2 - height / 2;
    }

    private void TourNext_Click(object sender, RoutedEventArgs e)
    {
        if (_tourIndex >= TourSteps.Length - 1)
            EndTour();
        else
            ShowTourStep(_tourIndex + 1);
    }

    private void TourBack_Click(object sender, RoutedEventArgs e) => ShowTourStep(_tourIndex - 1);

    private void TourSkip_Click(object sender, RoutedEventArgs e) => EndTour();

    private void Help_Click(object sender, RoutedEventArgs e) => StartTour();

    private void EndTour()
    {
        _tourActive = false;

        var settings = AppServices.Settings.Current;
        if (!settings.HasSeenTour)
        {
            settings.HasSeenTour = true;
            AppServices.Settings.Save();
        }

        if (settings.AnimationsEnabled)
        {
            var fade = new DoubleAnimation(TourLayer.Opacity, 0, TimeSpan.FromMilliseconds(170))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            fade.Completed += (_, _) => FinishTourHide();
            TourLayer.BeginAnimation(OpacityProperty, fade);
        }
        else
        {
            FinishTourHide();
        }

        if (_tourBlur is not null)
        {
            var blurOut = new DoubleAnimation(_tourBlur.Radius, 0, TimeSpan.FromMilliseconds(170));
            blurOut.Completed += (_, _) => { AppContent.Effect = null; _tourBlur = null; };
            _tourBlur.BeginAnimation(BlurEffect.RadiusProperty, blurOut);
        }
    }

    private void FinishTourHide()
    {
        TourLayer.BeginAnimation(OpacityProperty, null);
        TourLayer.Opacity = 0;
        TourLayer.Visibility = Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ first-run setup

    public void ShowSetup()
    {
        _setupActive = true;
        var settings = AppServices.Settings.Current;

        SetupLayer.Visibility = Visibility.Visible;
        SetupLayer.Opacity = 1;

        SetupNickBox.Text = settings.Nickname;
        ResetSetupNickHint();

        var total = SystemMonitor.TotalPhysicalMemoryMb;
        var maxRam = (int)Math.Clamp(total > 0 ? total : 8192, 4096, 32768);
        SetupRamSlider.Minimum = 2048;
        SetupRamSlider.Maximum = Math.Max(4096, maxRam);
        SetupRamSlider.Value = Math.Clamp(settings.RamMb, 2048, (int)SetupRamSlider.Maximum);

        SetupGlowToggle.IsChecked = settings.BackgroundGlow;
        SetupSplashToggle.IsChecked = settings.ShowSplash;
        HighlightSetupAccent(settings.AccentColor);

        var width = ActualWidth > 1 ? ActualWidth : Width;
        var height = ActualHeight > 1 ? ActualHeight : Height;
        _cursorX = _cursorTargetX = width / 2;
        _cursorY = _cursorTargetY = height / 2;

        HookSetupCursor();
        StartSetupAmbience();
        ShowSetupStep(0, animate: false);
        UpdateSetupRamText();

        _ = DetectSetupJavaAsync(silent: true);
    }

    private void HookSetupCursor()
    {
        if (_cursorHooked) return;
        _cursorHooked = true;
        CompositionTarget.Rendering += SetupCursorTick;
    }

    private void SetupCursorTick(object? sender, EventArgs e)
    {
        if (!_setupActive) return;

        _cursorX += (_cursorTargetX - _cursorX) * 0.14;
        _cursorY += (_cursorTargetY - _cursorY) * 0.14;

        SetupCursorTransform.X = _cursorX - SetupCursorGlow.Width / 2;
        SetupCursorTransform.Y = _cursorY - SetupCursorGlow.Height / 2;

        var width = ActualWidth > 1 ? ActualWidth : Width;
        var height = ActualHeight > 1 ? ActualHeight : Height;
        SetupParallax.X = (_cursorX - width / 2) * -0.016;
        SetupParallax.Y = (_cursorY - height / 2) * -0.016;
    }

    private void UnhookSetupCursor()
    {
        if (!_cursorHooked) return;
        CompositionTarget.Rendering -= SetupCursorTick;
        _cursorHooked = false;
    }

    private void SetupLayer_MouseMove(object sender, MouseEventArgs e)
    {
        var point = e.GetPosition(SetupLayer);
        _cursorTargetX = point.X;
        _cursorTargetY = point.Y;

        if (SetupCursorGlow.Opacity < 0.99)
            SetupCursorGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(280)));
    }

    private void SetupLayer_MouseLeave(object sender, MouseEventArgs e)
        => SetupCursorGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)));

    private void StartSetupAmbience()
    {
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };

        SetupBlob1.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(-30, 130, TimeSpan.FromSeconds(19)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
        SetupBlob1.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(-20, 90, TimeSpan.FromSeconds(15)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
        SetupBlob2.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(40, -120, TimeSpan.FromSeconds(23)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
        SetupBlob2.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(20, -80, TimeSpan.FromSeconds(17)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
        SetupBlob3.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(-60, 60, TimeSpan.FromSeconds(26)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });

        SetupShimmerTransform.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(-820, 820, TimeSpan.FromSeconds(7.5))
            {
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            });

        SetupRing1.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(46)) { RepeatBehavior = RepeatBehavior.Forever });
        SetupRing2.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(360, 0, TimeSpan.FromSeconds(64)) { RepeatBehavior = RepeatBehavior.Forever });
        SetupRing3.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(84)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    private void ShowSetupStep(int index, bool animate = true)
    {
        _setupStep = Math.Clamp(index, 0, SetupStepCount - 1);

        var panels = new[] { SetupStep0, SetupStep1, SetupStep2, SetupStep3, SetupStep4, SetupStep5 };
        for (var i = 0; i < panels.Length; i++)
            panels[i].Visibility = i == _setupStep ? Visibility.Visible : Visibility.Collapsed;

        var panel = panels[_setupStep];

        if (animate && AppServices.Settings.Current.AnimationsEnabled)
        {
            panel.Opacity = 0;
            var translate = new TranslateTransform(22, 0);
            panel.RenderTransform = translate;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
            translate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(22, 0, TimeSpan.FromMilliseconds(340)) { EasingFunction = ease });
        }
        else
        {
            panel.Opacity = 1;
        }

        UpdateSetupRail();

        SetupBackButton.Visibility = _setupStep == 0 ? Visibility.Collapsed : Visibility.Visible;
        SetupNextButton.Content = _setupStep switch
        {
            0 => "Начать",
            SetupStepCount - 1 => "Запустить nViel",
            _ => "Далее"
        };

        var titles = new[] { "Знакомство", "Профиль", "Память", "Java", "Внешний вид", "Готово" };
        SetupStepLabel.Text = _setupStep == 0 ? titles[0] : $"Шаг {_setupStep} из 5 · {titles[_setupStep]}";
        SetupHint.Text = _setupStep == SetupStepCount - 1
            ? "Нажми «Запустить nViel» — остальное лаунчер сделает сам"
            : "Можно всё изменить позже в настройках";

        if (_setupStep == 1)
        {
            SetupNickBox.Focus();
            SetupNickBox.CaretIndex = SetupNickBox.Text.Length;
        }
        else if (_setupStep == 2)
        {
            UpdateSetupRamText();
        }
        else if (_setupStep == 3 && !_setupJavaChecked)
        {
            _ = DetectSetupJavaAsync(silent: false);
        }
        else if (_setupStep == 4)
        {
            HighlightSetupAccent(AppServices.Settings.Current.AccentColor);
        }
        else if (_setupStep == SetupStepCount - 1)
        {
            UpdateSetupSummary();
        }
    }

    private void UpdateSetupRail()
    {
        var dots = new[] { SetupRailDot0, SetupRailDot1, SetupRailDot2, SetupRailDot3, SetupRailDot4 };
        var labels = new[] { SetupRailText0, SetupRailText1, SetupRailText2, SetupRailText3, SetupRailText4 };

        var primary = (Brush)FindResource("TextPrimaryBrush");
        var muted = (Brush)FindResource("TextMutedBrush");
        var accent = (Brush)FindResource("AccentBrush");
        var idle = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x33));

        for (var i = 0; i < dots.Length; i++)
        {
            var stepNumber = i + 1;
            var isCurrent = _setupStep == stepNumber;
            var isDone = _setupStep > stepNumber;

            dots[i].Background = isCurrent || isDone ? accent : idle;
            dots[i].Opacity = isCurrent ? 1 : isDone ? 0.55 : 1;
            labels[i].Foreground = isCurrent || isDone ? primary : muted;
            labels[i].Opacity = isCurrent ? 1 : isDone ? 0.6 : 1;
        }
    }

    private void SetupBack_Click(object sender, RoutedEventArgs e)
    {
        if (_setupStep > 0) ShowSetupStep(_setupStep - 1);
    }

    private void SetupNext_Click(object sender, RoutedEventArgs e)
    {
        if (_setupStep == SetupStepCount - 1)
        {
            SetupFinish();
            return;
        }

        if (!ValidateSetupStep()) return;
        ShowSetupStep(_setupStep + 1);
    }

    private bool ValidateSetupStep()
    {
        if (_setupStep != 1) return true;

        var nickname = (SetupNickBox.Text ?? "").Trim();
        if (LauncherService.IsValidNickname(nickname))
        {
            SetupNickError.Text = "Отлично — этот ник будет использоваться в игре";
            SetupNickError.Foreground = (Brush)FindResource("TextMutedBrush");
            return true;
        }

        SetupNickError.Text = "Ник: 3–16 символов, только A–Z, 0–9 и _";
        SetupNickError.Foreground = (Brush)FindResource("TextPrimaryBrush");
        return false;
    }

    private void ResetSetupNickHint()
    {
        SetupNickError.Text = "3–16 символов: A–Z, 0–9 и _";
        SetupNickError.Foreground = (Brush)FindResource("TextMutedBrush");
    }

    private void SetupNickBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SetupNickError is not null) ResetSetupNickHint();
    }

    private void SetupNickBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SetupNext_Click(sender, e);
    }

    private void SetupRamSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SetupRamValue is not null) UpdateSetupRamText();
    }

    private void UpdateSetupRamText()
    {
        if (SetupRamValue is null || SetupRamChip is null || SetupRamHint is null || SetupRamSlider is null) return;

        var value = (int)SetupRamSlider.Value;
        var total = SystemMonitor.TotalPhysicalMemoryMb;
        var recommended = total > 0 ? Math.Clamp(total / 4, 4096, 8192) : 4096;

        SetupRamValue.Text = $"{value} МБ";
        SetupRamChip.Text = value + 512 < recommended
            ? "можно больше"
            : value > recommended + 1024 ? "с запасом" : "рекомендуется";
        SetupRamHint.Text = total > 0
            ? $"Всего в системе: {total} МБ ({total / 1024.0:0.0} ГБ). Рекомендуем около {recommended} МБ."
            : $"Рекомендуем около {recommended} МБ.";
    }

    private async Task DetectSetupJavaAsync(bool silent)
    {
        _setupJavaChecked = true;

        SetupJavaDot.Fill = new SolidColorBrush(Color.FromRgb(0x56, 0x56, 0x60));
        SetupJavaStatus.Text = silent ? "Проверяем…" : "Ищем Java 21…";
        SetupJavaInfo.Text = "Сканируем систему";

        try
        {
            var info = await Task.Run(() => AppServices.Java.DetectAsync(21, CancellationToken.None));

            if (info is null)
            {
                SetupJavaStatus.Text = "Java не найдена";
                SetupJavaInfo.Text = "Не страшно — Temurin 21 скачается автоматически при первом запуске игры.";
                SetupSummaryJava.Text = "скачается автоматически";
                return;
            }

            AppServices.Settings.Current.JavaPath = info.JavaExe;
            AppServices.Settings.Save();

            SetupJavaDot.Fill = new SolidColorBrush(Color.FromRgb(0x7B, 0xE3, 0xA0));
            SetupJavaStatus.Text = $"Java {info.Major} — готова";
            SetupJavaInfo.Text = info.VersionString;
            SetupSummaryJava.Text = $"Java {info.Major}";
        }
        catch (Exception ex)
        {
            SetupJavaStatus.Text = "Не удалось проверить Java";
            SetupJavaInfo.Text = ex.Message;
            SetupSummaryJava.Text = "проверим при запуске";
        }
    }

    private async void SetupJavaRetry_Click(object sender, RoutedEventArgs e)
    {
        _setupJavaChecked = false;
        await DetectSetupJavaAsync(silent: false);
    }

    private void SetupAccent_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string hex }) return;

        AppServices.Settings.Current.AccentColor = hex;
        HighlightSetupAccent(hex);
        ApplyAppearance();
    }

    private void HighlightSetupAccent(string color)
    {
        var selected = new SolidColorBrush(Color.FromArgb(0xDD, 0xFF, 0xFF, 0xFF));
        var idle = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));

        foreach (var swatch in new[] { SetupAccent0, SetupAccent1, SetupAccent2, SetupAccent3, SetupAccent4 })
        {
            var isActive = string.Equals(swatch.Tag as string, color, StringComparison.OrdinalIgnoreCase);
            swatch.BorderThickness = new Thickness(isActive ? 3 : 1);
            swatch.BorderBrush = isActive ? selected : idle;
        }
    }

    private void UpdateSetupSummary()
    {
        var nickname = (SetupNickBox.Text ?? "").Trim();
        SetupSummaryNick.Text = string.IsNullOrWhiteSpace(nickname) ? "Steve" : nickname;
        SetupSummaryRam.Text = $"{(int)SetupRamSlider.Value} МБ";

        if (string.IsNullOrWhiteSpace(AppServices.Settings.Current.JavaPath))
            SetupSummaryJava.Text = "скачается автоматически";
    }

    private async void SetupFinish()
    {
        var settings = AppServices.Settings.Current;
        var nickname = (SetupNickBox.Text ?? "").Trim();

        if (!LauncherService.IsValidNickname(nickname))
        {
            ShowSetupStep(1);
            ValidateSetupStep();
            return;
        }

        settings.Nickname = nickname;
        settings.RamMb = (int)SetupRamSlider.Value;
        settings.BackgroundGlow = SetupGlowToggle.IsChecked == true;
        settings.ShowSplash = SetupSplashToggle.IsChecked == true;
        settings.SetupCompleted = true;

        settings.Profiles.Clear();
        var profile = new PlayerProfile { Name = nickname, Nickname = nickname };
        settings.Profiles.Add(profile);
        settings.ActiveProfileId = profile.Id;

        AppServices.Settings.Save();

        ApplyAppearance();
        RefreshProfile();

        _setupActive = false;
        UnhookSetupCursor();
        SetupParallax.X = 0;
        SetupParallax.Y = 0;
        HideSetup();

        ShowMain();
        await MaybeShowMigrationAsync();
        MaybeShowTour();
    }

    private void HideSetup()
    {
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(380))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        fade.Completed += (_, _) =>
        {
            SetupLayer.Visibility = Visibility.Collapsed;
            SetupLayer.Opacity = 1;
        };
        SetupLayer.BeginAnimation(OpacityProperty, fade);
    }

    private void SetupClose_Click(object sender, RoutedEventArgs e) => Close();

    private void SetupDragArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    // ------------------------------------------------------------------ toasts

    private void OnToast(ToastMessage message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnToast(message));
            return;
        }

        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 7,
            Height = 7,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = message.Kind switch
            {
                "error" => new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)),
                "success" => new SolidColorBrush(Color.FromRgb(0x7B, 0xE3, 0xA0)),
                _ => (Brush)FindResource("AccentBrush")
            }
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var dotHost = new Border { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 0, 0), Child = dot };
        Grid.SetColumn(dotHost, 0);

        var stack = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        stack.Children.Add(new TextBlock
        {
            Text = message.Title, FontSize = 12.5, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        stack.Children.Add(new TextBlock
        {
            Text = message.Text, FontSize = 11, Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("TextSecondaryBrush")
        });
        Grid.SetColumn(stack, 1);

        grid.Children.Add(dotHost);
        grid.Children.Add(stack);

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x0E, 0x0E, 0x13)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, 10),
            Child = grid,
            Opacity = 0
        };

        var translate = new TranslateTransform(26, 0);
        border.RenderTransform = translate;
        ToastPanel.Children.Insert(0, border);

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        border.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
        translate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(26, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4.5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var fade = new DoubleAnimation(border.Opacity, 0, TimeSpan.FromMilliseconds(220));
            fade.Completed += (_, _) => ToastPanel.Children.Remove(border);
            border.BeginAnimation(OpacityProperty, fade);
        };
        timer.Start();
    }

    // ------------------------------------------------------------------ appearance

    public void ApplyAppearance()
    {
        var settings = AppServices.Settings.Current;

        if (TryParseColor(settings.AccentColor, out var accent))
            Application.Current.Resources["AccentBrush"] = new SolidColorBrush(accent);

        BackdropLayer.Visibility = settings.BackgroundGlow ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool TryParseColor(string text, out Color color)
    {
        color = Colors.White;
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            color = (Color)ColorConverter.ConvertFromString(text);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ chrome

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;

        try
        {
            DragMove();
        }
        catch
        {
            // ignored
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseIn };
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease };
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);

        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, 0.985, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, 0.985, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
    }

    private void ChangeNick_Click(object sender, RoutedEventArgs e) => OpenProfiles();

    private void OpenRe_Click(object sender, RoutedEventArgs e) => ReLauncher.OpenRe();

    /// <summary>Opens the player-profile picker (used by Home/Settings shortcuts).</summary>
    public void ShowProfiles() => OpenProfiles();
}
