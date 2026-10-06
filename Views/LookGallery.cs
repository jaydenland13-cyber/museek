using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Museek.Services;

namespace Museek.Views;

/// <summary>
/// Draws small live mock-ups of each look (panel, title dots, cover, text, progress bar, play button) so you can
/// compare styles before choosing. Pure shapes — no images — and only built when a gallery is on screen.
/// </summary>
public static class LookGallery
{
    private static Color Hex(string h)
    {
        try { return (Color)ColorConverter.ConvertFromString(h); } catch { return Colors.Gray; }
    }

    private static SolidColorBrush Solid(Color c) => new(c);
    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    public static FrameworkElement BuildPreview(LookPreset p, double w)
    {
        var h = Math.Round(w * 0.74);
        var dark = p.Theme != "light";
        var bg = Hex(p.Bg);
        var acc = Hex(p.Accent);
        var fg = dark ? Color.FromRgb(0xF8, 0xFA, 0xFC) : Color.FromRgb(0x0F, 0x17, 0x2A);
        var k = w / 420.0;                                        // preview scale relative to the real overlay

        var root = new Grid { Width = w, Height = h, ClipToBounds = true };
        root.Children.Add(new Border   // "wallpaper" behind the glass, so see-through looks read as glass
        {
            CornerRadius = new CornerRadius(8),
            Background = new LinearGradientBrush(Color.FromRgb(0x4C, 0x1D, 0x95), Color.FromRgb(0x0E, 0x74, 0x90), 35),
        });

        double pw = w - 12, ph = h - 12;
        var panel = new Border
        {
            Margin = new Thickness(6),
            CornerRadius = new CornerRadius(Math.Max(3, p.Radius * k * 1.4)),
            Background = Solid(Color.FromArgb((byte)(255 * Math.Clamp(p.Opacity, 0.2, 1.0)), bg.R, bg.G, bg.B)),
            BorderThickness = new Thickness(Math.Min(2, p.Border)),
            BorderBrush = p.BorderAccent ? Solid(WithAlpha(acc, 0xCC)) : Solid(dark ? Color.FromArgb(0x40, 255, 255, 255) : Color.FromArgb(0x30, 0, 0, 0)),
            ClipToBounds = true,
        };
        var cv = new Canvas { Width = pw, Height = ph };
        panel.Child = cv;
        root.Children.Add(panel);

        void Put(UIElement e, double x, double y) { Canvas.SetLeft(e, x); Canvas.SetTop(e, y); cv.Children.Add(e); }
        Rectangle Bar(double bw, double bh, Color c, double r = 1.5) => new() { Width = bw, Height = bh, RadiusX = r, RadiusY = r, Fill = Solid(c) };

        if (p.Glow)
            Put(new Ellipse
            {
                Width = pw * 0.95, Height = pw * 0.95,
                Fill = new RadialGradientBrush(WithAlpha(acc, 0x55), WithAlpha(acc, 0x00)),
            }, -pw * 0.25, -pw * 0.38);

        // title-bar dots + brand line
        Color Dot(int i) => p.Dots == "mono" ? WithAlpha(fg, 0x66)
            : i == 0 ? Color.FromRgb(0xEF, 0x44, 0x44) : i == 1 ? Color.FromRgb(0xEA, 0xB3, 0x08) : Color.FromRgb(0x22, 0xC5, 0x5E);
        for (var i = 0; i < 3; i++)
            Put(new Ellipse { Width = 5, Height = 5, Fill = Solid(Dot(i)) }, 8 + i * 8, 7);
        Put(Bar(pw * 0.16, 3, WithAlpha(fg, 0x66)), 36, 8);

        // cover + text
        var art = Math.Round(ph * 0.30);
        var artRadius = Math.Max(2, p.CardRadius * k * 1.4);
        Put(new Border
        {
            Width = art, Height = art, CornerRadius = new CornerRadius(artRadius),
            Background = new LinearGradientBrush(acc, Color.FromRgb(0xEC, 0x48, 0x99), 45),
        }, 10, 20);
        Put(Bar(pw * 0.36, 4, fg), 10 + art + 8, 24);
        Put(Bar(pw * 0.24, 3, WithAlpha(fg, 0x8C)), 10 + art + 8, 33);
        Put(Bar(pw * 0.15, 3, WithAlpha(fg, 0x55)), 10 + art + 8, 40);

        // progress bar
        var barH = Math.Max(2, p.Progress * k * 1.6);
        var barY = 20 + art + 9;
        var rad = p.ProgressRound ? barH / 2 : 1;
        Put(Bar(pw - 20, barH, WithAlpha(fg, 0x33), rad), 10, barY);
        var fill = Bar((pw - 20) * 0.42, barH, acc, rad);
        if (p.PlayStyle == "gradient") fill.Fill = new LinearGradientBrush(acc, Color.FromRgb(0x06, 0xB6, 0xD4), 0);
        Put(fill, 10, barY);

        // transport controls
        var cy = ph - 20;
        Put(new Ellipse { Width = 6, Height = 6, Fill = Solid(WithAlpha(fg, 0x88)) }, pw / 2 - 30, cy + 5);
        Put(new Ellipse { Width = 6, Height = 6, Fill = Solid(WithAlpha(fg, 0x88)) }, pw / 2 + 24, cy + 5);

        const double ps = 16;
        UIElement play;
        switch (p.PlayStyle)
        {
            case "solid":
                play = new Ellipse { Width = ps, Height = ps, Fill = Solid(acc) }; break;
            case "square":
                play = new Rectangle { Width = ps, Height = ps, RadiusX = 5, RadiusY = 5, Fill = Solid(acc) }; break;
            case "outline":
                play = new Ellipse { Width = ps, Height = ps, Stroke = Solid(acc), StrokeThickness = 1.6, Fill = Brushes.Transparent }; break;
            case "ghost":
                play = new Ellipse { Width = ps, Height = ps, Fill = Brushes.Transparent }; break;
            default:
                play = new Ellipse { Width = ps, Height = ps, Fill = new LinearGradientBrush(acc, Color.FromRgb(0xEC, 0x48, 0x99), 45) }; break;
        }
        Put(play, pw / 2 - ps / 2, cy);
        var glyph = p.PlayStyle == "outline" ? acc : p.PlayStyle == "ghost" ? fg : Colors.White;
        Put(new Polygon
        {
            Points = new PointCollection { new Point(0, 0), new Point(0, 7), new Point(6, 3.5) },
            Fill = Solid(glyph),
        }, pw / 2 - 2, cy + ps / 2 - 3.5);

        return root;
    }

