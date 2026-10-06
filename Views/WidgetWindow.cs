using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Museek.Services;

namespace Museek.Views;

/// <summary>
/// A floating glass window that hosts one panel (Up Next, Lyrics, Chat, Visuals) popped out of the main overlay.
/// It shares the overlay's theme resources, so colour changes apply to it live. Move it by its title bar,
/// resize it from any edge or corner, and press "Dock" to put the panel back into the overlay.
/// </summary>
public sealed class WidgetWindow : Window
{
    private readonly Border _host = new() { Padding = new Thickness(14, 12, 14, 14) };
    private readonly Grid _zoomTarget = new();
    private readonly Border _dockBtn;
    private IntPtr _hwnd;

    public string Key { get; }
    public event Action<string>? DockRequested;
    public event Action<string, Rect>? GeometryChanged;

    public WidgetWindow(string key, string title, ResourceDictionary theme, UIElement content, WidgetState st)
    {
        Key = key;
        try { Resources.MergedDictionaries.Add(theme); }  // shared instance: theme changes propagate here too
        catch (Exception ex)
        {
            Log.Warn($"Could not share theme with widget, copying instead: {ex.Message}");
            foreach (System.Collections.DictionaryEntry de in theme) Resources[de.Key] = de.Value;
        }

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Title = "Museek — " + title;
        MinWidth = 260; MinHeight = 170;
        Left = st.X; Top = st.Y;
        Width = Math.Max(MinWidth, st.W); Height = Math.Max(MinHeight, st.H);
        FontFamily = new FontFamily(SettingsStore.Current.EffectiveFont);
        SetResourceReference(ForegroundProperty, "Fg");

        var r = Math.Clamp(SettingsStore.Current.CornerRadius, 4, 22);

        var root = new Grid { Margin = new Thickness(12) };
        root.Children.Add(new Border   // cheap fake shadow (no blur effect)
        {
            CornerRadius = new CornerRadius(r + 4), Margin = new Thickness(-4, -2, -4, -8),
            Background = new SolidColorBrush(Color.FromArgb(0x12, 0, 0, 0)),
        });

        var panel = new Border
        {
            CornerRadius = new CornerRadius(r), BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x1F, 255, 255, 255)),
        };
        panel.SetResourceReference(Border.BackgroundProperty, "Panel");

        var grid = _zoomTarget;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // ---- title bar ---------------------------------------------------------
        var bar = new Border
        {
            Height = 34, CornerRadius = new CornerRadius(r, r, 0, 0), Padding = new Thickness(14, 0, 8, 0),
            BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = new SolidColorBrush(Color.FromArgb(0x1A, 255, 255, 255)),
        };
        bar.SetResourceReference(Border.BackgroundProperty, "TitleBar");

        var barGrid = new Grid();
        var titleText = new TextBlock
        {
            Text = title.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        titleText.SetResourceReference(TextBlock.ForegroundProperty, "FgDim");

        var dockText = new TextBlock { Text = "Dock", FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White };
        _dockBtn = new Border
        {
            CornerRadius = new CornerRadius(9), Padding = new Thickness(10, 2, 10, 2), Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Put this panel back into the overlay", Child = dockText,
        };
        _dockBtn.SetResourceReference(Border.BackgroundProperty, "Accent");
        _dockBtn.MouseLeftButtonUp += (_, e) => { DockRequested?.Invoke(Key); e.Handled = true; };

        barGrid.Children.Add(titleText);
        barGrid.Children.Add(_dockBtn);
        bar.Child = barGrid;
        Grid.SetRow(bar, 0);

        Grid.SetRow(_host, 1);
        _host.Child = content;

        grid.Children.Add(bar);
        grid.Children.Add(_host);
        panel.Child = grid;
        root.Children.Add(panel);

        // resize grip hint
        root.Children.Add(new Path
        {
            Data = Geometry.Parse("M 12,2 L 12,12 L 2,12 Z"), Width = 12, Height = 12, Stretch = Stretch.Fill,
            Fill = new SolidColorBrush(Color.FromArgb(0x66, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 6, 5), IsHitTestVisible = false,
        });
        Content = root;

        // move (title bar, anywhere except the Dock button) + resize (all edges / corners)
        var drag = new DragController(this, 12, d => IsInside(d, _dockBtn)) { Completed = ReportGeometry };
        drag.Attach(bar);
        WindowResizer.Attach(this, root, () => true, ReportGeometry);

        SetZoom(SettingsStore.Current.UiScale);

        SourceInitialized += (_, _) =>
        {
            _hwnd = WinNative.Handle(this);
            WinNative.SetExStyle(_hwnd, WinNative.WS_EX_TOOLWINDOW | WinNative.WS_EX_NOACTIVATE, true);
            ForceTopmost();
        };
    }

    private void ReportGeometry() => GeometryChanged?.Invoke(Key, new Rect(Left, Top, Width, Height));

    private static bool IsInside(DependencyObject d, DependencyObject ancestor)
    {
        for (DependencyObject? o = d; o != null;
             o = (o is Visual || o is System.Windows.Media.Media3D.Visual3D) ? VisualTreeHelper.GetParent(o) : LogicalTreeHelper.GetParent(o))
            if (o == ancestor) return true;
        return false;
    }

    /// <summary>Release the hosted panel so it can be re-parented back into the overlay.</summary>
    public void Detach() => _host.Child = null;

    /// <summary>Text &amp; interface size, same as the main overlay.</summary>
    public void SetZoom(double k)
    {
        k = Math.Clamp(k, 0.7, 1.6);
        _zoomTarget.LayoutTransform = Math.Abs(k - 1) < 0.005 ? Transform.Identity : new ScaleTransform(k, k);
    }

    public void ForceTopmost() { if (IsVisible) WinNative.ForceTopmost(_hwnd, Topmost); }

    public void SetClickThrough(bool on) => WinNative.SetExStyle(_hwnd, WinNative.WS_EX_TRANSPARENT, on);
}
