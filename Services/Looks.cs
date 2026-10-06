namespace Museek.Services;

/// <summary>One complete visual style: colours, shapes, spacing and typography.</summary>
public sealed record LookPreset(
    string Key, string Name, string Blurb,
    string Theme,            // dark | light
    string Bg,               // panel colour
    string Accent,
    double Opacity,          // panel opacity 0.2 - 1
    int Radius, int CardRadius, int ControlRadius,
    int Border, bool BorderAccent,
    int Progress, bool ProgressRound,
    string PlayStyle,        // gradient | solid | square | outline | ghost
    string Dots,             // color | mono
    bool Glow,
    string Density,          // comfortable | compact
    string Font);

public static class Looks
{
    private const string Modern = "Segoe UI Variable Text, Segoe UI";

    public static readonly LookPreset[] All =
    {
        new("aurora",   "Aurora",       "Deep glass with a purple glow",   "dark",  "#0F172A", "#8B5CF6", 0.88, 24, 12, 12, 1, false, 8, true,  "gradient", "color", true,  "comfortable", "Inter, Segoe UI Variable Text, Segoe UI"),
        new("midnight", "Midnight Pro", "Flat, sharp and quiet",           "dark",  "#0B0B0F", "#3B82F6", 0.96, 12,  8,  8, 1, false, 4, false, "solid",    "mono",  false, "compact",     Modern),
        new("frost",    "Frost",        "Bright, clean and airy",          "light", "#F8FAFC", "#2563EB", 0.92, 20, 14, 12, 1, false, 6, true,  "solid",    "color", false, "comfortable", Modern),
        new("neon",     "Neon",         "Black glass, glowing edge",       "dark",  "#07040F", "#22D3EE", 0.94, 16, 10, 10, 2, true,  6, true,  "outline",  "color", true,  "comfortable", Modern),
        new("ember",    "Ember",        "Warm sunset tones",               "dark",  "#1C0F13", "#F97316", 0.90, 22, 12, 12, 1, false, 8, true,  "gradient", "color", true,  "comfortable", Modern),
        new("emerald",  "Emerald",      "Calm and natural",                "dark",  "#04140F", "#10B981", 0.90, 18, 12, 12, 1, false, 6, true,  "solid",    "color", true,  "comfortable", Modern),
        new("mono",     "Mono",         "Grayscale and understated",       "dark",  "#111111", "#E5E7EB", 0.95, 10,  6,  6, 1, false, 3, false, "ghost",    "mono",  false, "compact",     Modern),
        new("crystal",  "Crystal",      "See-through frosted glass",       "dark",  "#0F172A", "#A78BFA", 0.45, 28, 16, 14, 1, false, 6, true,  "square",   "color", true,  "comfortable", Modern),
    };

    public static LookPreset Default => All[0];

    public static LookPreset Find(string? key)
    {
        foreach (var p in All) if (p.Key == key) return p;
        return Default;
    }

    /// <summary>Writes a look into the settings. Everything stays individually adjustable afterwards.</summary>
    public static void Apply(AppSettings s, LookPreset p)
    {
        s.Look = p.Key;
        s.ThemeMode = p.Theme;
        s.BgColorOverride = p.Bg;
        s.TextColorOverride = null;
        s.AccentColor = p.Accent;
        s.OverlayOpacity = p.Opacity;
        s.BackgroundMode = "solid";
        s.CornerRadius = p.Radius;
        s.CardRadius = p.CardRadius;
        s.ControlRadius = p.ControlRadius;
        s.BorderWidth = p.Border;
        s.BorderAccent = p.BorderAccent;
        s.ProgressBarHeight = p.Progress;
        s.ProgressRound = p.ProgressRound;
        s.PlayStyle = p.PlayStyle;
        s.DotStyle = p.Dots;
        s.AmbientGlow = p.Glow;
        s.Density = p.Density;
        s.FontFamily = p.Font;
    }

    /// <summary>Snapshot of the current look-related settings (used for "Undo").</summary>
    public static LookPreset Capture(AppSettings s)
    {
        var light = string.Equals(s.ThemeMode, "light", System.StringComparison.OrdinalIgnoreCase);
        return new LookPreset(s.Look, "Previous", "", s.ThemeMode,
            s.BgColorOverride ?? (light ? "#F8FAFC" : "#0F172A"), s.AccentColor, s.OverlayOpacity,
            s.CornerRadius, s.CardRadius, s.ControlRadius, s.BorderWidth, s.BorderAccent,
            s.ProgressBarHeight, s.ProgressRound, s.PlayStyle, s.DotStyle, s.AmbientGlow, s.Density, s.FontFamily);
    }
}
