using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Museek.Services;

public sealed record ShareCardData(string Title, string Artist, string Album, ImageSource? Art,
                                   double Fraction, Color Accent, string? Lyric, string FontFamily);

/// <summary>Renders a 1080×1350 "now playing" picture for sharing. Runs only when you ask for it.</summary>
public static class ShareCard
{
    public static BitmapSource Render(ShareCardData d)
    {
        const double W = 1080, H = 1350, inner = 880;
        var a = d.Accent;
        Color Shade(double f) => Color.FromRgb((byte)(a.R * f), (byte)(a.G * f), (byte)(a.B * f));

        var root = new Grid
        {
            Width = W, Height = H,
            Background = new LinearGradientBrush(Shade(0.30), Color.FromRgb(0x07, 0x0A, 0x13), 62),
        };
        root.Children.Add(new Ellipse
        {
            Width = 1000, Height = 1000, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(-320, -300, 0, 0),
            Fill = new RadialGradientBrush(Color.FromArgb(0x70, a.R, a.G, a.B), Color.FromArgb(0, a.R, a.G, a.B)),
        });

        var stack = new StackPanel { Width = inner, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var font = new FontFamily(d.FontFamily);

        var art = new Border
        {
            Width = 640, Height = 640, CornerRadius = new CornerRadius(48), HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 52),
            Background = d.Art != null ? new ImageBrush(d.Art) { Stretch = Stretch.UniformToFill }
                                       : new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)),
        };
        if (d.Art == null)
            art.Child = new TextBlock { Text = "♪", FontSize = 280, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(art);

        TextBlock T(string text, double size, Brush fg, FontWeight? w = null, double top = 0) => new()
        {
            Text = text, FontSize = size, Foreground = fg, FontFamily = font, FontWeight = w ?? FontWeights.Normal,
            TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, top, 0, 0),
        };

        stack.Children.Add(T(d.Title, 60, Brushes.White, FontWeights.Bold));
        stack.Children.Add(T(d.Artist, 38, new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1)), top: 8));
        if (!string.IsNullOrWhiteSpace(d.Album))
            stack.Children.Add(T(d.Album, 28, new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)), top: 4));

        var bar = new Grid { Height = 10, Margin = new Thickness(0, 44, 0, 0) };
        bar.Children.Add(new Border { CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(Color.FromArgb(0x44, 255, 255, 255)) });
        bar.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(5), HorizontalAlignment = HorizontalAlignment.Left,
            Width = Math.Max(10, inner * Math.Clamp(d.Fraction, 0, 1)),
            Background = new LinearGradientBrush(a, Color.FromRgb(0x06, 0xB6, 0xD4), 0),
        });
        stack.Children.Add(bar);

        if (!string.IsNullOrWhiteSpace(d.Lyric))
        {
            var q = T("“" + d.Lyric + "”", 36, new SolidColorBrush(Color.FromRgb(
                (byte)Math.Min(255, a.R + 110), (byte)Math.Min(255, a.G + 110), (byte)Math.Min(255, a.B + 110))), top: 44);
            q.FontStyle = FontStyles.Italic; q.TextWrapping = TextWrapping.Wrap; q.TextTrimming = TextTrimming.None; q.MaxHeight = 120;
            stack.Children.Add(q);
        }
        root.Children.Add(stack);

        root.Children.Add(new TextBlock
        {
            Text = "♫  Museek", FontSize = 30, FontFamily = font, Foreground = new SolidColorBrush(Color.FromArgb(0x99, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 44),
        });

        root.Measure(new Size(W, H));
        root.Arrange(new Rect(0, 0, W, H));
        root.UpdateLayout();
        var rtb = new RenderTargetBitmap((int)W, (int)H, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);
        rtb.Freeze();
        return rtb;
    }
}
