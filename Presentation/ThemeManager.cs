using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace CompactLoader;

internal sealed record AccentOption(string Name, string Dark, string Light);
internal sealed record AppearancePreferences(bool Light = false, string Accent = "Windows")
{
    public string? CustomAccent { get; init; }
}

internal static class ThemeManager
{
    internal static IReadOnlyList<AccentOption> Accents { get; } = new[]
    {
        new AccentOption("Green", "#A0CB87", "#39713C"),
        new AccentOption("Blue", "#83B6E7", "#245E9B"),
        new AccentOption("Cyan", "#7BC7BF", "#206D68"),
        new AccentOption("Amber", "#D9B66F", "#8B5A10"),
        new AccentOption("Violet", "#B7A0DF", "#7351A2"),
        new AccentOption("Coral", "#DD9684", "#A44835")
    };
    internal static bool IsLight { get; private set; }
    internal static string AccentName { get; private set; } = "Windows";
    internal static Color WindowsAccentColor
    {
        get
        {
            var color = SystemParameters.WindowGlassColor;
            return Color.FromRgb(color.R, color.G, color.B);
        }
    }
    internal static string CustomAccentHex { get; private set; } = "#A0CB87";
    static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CompactLoader", "appearance.json");

    internal static void Load()
    {
        var settings = AppearancePreferencesStore.Read(SettingsPath);
        bool validCustom = TryParseHex(settings.CustomAccent, out var custom);
        CustomAccentHex = validCustom ? Hex(custom) : "#A0CB87";
        var knownAccent = settings.Accent == "Windows" || settings.Accent == "Custom" && validCustom || Accents.Any(a => a.Name == settings.Accent);
        Apply(settings.Light, knownAccent ? settings.Accent : "Windows", false);
    }

    internal static Color Preview(AccentOption option) => (Color)ColorConverter.ConvertFromString(IsLight ? option.Light : option.Dark);

    internal static bool Apply(bool light, string accentName, bool save = true)
    {
        var option = Accents.FirstOrDefault(a => a.Name == accentName);
        if (option is null && accentName is not ("Custom" or "Windows")) throw new ArgumentOutOfRangeException(nameof(accentName));
        var palette = new ResourceDictionary { Source = new Uri($"/CompactLoader;component/Themes/{(light ? "Light" : "Dark")}.xaml", UriKind.Relative) };
        var accent = accentName == "Windows" ? WindowsAccentColor
            : accentName == "Custom" ? (Color)ColorConverter.ConvertFromString(CustomAccentHex)
            : (Color)ColorConverter.ConvertFromString(light ? option!.Light : option!.Dark);
        void Brush(string key, Color color)
        {
            var brush = new SolidColorBrush(color); brush.Freeze(); palette[key] = brush;
        }
        // Preserve each role's brightness while deriving its tint from the accent.
        // Previously only active controls changed; the remaining palette stayed green.
        var tintedRoles = new (string Key, double Strength)[]
        {
            ("AppBackground", .12), ("Surface", .15), ("PopupSurface", .16),
            ("ButtonBackground", .23), ("Divider", .27), ("InputBorder", .28),
            ("FrameBorder", .35), ("FieldBorder", .32), ("ScrollThumbBrush", .35),
            ("PopupBorder", .35), ("ConsoleBackground", .09), ("ConsoleBorder", .24),
            ("Text", .08), ("Muted", .18), ("Subtle", .17), ("CaptionText", .18),
            ("SecondaryText", .25), ("ConsoleText", .55)
        };
        foreach (var (key, strength) in tintedRoles)
            Brush(key, Tint(((SolidColorBrush)palette[key]).Color, accent, strength));
        if (!light) Brush("OnAccent", Tint(((SolidColorBrush)palette["OnAccent"]).Color, accent, .10));
        var background = ((SolidColorBrush)palette["AppBackground"]).Color;
        var text = ((SolidColorBrush)palette["Text"]).Color;
        Brush("Accent", accent);
        Brush("OnAccent", Contrast(accent, Colors.White) > Contrast(accent, Colors.Black) ? Colors.White : Colors.Black);
        Brush("AccentInk", Legible(accent, background, 4.5));
        Brush("AccentOutline", Legible(accent, background, 3));
        Brush("AccentHover", Legible(Blend(accent, text, 0.12), background, 3));
        Brush("AccentMuted", Legible(Blend(accent, text, 0.35), background, 4.5));
        Brush("SelectedBackground", Blend(background, accent, light ? 0.13 : 0.18));
        Brush("HoverBackground", Blend(background, accent, light ? 0.06 : 0.08));
        Brush("TextSelection", Blend(background, accent, light ? 0.24 : 0.4));
        Brush("PickerBorder", Blend(background, accent, 0.5));
        Application.Current.Resources.MergedDictionaries[0] = palette;
        IsLight = light; AccentName = accentName;
        return !save || AppearancePreferencesStore.Write(SettingsPath, new AppearancePreferences(light, accentName) { CustomAccent = CustomAccentHex });
    }

    internal static bool ApplyCustom(string hex, bool save = true)
    {
        if (!TryParseHex(hex, out var color)) throw new ArgumentException("Use a six-digit RGB hex color.", nameof(hex));
        CustomAccentHex = Hex(color);
        return Apply(IsLight, "Custom", save);
    }
    internal static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    internal static bool TryParseHex(string? value, out Color color)
    {
        color = default;
        var text = value?.Trim();
        if (text?.StartsWith('#') == true) text = text[1..];
        if (text?.Length != 6 || !uint.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var rgb)) return false;
        color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }
    static double Luminance(Color color)
    {
        static double Channel(byte value) { var v = value / 255.0; return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); }
        return .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B);
    }
    static double Contrast(Color a, Color b) => (Math.Max(Luminance(a), Luminance(b)) + .05) / (Math.Min(Luminance(a), Luminance(b)) + .05);
    static Color Legible(Color color, Color background, double minimum)
    {
        var target = Luminance(background) > .2 ? Colors.Black : Colors.White;
        for (int step = 0; step <= 20; step++)
        {
            var result = Blend(color, target, step / 20.0);
            if (Contrast(result, background) >= minimum) return result;
        }
        return target;
    }

    static Color Tint(Color basis, Color accent, double strength)
    {
        static double Brightness(Color color) => .2126 * color.R + .7152 * color.G + .0722 * color.B;
        var brightness = Brightness(basis);
        var accentBrightness = Brightness(accent);
        byte Channel(byte value) => (byte)Math.Clamp(Math.Round(brightness + (value - accentBrightness) * strength), 0, 255);
        return Color.FromRgb(Channel(accent.R), Channel(accent.G), Channel(accent.B));
    }

    static Color Blend(Color from, Color to, double ratio) => Color.FromRgb(
        (byte)Math.Round(from.R + (to.R - from.R) * ratio),
        (byte)Math.Round(from.G + (to.G - from.G) * ratio),
        (byte)Math.Round(from.B + (to.B - from.B) * ratio));
}
