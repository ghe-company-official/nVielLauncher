using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace nVielLauncher.Services;

/// <summary>
/// Turns a seed (accent) colour and a base colour into a Material 3 style dark scheme
/// and writes it into the application resources. The nViel mode keeps the original
/// monochrome look but still honours the user's accent colour.
/// </summary>
public static class MaterialTheme
{
    public const string ModeNviel = "nViel";
    public const string ModeMaterial = "material";

    public const string DefaultSeedHex = "#FFD0BCFF";
    public const string DefaultBaseHex = "#FF6750A4";

    public static readonly Color DefaultSeed = Color.FromRgb(0xD0, 0xBC, 0xFF);
    public static readonly Color DefaultBase = Color.FromRgb(0x67, 0x50, 0xA4);

    public static string NormalizeMode(string? mode) =>
        string.Equals(mode, ModeNviel, StringComparison.OrdinalIgnoreCase) ? ModeNviel : ModeMaterial;

    public static bool IsMaterial(string? mode) => NormalizeMode(mode) == ModeMaterial;

    /// <summary>Rebuilds every palette brush and icon resource from the current settings.</summary>
    public static void Apply(ResourceDictionary resources, LauncherSettings settings)
    {
        var mode = NormalizeMode(settings.ThemeMode);
        var seed = ParseOpaque(settings.AccentColor, DefaultSeed);
        var baseColor = ParseOpaque(settings.BaseColor, DefaultBase);

        var brushes = mode == ModeNviel ? NvielBrushes(seed) : MaterialBrushes(seed, baseColor);
        Write(resources, brushes);
        IconTheme.Apply(resources, settings);
    }

    public static Color ParseOpaque(string? text, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;

        try
        {
            var color = (Color)ColorConverter.ConvertFromString(text.Trim());
            return Color.FromRgb(color.R, color.G, color.B);
        }
        catch
        {
            return fallback;
        }
    }

    private static void Write(ResourceDictionary resources, Dictionary<string, Color> brushes)
    {
        foreach (var pair in brushes)
        {
            var brush = new SolidColorBrush(pair.Value);
            brush.Freeze();
            resources[pair.Key] = brush;

            if (pair.Key.EndsWith("Brush", StringComparison.Ordinal))
            {
                var colorKey = pair.Key[..^"Brush".Length] + "Color";
                resources[colorKey] = pair.Value;
            }
        }
    }

    // ------------------------------------------------------------- nViel (monochrome)

    private static Dictionary<string, Color> NvielBrushes(Color accent) => new()
    {
        ["BgBrush"] = Rgb(0x06, 0x06, 0x08),
        ["BgSoftBrush"] = Rgb(0x09, 0x09, 0x0C),
        ["SurfaceBrush"] = Rgb(0x0C, 0x0C, 0x10),
        ["SurfaceHoverBrush"] = Rgb(0x13, 0x13, 0x18),
        ["StrokeBrush"] = Rgb(0x1B, 0x1B, 0x21),
        ["StrokeSoftBrush"] = Rgb(0x12, 0x12, 0x17),
        ["StrokeStrongBrush"] = Rgb(0x2B, 0x2B, 0x33),
        ["TextPrimaryBrush"] = Rgb(0xF5, 0xF5, 0xF7),
        ["TextSecondaryBrush"] = Rgb(0x9B, 0x9B, 0xA6),
        ["TextMutedBrush"] = Rgb(0x61, 0x61, 0x6C),
        ["AccentBrush"] = accent,
        ["OnAccentBrush"] = Rgb(0x07, 0x07, 0x09),
        ["BlackBrush"] = Rgb(0x05, 0x05, 0x07),
        ["SuccessBrush"] = Rgb(0x7B, 0xE3, 0xA0),
        ["DangerBrush"] = Rgb(0xFF, 0x9A, 0x9A),
        ["OverlayFaintBrush"] = Alpha(0x08, 0xFF, 0xFF, 0xFF),
        ["OverlaySoftBrush"] = Alpha(0x10, 0xFF, 0xFF, 0xFF),
        ["OverlayBrush"] = Alpha(0x18, 0xFF, 0xFF, 0xFF),
        ["OverlayStrongBrush"] = Alpha(0x26, 0xFF, 0xFF, 0xFF),
        ["BorderSubtleBrush"] = Alpha(0x33, 0xFF, 0xFF, 0xFF),
        ["BorderSoftBrush"] = Alpha(0x59, 0xFF, 0xFF, 0xFF),
        ["BorderStrongBrush"] = Alpha(0x8C, 0xFF, 0xFF, 0xFF),
        ["ScrimBrush"] = Alpha(0xCC, 0x05, 0x05, 0x07),
        ["ScrimSoftBrush"] = Alpha(0x55, 0x06, 0x06, 0x08),
        ["HoverFillBrush"] = Alpha(0x0D, 0xFF, 0xFF, 0xFF),
        ["ActiveFillBrush"] = Alpha(0x16, 0xFF, 0xFF, 0xFF),
        ["LineBrush"] = Alpha(0x14, 0xFF, 0xFF, 0xFF),
    };

