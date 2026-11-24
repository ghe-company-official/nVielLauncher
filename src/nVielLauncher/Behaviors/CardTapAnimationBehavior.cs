using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using nVielLauncher.Services;

namespace nVielLauncher.Behaviors;

public static class CardTapAnimationBehavior
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(CardTapAnimationBehavior),
        new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly DependencyProperty DentProperty = DependencyProperty.RegisterAttached(
        "Dent", typeof(DepressionAdorner), typeof(CardTapAnimationBehavior));

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    private static void OnIsEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not Border border) return;

        if ((bool)e.NewValue)
        {
            border.AddHandler(UIElement.MouseLeftButtonDownEvent, new MouseButtonEventHandler(OnMouseLeftButtonDown), true);
            border.AddHandler(UIElement.MouseLeftButtonUpEvent, new MouseButtonEventHandler(OnMouseLeftButtonUp), true);
            border.AddHandler(UIElement.MouseLeaveEvent, new MouseEventHandler(OnMouseLeave), true);
            border.AddHandler(UIElement.LostMouseCaptureEvent, new MouseEventHandler(OnLostMouseCapture), true);
            border.Unloaded += OnUnloaded;
        }
        else
        {
            border.RemoveHandler(UIElement.MouseLeftButtonDownEvent, new MouseButtonEventHandler(OnMouseLeftButtonDown));
            border.RemoveHandler(UIElement.MouseLeftButtonUpEvent, new MouseButtonEventHandler(OnMouseLeftButtonUp));
            border.RemoveHandler(UIElement.MouseLeaveEvent, new MouseEventHandler(OnMouseLeave));
            border.RemoveHandler(UIElement.LostMouseCaptureEvent, new MouseEventHandler(OnLostMouseCapture));
            border.Unloaded -= OnUnloaded;
        }
    }

    private static void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border || e.ChangedButton != MouseButton.Left ||
            !AppServices.Settings.Current.AnimationsEnabled || IsInteractiveSource(e.OriginalSource, border) ||
            !IsNearestAnimatedCard(e.OriginalSource, border))
            return;

        var layer = AdornerLayer.GetAdornerLayer(border);
        if (layer is null) return;

        RemoveDent(border);

        var adorner = new DepressionAdorner(border, e.GetPosition(border));
        layer.Add(adorner);
        border.SetValue(DentProperty, adorner);
        adorner.Press();
        StartFallbackRelease(border, adorner);
    }

    // Safety net: the dent must never stay behind if the matching mouse-up is lost.
    private static void StartFallbackRelease(Border border, DepressionAdorner adorner)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, border.Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(3.5)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (ReferenceEquals(border.GetValue(DentProperty), adorner))
                ReleaseDent(border);
        };
        timer.Start();
    }

    private static void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border) ReleaseDent(border);
    }

    private static void OnMouseLeave(object sender, MouseEventArgs e)
    {
        // Dragging the pointer off the card cancels the dent, so it never lingers.
        if (sender is Border border && e.LeftButton == MouseButtonState.Pressed)
            ReleaseDent(border);
    }

    private static void OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (sender is Border border) ReleaseDent(border);
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is Border border) ReleaseDent(border, immediate: true);
    }

    private static void ReleaseDent(Border border, bool immediate = false)
    {
        if (border.GetValue(DentProperty) is not DepressionAdorner adorner) return;

        border.ClearValue(DentProperty);
        if (immediate)
        {
            adorner.Remove();
            return;
        }

        adorner.Release(adorner.Remove);
    }

    private static void RemoveDent(Border border)
    {
        if (border.GetValue(DentProperty) is not DepressionAdorner previous) return;

        border.ClearValue(DentProperty);
        previous.Remove();
    }

    private static bool IsNearestAnimatedCard(object source, Border target)
    {
        DependencyObject? current = source as DependencyObject;
        if (current is null) return false;

        while (current is not null)
        {
            if (current is Border border && GetIsEnabled(border))
                return ReferenceEquals(border, target);

            current = GetParent(current);
        }

        return false;
    }

    private static bool IsInteractiveSource(object source, Border target)
    {
        DependencyObject? current = source as DependencyObject;
        if (current is null) return false;

        while (current is not null && !ReferenceEquals(current, target))
        {
            if (current is ButtonBase or TextBoxBase or Selector or RangeBase)
                return true;

            current = GetParent(current);
        }

        return false;
    }

    private static DependencyObject? GetParent(DependencyObject element)
    {
        if (element is Visual or System.Windows.Media.Media3D.Visual3D)
        {
            var parent = VisualTreeHelper.GetParent(element);
            if (parent is not null) return parent;
        }

        return LogicalTreeHelper.GetParent(element);
    }

    /// <summary>
    /// Soft concave dent drawn over the card at the point of the pointer press.
    /// </summary>
    private sealed class DepressionAdorner : Adorner
    {
        private readonly VisualCollection _visuals;
        private readonly Grid _root;
        private readonly Ellipse _highlight;
        private readonly Ellipse _dent;
        private readonly double _diameter;
        private readonly Point _origin;
        private bool _released;
        private bool _removed;

        public DepressionAdorner(UIElement adornedElement, Point origin) : base(adornedElement)
        {
            _origin = origin;

            var size = adornedElement.RenderSize;
            _diameter = Math.Clamp(Math.Min(size.Width, size.Height) * 1.9, 56, 260);

            var accent = (FrameworkElement)adornedElement is { } element
                ? element.TryFindResource("AccentBrush") as SolidColorBrush
                : null;
            var accentColor = accent?.Color ?? Color.FromRgb(0x9B, 0x8C, 0xFF);

            _highlight = new Ellipse
            {
                Width = _diameter,
                Height = _diameter,
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Left,
                Fill = new RadialGradientBrush
                {
                    GradientStops = new GradientStopCollection
                    {
                        new(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF), 0.0),
                        new(Color.FromArgb(0x0C, 0xFF, 0xFF, 0xFF), 0.52),
                        new(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1.0)
                    }
                },
                Margin = new Thickness(0, _diameter * 0.16, 0, 0),
                Opacity = 0
            };

            _dent = new Ellipse
            {
                Width = _diameter,
                Height = _diameter,
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Left,
                Fill = new RadialGradientBrush
                {
                    GradientOrigin = new Point(0.47, 0.43),
                    Center = new Point(0.5, 0.5),
                    RadiusX = 0.5,
                    RadiusY = 0.5,
                    GradientStops = new GradientStopCollection
                    {
                        new(Color.FromArgb(0x4C, 0x00, 0x00, 0x00), 0.0),
                        new(Color.FromArgb(0x30, 0x00, 0x00, 0x00), 0.38),
                        new(Color.FromArgb(0x14, 0x00, 0x00, 0x00), 0.66),
                        new(Color.FromArgb(0x10, accentColor.R, accentColor.G, accentColor.B), 0.84),
                        new(Color.FromArgb(0x00, accentColor.R, accentColor.G, accentColor.B), 1.0)
                    }
                },
                Opacity = 0,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(0.38, 0.38)
            };

            _root = new Grid
            {
                Width = _diameter,
                Height = _diameter,
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Left,
                IsHitTestVisible = false
            };
            _root.Children.Add(_highlight);
            _root.Children.Add(_dent);

            _visuals = new VisualCollection(this) { _root };

            var radius = adornedElement is Border adornedBorder ? adornedBorder.CornerRadius.TopLeft : 0;
            Clip = new RectangleGeometry(new Rect(size), radius, radius);

            // Only after the visual child exists: setting this earlier makes WPF
            // walk VisualChildrenCount while _visuals is still null.
            IsHitTestVisible = false;
        }

        protected override int VisualChildrenCount => _visuals.Count;
        protected override Visual GetVisualChild(int index) => _visuals[index];

        protected override Size ArrangeOverride(Size finalSize)
        {
            var slot = new Rect(_origin.X - _diameter / 2, _origin.Y - _diameter / 2, _diameter, _diameter);
            _root.Measure(new Size(_diameter, _diameter));
            _root.Arrange(slot);
            return finalSize;
        }

        public void Remove()
        {
            if (_removed) return;
            _removed = true;

            if (Parent is AdornerLayer layer) layer.Remove(this);
        }

        public void Press()
        {
            var scale = (ScaleTransform)_dent.RenderTransform;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            _dent.BeginAnimation(OpacityProperty, new DoubleAnimation
            {
                To = 1,
                Duration = TimeSpan.FromMilliseconds(110),
                EasingFunction = ease
            });

            _highlight.BeginAnimation(OpacityProperty, new DoubleAnimation
            {
                To = 1,
                Duration = TimeSpan.FromMilliseconds(150),
                EasingFunction = ease
            });

            var grow = new DoubleAnimation(0.38, 1, TimeSpan.FromMilliseconds(240))
            {
                EasingFunction = ease
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }

        public void Release(Action completed)
        {
            if (_released) return;
            _released = true;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var scale = (ScaleTransform)_dent.RenderTransform;

            var fade = new DoubleAnimation
            {
                To = 0,
                Duration = TimeSpan.FromMilliseconds(340),
                EasingFunction = ease,
                FillBehavior = FillBehavior.Stop
            };
            fade.Completed += (_, _) => completed();

            _dent.BeginAnimation(OpacityProperty, fade);
            _highlight.BeginAnimation(OpacityProperty, new DoubleAnimation
            {
                To = 0,
                Duration = TimeSpan.FromMilliseconds(280),
                EasingFunction = ease,
                FillBehavior = FillBehavior.Stop
            });

            var shrink = new DoubleAnimation
            {
                To = 1.06,
                Duration = TimeSpan.FromMilliseconds(300),
                EasingFunction = ease,
                FillBehavior = FillBehavior.Stop
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
        }
    }
}
