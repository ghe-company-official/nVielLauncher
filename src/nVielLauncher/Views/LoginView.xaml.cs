using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using nVielLauncher.Services;

namespace nVielLauncher.Views;

public partial class LoginView : UserControl
{
    public event EventHandler? Completed;

    public LoginView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        NickBox.Text = AppServices.Settings.Current.Nickname;

        AnimateIn(BrandBlock, 0);
        AnimateIn(LoginCard, 120);

        NickBox.Focus();
        NickBox.CaretIndex = NickBox.Text.Length;
    }

    private static void AnimateIn(UIElement element, int delayMs)
    {
        element.Opacity = 0;
        var translate = new TranslateTransform(0, 16);
        element.RenderTransform = translate;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var begin = TimeSpan.FromMilliseconds(delayMs);

        element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280))
        {
            BeginTime = begin,
            EasingFunction = ease
        });

        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(16, 0, TimeSpan.FromMilliseconds(360))
        {
            BeginTime = begin,
            EasingFunction = ease
        });
    }

    private void NickBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Submit();
    }

    private void Login_Click(object sender, RoutedEventArgs e) => Submit();

    private void Submit()
    {
        var nickname = NickBox.Text.Trim();

        if (!LauncherService.IsValidNickname(nickname))
        {
            ShowError("Ник: от 3 до 16 символов, только A-Z, 0-9 и _");
            return;
        }

        HideError();

        var settings = AppServices.Settings.Current;
        settings.Nickname = nickname;
        AppServices.Settings.Save();

        Log.Write("Профиль сохранён: " + nickname);
        Completed?.Invoke(this, EventArgs.Empty);
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;

        var shake = new DoubleAnimationUsingKeyFrames();
        shake.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        shake.KeyFrames.Add(new EasingDoubleKeyFrame(-7, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(60))));
        shake.KeyFrames.Add(new EasingDoubleKeyFrame(6, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120))));
        shake.KeyFrames.Add(new EasingDoubleKeyFrame(-4, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180))));
        shake.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(240))));

        var transform = new TranslateTransform();
        LoginCard.RenderTransform = transform;
        transform.BeginAnimation(TranslateTransform.XProperty, shake);
    }

    private void HideError() => ErrorText.Visibility = Visibility.Collapsed;
}