    /// <summary>A wrap-panel of clickable preview cards. The chosen look gets a ring.</summary>
    public static WrapPanel Build(string currentKey, Action<LookPreset> onPick, double cardWidth,
                                  Brush nameFg, Brush subFg, Brush ring)
    {
        var wrap = new WrapPanel();
        foreach (var p in Looks.All)
        {
            var look = p;
            var selected = look.Key == currentKey;
            var card = new Border
            {
                Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(4), CornerRadius = new CornerRadius(12),
                Cursor = Cursors.Hand, BorderThickness = new Thickness(2), Background = Brushes.Transparent,
                BorderBrush = selected ? ring : Brushes.Transparent, ToolTip = look.Blurb,
            };
            var stack = new StackPanel();
            stack.Children.Add(BuildPreview(look, cardWidth));
            stack.Children.Add(new TextBlock
            {
                Text = look.Name, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = nameFg,
                Margin = new Thickness(2, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = cardWidth,
            });
            if (cardWidth >= 130)
                stack.Children.Add(new TextBlock
                {
                    Text = look.Blurb, FontSize = 10, Foreground = subFg, Margin = new Thickness(2, 1, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = cardWidth,
                });
            card.Child = stack;
            var hover = Solid(Color.FromArgb(0x66, 255, 255, 255));
            card.MouseEnter += (_, _) => { if (!selected) card.BorderBrush = hover; };
            card.MouseLeave += (_, _) => { if (!selected) card.BorderBrush = Brushes.Transparent; };
            card.MouseLeftButtonUp += (_, _) => onPick(look);
            wrap.Children.Add(card);
        }
        return wrap;
    }
}