    // ------------------------------------------------------------- Material 3 (dark)

    private static Dictionary<string, Color> MaterialBrushes(Color seed, Color baseColor)
    {
        var primary = TonalPalette.FromSeed(seed);
        var neutral = TonalPalette.FromColor(baseColor, 0.22, 0.022);
        var neutralVariant = TonalPalette.FromColor(baseColor, 0.45, 0.045);
        var success = TonalPalette.FromColor(Rgb(0x4C, 0xAF, 0x50), 1.0, 0.12);
        var danger = TonalPalette.FromColor(Rgb(0xB3, 0x26, 0x1E), 1.0, 0.14);

        var bg = neutral.Tone(6);
        var bgSoft = neutral.Tone(9);
        var surface = neutral.Tone(12);
        var surfaceHover = neutral.Tone(17);
        var textPrimary = neutral.Tone(93);
        var textSecondary = neutral.Tone(80);
        var textMuted = neutral.Tone(62);

        return new Dictionary<string, Color>
        {
            ["BgBrush"] = bg,
            ["BgSoftBrush"] = bgSoft,
            ["SurfaceBrush"] = surface,
            ["SurfaceHoverBrush"] = surfaceHover,
            ["StrokeBrush"] = neutralVariant.Tone(30),
            ["StrokeSoftBrush"] = neutralVariant.Tone(20),
            ["StrokeStrongBrush"] = neutralVariant.Tone(38),
            ["TextPrimaryBrush"] = textPrimary,
            ["TextSecondaryBrush"] = textSecondary,
            ["TextMutedBrush"] = textMuted,
            ["AccentBrush"] = primary.Tone(80),
            ["OnAccentBrush"] = primary.Tone(20),
            ["BlackBrush"] = neutral.Tone(3),
            ["SuccessBrush"] = success.Tone(80),
            ["DangerBrush"] = danger.Tone(80),
            ["OverlayFaintBrush"] = WithAlpha(textPrimary, 0x0A),
            ["OverlaySoftBrush"] = WithAlpha(textPrimary, 0x10),
            ["OverlayBrush"] = WithAlpha(textPrimary, 0x1A),
            ["OverlayStrongBrush"] = WithAlpha(textPrimary, 0x26),
            ["BorderSubtleBrush"] = WithAlpha(textPrimary, 0x33),
            ["BorderSoftBrush"] = WithAlpha(textPrimary, 0x59),
            ["BorderStrongBrush"] = WithAlpha(textPrimary, 0x8C),
            ["ScrimBrush"] = WithAlpha(neutral.Tone(2), 0xCC),
            ["ScrimSoftBrush"] = WithAlpha(neutral.Tone(2), 0x55),
            ["HoverFillBrush"] = WithAlpha(textPrimary, 0x0D),
            ["ActiveFillBrush"] = WithAlpha(textPrimary, 0x16),
            ["LineBrush"] = WithAlpha(textPrimary, 0x14),
        };
    }

    // ------------------------------------------------------------- helpers

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    private static Color Alpha(byte a, byte r, byte g, byte b) => Color.FromArgb(a, r, g, b);

