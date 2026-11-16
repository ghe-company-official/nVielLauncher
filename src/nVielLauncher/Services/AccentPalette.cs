namespace nVielLauncher.Services;

/// <summary>
/// Preset accent colours shared by the settings page and the first-run wizard.
/// They are deliberately soft so the monochrome UI stays readable.
/// </summary>
public static class AccentPalette
{
    public static readonly string[] Presets =
    {
        "#FFFFFFFF", // white
        "#FFC8CDD6", // silver
        "#FFBFD4E8", // ice
        "#FFBFE8D0", // mint
        "#FFE8D9BF", // sand
        "#FFE8BFCB", // rose
        "#FFD4C4E8", // lavender
        "#FFBFE8E4", // aqua
        "#FFE8E4BF", // lemon
        "#FFE8CBBF", // peach
        "#FFBFD9E8", // sky
        "#FFCBE8BF"  // sage
    };

    /// <summary>Base colours used by the Material You theme. Only hue and chroma matter.</summary>
    public static readonly string[] BasePresets =
    {
        "#FF6750A4", // purple
        "#FF4F6B2A", // olive
        "#FF2F6A8C", // blue
        "#FF8C4A5B", // rose
        "#FF8C6A2F", // amber
        "#FF2F8C7A", // teal
        "#FF5B4A8C", // indigo
        "#FF8C3A2F"  // rust
    };
}
