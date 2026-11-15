using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace nVielLauncher.Controls;

/// <summary>
/// Circular hue/saturation picker with a brightness slider and a HEX field.
/// The wheel is rendered once into a bitmap; the marker and preview update live.
/// </summary>
public partial class ColorWheelPicker : UserControl
{
    private const int WheelSize = 256;

    private static readonly BitmapSource WheelBitmap = CreateWheel(WheelSize);

    private double _hue;
    private double _saturation;
    private double _value = 1;
    private bool _suppress;
    private bool _dragging;
    private bool _ready;

    public ColorWheelPicker()
    {
        InitializeComponent();

        WheelImage.Source = WheelBitmap;
        RenderOptions.SetBitmapScalingMode(WheelImage, BitmapScalingMode.HighQuality);
        WheelHost.SizeChanged += (_, _) => { if (_ready) UpdateMarker(); };

        _ready = true;
        UpdateUi(raise: false);
    }

    /// <summary>Raised while the user drags on the wheel or edits the brightness/HEX value.</summary>
    public event Action<Color>? ColorChanged;

    public Color SelectedColor => HsvToColor(_hue, _saturation, _value);

    /// <summary>Loads a colour without raising <see cref="ColorChanged"/>.</summary>
    public void SetColor(Color color)
    {
        var (hue, saturation, value) = RgbToHsv(color);
        _hue = hue;
        _saturation = saturation;
        _value = Math.Clamp(value, 0, 1);

        _suppress = true;
        ValueSlider.Value = _value;
        _suppress = false;

        UpdateUi(raise: false);
    }

    private void Wheel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        WheelHost.CaptureMouse();
        WheelHost.Focus();
        PickFromPoint(e.GetPosition(WheelHost));
        e.Handled = true;
    }

    private void Wheel_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed) return;
        PickFromPoint(e.GetPosition(WheelHost));
    }

    private void Wheel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        WheelHost.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void Wheel_MouseLeave(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
            _dragging = false;
    }

    private void PickFromPoint(Point point)
    {
        var size = Math.Min(HostWidth(), HostHeight());
        var radius = size / 2.0;
        if (radius <= 0) return;

        var dx = point.X - HostWidth() / 2.0;
        var dy = point.Y - HostHeight() / 2.0;
        var distance = Math.Sqrt(dx * dx + dy * dy);

        _saturation = Math.Clamp(distance / radius, 0, 1);
        if (distance > 0.001)
        {
            var hue = Math.Atan2(dy, dx) * 180.0 / Math.PI;
            if (hue < 0) hue += 360;
            _hue = hue;
        }

        UpdateUi(raise: true);
    }

    private void ValueSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _suppress) return;
        _value = Math.Clamp(e.NewValue, 0, 1);
        UpdateUi(raise: true);
    }

    private void HexBox_LostFocus(object sender, RoutedEventArgs e) => ApplyHex();

    private void HexBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplyHex();
        WheelHost.Focus();
        e.Handled = true;
    }

    private void ApplyHex()
    {
        if (_suppress) return;
        if (!TryParseHex(HexBox.Text, out var color))
        {
            UpdateUi(raise: false);
            return;
        }

        SetColor(color);
        ColorChanged?.Invoke(color);
    }

    private void UpdateUi(bool raise)
    {
        var color = HsvToColor(_hue, _saturation, _value);

        Preview.Background = new SolidColorBrush(color);
        ValueText.Text = $"{_value * 100:0}%";

        _suppress = true;
        HexBox.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        _suppress = false;

        UpdateMarker();

        if (raise)
            ColorChanged?.Invoke(color);
    }

    private void UpdateMarker()
    {
        var width = HostWidth();
        var height = HostHeight();
        var radius = Math.Min(width, height) / 2.0;
        if (radius <= 0) return;

        var angle = _hue * Math.PI / 180.0;
        var distance = _saturation * radius;
        var x = width / 2.0 + Math.Cos(angle) * distance;
        var y = height / 2.0 + Math.Sin(angle) * distance;

        Canvas.SetLeft(Marker, x - Marker.Width / 2);
        Canvas.SetTop(Marker, y - Marker.Height / 2);
    }

    private double HostWidth() => WheelHost.ActualWidth > 0 ? WheelHost.ActualWidth : WheelHost.Width;
    private double HostHeight() => WheelHost.ActualHeight > 0 ? WheelHost.ActualHeight : WheelHost.Height;

    private static bool TryParseHex(string? text, out Color color)
    {
        color = Colors.White;
        if (string.IsNullOrWhiteSpace(text)) return false;

        text = text.Trim();
        if (!text.StartsWith('#')) text = "#" + text;

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

    private static Color HsvToColor(double hue, double saturation, double value)
    {
        hue = ((hue % 360) + 360) % 360;
        saturation = Math.Clamp(saturation, 0, 1);
        value = Math.Clamp(value, 0, 1);

        var c = value * saturation;
        var x = c * (1 - Math.Abs(hue / 60.0 % 2 - 1));
        var m = value - c;

        double r, g, b;
        if (hue < 60) { r = c; g = x; b = 0; }
        else if (hue < 120) { r = x; g = c; b = 0; }
        else if (hue < 180) { r = 0; g = c; b = x; }
        else if (hue < 240) { r = 0; g = x; b = c; }
        else if (hue < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }

        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    private static (double Hue, double Saturation, double Value) RgbToHsv(Color color)
    {
        var r = color.R / 255.0;
        var g = color.G / 255.0;
        var b = color.B / 255.0;

        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        double hue = 0;
        if (delta > 1e-6)
        {
            if (Math.Abs(max - r) < 1e-9)
                hue = 60 * (((g - b) / delta) % 6);
            else if (Math.Abs(max - g) < 1e-9)
                hue = 60 * ((b - r) / delta + 2);
            else
                hue = 60 * ((r - g) / delta + 4);
        }

        if (hue < 0) hue += 360;

        var saturation = max <= 0 ? 0 : delta / max;
        return (hue, saturation, max);
    }

    private static BitmapSource CreateWheel(int size)
    {
        var pixels = new byte[size * size * 4];
        var radius = size / 2.0;

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var dx = x + 0.5 - radius;
                var dy = y + 0.5 - radius;
                var distance = Math.Sqrt(dx * dx + dy * dy);
                var index = (y * size + x) * 4;

                if (distance > radius)
                {
                    pixels[index + 3] = 0;
                    continue;
                }

                var hue = Math.Atan2(dy, dx) * 180.0 / Math.PI;
                if (hue < 0) hue += 360;

                var saturation = Math.Min(1, distance / radius);
                var color = HsvToColor(hue, saturation, 1);

                // Bgra32
                pixels[index] = color.B;
                pixels[index + 1] = color.G;
                pixels[index + 2] = color.R;
                pixels[index + 3] = 255;
            }
        }

        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        bitmap.Freeze();
        return bitmap;
    }
}