    private static Color WithAlpha(Color color, byte alpha) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);

    /// <summary>A tonal palette with a fixed OKLCH hue and chroma, sampled by Material tone.</summary>
    private readonly struct TonalPalette
    {
        private readonly double _hue;
        private readonly double _chroma;

        private TonalPalette(double hue, double chroma)
        {
            _hue = hue;
            _chroma = chroma;
        }

        public static TonalPalette FromColor(Color color, double chromaScale, double maxChroma)
        {
            var (_, chroma, hue) = Oklch.FromColor(color);
            return new TonalPalette(hue, Math.Min(chroma * chromaScale, maxChroma));
        }

        /// <summary>
        /// Builds a vivid primary palette. Pale accents are lifted to a usable chroma,
        /// while a truly neutral seed (white/grey/black) stays neutral on purpose.
        /// </summary>
        public static TonalPalette FromSeed(Color color)
        {
            var (_, chroma, hue) = Oklch.FromColor(color);
            if (chroma < 0.01) return new TonalPalette(0, 0);
            return new TonalPalette(hue, Math.Clamp(chroma * 1.4, 0.06, 0.16));
        }

        /// <summary>Samples a Material tone (0 = black, 100 = white).</summary>
        public Color Tone(double tone) => Oklch.ToColor(ToneToLightness(tone), _chroma, _hue);

        /// <summary>Maps a CIE L* tone to OKLab lightness so the scale matches Material 3.</summary>
        private static double ToneToLightness(double tone)
        {
            tone = Math.Clamp(tone, 0, 100);
            return tone <= 8
                ? Math.Cbrt(tone / 903.3)
                : (tone + 16.0) / 116.0;
        }
    }

    private static class Oklch
    {
        public static (double Lightness, double Chroma, double Hue) FromColor(Color color)
        {
            var r = ToLinear(color.R / 255.0);
            var g = ToLinear(color.G / 255.0);
            var b = ToLinear(color.B / 255.0);

            var l = 0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b;
            var m = 0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b;
            var s = 0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b;

            var lRoot = Math.Cbrt(l);
            var mRoot = Math.Cbrt(m);
            var sRoot = Math.Cbrt(s);

            var lightness = 0.2104542553 * lRoot + 0.7936177850 * mRoot - 0.0040720468 * sRoot;
            var a = 1.9779984951 * lRoot - 2.4285922050 * mRoot + 0.4505937099 * sRoot;
            var bb = 0.0259040371 * lRoot + 0.7827717662 * mRoot - 0.8086757660 * sRoot;

            var chroma = Math.Sqrt(a * a + bb * bb);
            var hue = Math.Atan2(bb, a) * 180.0 / Math.PI;
            if (hue < 0) hue += 360;

            return (lightness, chroma, hue);
        }

        public static Color ToColor(double lightness, double chroma, double hue)
        {
            lightness = Math.Clamp(lightness, 0, 1);
            chroma = Math.Max(0, chroma);

            var radians = hue * Math.PI / 180.0;
            var cos = Math.Cos(radians);
            var sin = Math.Sin(radians);

            if (!InGamut(lightness, chroma * cos, chroma * sin))
            {
                var low = 0.0;
                var high = chroma;
                for (var i = 0; i < 22; i++)
                {
                    var mid = (low + high) / 2.0;
                    if (InGamut(lightness, mid * cos, mid * sin)) low = mid;
                    else high = mid;
                }
                chroma = low;
            }

            var (red, green, blue) = ToLinearRgb(lightness, chroma * cos, chroma * sin);
            return Color.FromRgb(ToByte(red), ToByte(green), ToByte(blue));
        }

        private static bool InGamut(double lightness, double a, double b)
        {
            var (r, g, bb) = ToLinearRgb(lightness, a, b);
            const double epsilon = 0.0001;
            return r >= -epsilon && r <= 1 + epsilon
                && g >= -epsilon && g <= 1 + epsilon
                && bb >= -epsilon && bb <= 1 + epsilon;
        }

        private static (double Red, double Green, double Blue) ToLinearRgb(double lightness, double a, double b)
        {
            var lRoot = lightness + 0.3963377774 * a + 0.2158037573 * b;
            var mRoot = lightness - 0.1055613458 * a - 0.0638541728 * b;
            var sRoot = lightness - 0.0894841775 * a - 1.2914855480 * b;

            var l = lRoot * lRoot * lRoot;
            var m = mRoot * mRoot * mRoot;
            var s = sRoot * sRoot * sRoot;

            return (
                4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
                -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
                -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s);
        }

        private static double ToLinear(double value) =>
            value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);

        private static double FromLinear(double value) =>
            value <= 0.0031308 ? value * 12.92 : 1.055 * Math.Pow(value, 1.0 / 2.4) - 0.055;

        private static byte ToByte(double linear)
        {
            var srgb = Math.Clamp(FromLinear(linear), 0, 1);
            return (byte)Math.Round(srgb * 255.0);
        }
    }
}
