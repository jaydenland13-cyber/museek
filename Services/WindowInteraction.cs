using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Museek.Services;

/// <summary>
/// Moves a borderless window by dragging a handle. Works in raw screen pixels (no DPI maths), keeps the
/// *visible card* (not the transparent shadow margin) inside the monitor, and snaps to screen edges.
/// </summary>
public sealed class DragController
{
    private readonly Window _win;
    private readonly int _marginDip;
    private readonly Func<DependencyObject, bool>? _ignore;
    private bool _dragging;
    private WinNative.RECT _rect;
    private System.Drawing.Point _cursor;

    public Func<bool>? Locked { get; set; }
    public Action? Completed { get; set; }
    public bool IsDragging => _dragging;

    public DragController(Window win, int marginDip, Func<DependencyObject, bool>? ignore = null)
    {
        _win = win; _marginDip = marginDip; _ignore = ignore;
    }

    public void Attach(UIElement handle)
    {
        handle.MouseLeftButtonDown += Down;
        handle.MouseMove += Move;
        handle.MouseLeftButtonUp += Up;
    }

    private void Down(object sender, MouseButtonEventArgs e)
    {
        if (Locked?.Invoke() == true) return;
        if (e.OriginalSource is not DependencyObject d || (_ignore?.Invoke(d) ?? false)) return;
        var h = WinNative.Handle(_win);
        if (!WinNative.GetWindowRect(h, out _rect)) return;
        _cursor = System.Windows.Forms.Cursor.Position;
        _dragging = true;
        ((UIElement)sender).CaptureMouse();
        e.Handled = true;
    }

    private void Move(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        if (e.LeftButton != MouseButtonState.Pressed) { End(sender); return; }

        var c = System.Windows.Forms.Cursor.Position;
        int w = _rect.Right - _rect.Left, h = _rect.Bottom - _rect.Top;
        int x = _rect.Left + (c.X - _cursor.X);
        int y = _rect.Top + (c.Y - _cursor.Y);

        var scr = System.Windows.Forms.Screen.FromPoint(c);
        var b = scr.Bounds;
        var wa = scr.WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(_win).DpiScaleX;
        int m = (int)Math.Round(_marginDip * dpi);
        int snap = (int)Math.Round(14 * dpi);

        // snap the visible card to the work-area edges (so it sits flush, but never under the taskbar by accident)
        int vl = x + m, vr = x + w - m, vt = y + m, vb = y + h - m;
        if (Math.Abs(vl - wa.Left) < snap) x = wa.Left - m;
        else if (Math.Abs(vr - wa.Right) < snap) x = wa.Right - (w - m);
        if (Math.Abs(vt - wa.Top) < snap) y = wa.Top - m;
        else if (Math.Abs(vb - wa.Bottom) < snap) y = wa.Bottom - (h - m);

        // hard limit: the visible card may touch (but not leave) the monitor under the cursor
        x = Math.Max(b.Left - m, Math.Min(x, b.Right - w + m));
        y = Math.Max(b.Top - m, Math.Min(y, b.Bottom - h + m));

        WinNative.SetWindowPos(WinNative.Handle(_win), IntPtr.Zero, x, y, 0, 0,
            WinNative.SWP_NOSIZE | WinNative.SWP_NOZORDER | WinNative.SWP_NOACTIVATE);
    }

    private void Up(object sender, MouseButtonEventArgs e) { if (_dragging) End(sender); }

    private void End(object sender)
    {
        _dragging = false;
        ((UIElement)sender).ReleaseMouseCapture();
        Completed?.Invoke();
    }
}

/// <summary>
/// Adds invisible resize zones on all four edges and four corners of a borderless window, so it can be
/// resized freely (width and height independently) like a normal window.
/// </summary>
public sealed class WindowResizer
{
    [Flags] private enum Edge { None = 0, L = 1, R = 2, T = 4, B = 8 }

    private readonly Window _win;
    private readonly Func<bool> _allowVertical;
    private readonly Action _completed;
    private Edge _edge;
    private bool _active;
    private WinNative.RECT _rect;
    private System.Drawing.Point _cursor;

    private WindowResizer(Window win, Func<bool> allowVertical, Action completed)
    {
        _win = win; _allowVertical = allowVertical; _completed = completed;
    }

    /// <param name="host">The grid that fills the visible card area (inside the transparent shadow margin).</param>
    public static void Attach(Window win, Grid host, Func<bool> allowVertical, Action completed)
    {
        var r = new WindowResizer(win, allowVertical, completed);
        const double t = 6, c = 14;
        r.Zone(host, Edge.R, HorizontalAlignment.Right, VerticalAlignment.Stretch, t, double.NaN, new Thickness(0, c, 0, c), Cursors.SizeWE);
        r.Zone(host, Edge.L, HorizontalAlignment.Left, VerticalAlignment.Stretch, t, double.NaN, new Thickness(0, c, 0, c), Cursors.SizeWE);
        r.Zone(host, Edge.T, HorizontalAlignment.Stretch, VerticalAlignment.Top, double.NaN, t, new Thickness(c, 0, c, 0), Cursors.SizeNS);
        r.Zone(host, Edge.B, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, double.NaN, t, new Thickness(c, 0, c, 0), Cursors.SizeNS);
        r.Zone(host, Edge.T | Edge.L, HorizontalAlignment.Left, VerticalAlignment.Top, c, c, new Thickness(0), Cursors.SizeNWSE);
        r.Zone(host, Edge.T | Edge.R, HorizontalAlignment.Right, VerticalAlignment.Top, c, c, new Thickness(0), Cursors.SizeNESW);
        r.Zone(host, Edge.B | Edge.L, HorizontalAlignment.Left, VerticalAlignment.Bottom, c, c, new Thickness(0), Cursors.SizeNESW);
        r.Zone(host, Edge.B | Edge.R, HorizontalAlignment.Right, VerticalAlignment.Bottom, c, c, new Thickness(0), Cursors.SizeNWSE);
    }

    private void Zone(Grid host, Edge edge, HorizontalAlignment ha, VerticalAlignment va, double w, double h, Thickness margin, Cursor cursor)
    {
        var z = new Border { Background = Brushes.Transparent, HorizontalAlignment = ha, VerticalAlignment = va, Margin = margin, Cursor = cursor };
        if (!double.IsNaN(w)) z.Width = w;
        if (!double.IsNaN(h)) z.Height = h;
        Panel.SetZIndex(z, 100);
        z.MouseLeftButtonDown += (s, e) => Begin(edge, (UIElement)s, e);
        z.MouseMove += Move;
        z.MouseLeftButtonUp += (s, _) => End((UIElement)s);
        host.Children.Add(z);
    }

    private void Begin(Edge edge, UIElement zone, MouseButtonEventArgs e)
    {
        if (!_allowVertical()) edge &= ~(Edge.T | Edge.B);      // mini view: height follows the content
        if (edge == Edge.None) return;
        if (!WinNative.GetWindowRect(WinNative.Handle(_win), out _rect)) return;
        _edge = edge;
        _cursor = System.Windows.Forms.Cursor.Position;
        _active = true;
        zone.CaptureMouse();
        e.Handled = true;
    }

    private void Move(object sender, MouseEventArgs e)
    {
        if (!_active) return;
        if (e.LeftButton != MouseButtonState.Pressed) { End((UIElement)sender); return; }

        var c = System.Windows.Forms.Cursor.Position;
        int dx = c.X - _cursor.X, dy = c.Y - _cursor.Y;
        var dpi = VisualTreeHelper.GetDpi(_win);
        int minW = (int)(_win.MinWidth * dpi.DpiScaleX), minH = (int)(_win.MinHeight * dpi.DpiScaleY);

        int l = _rect.Left, t = _rect.Top, r = _rect.Right, b = _rect.Bottom;
        if (_edge.HasFlag(Edge.L)) l = Math.Min(_rect.Left + dx, r - minW);
        if (_edge.HasFlag(Edge.R)) r = Math.Max(_rect.Right + dx, l + minW);
        if (_edge.HasFlag(Edge.T)) t = Math.Min(_rect.Top + dy, b - minH);
        if (_edge.HasFlag(Edge.B)) b = Math.Max(_rect.Bottom + dy, t + minH);

        WinNative.SetWindowPos(WinNative.Handle(_win), IntPtr.Zero, l, t, r - l, b - t,
            WinNative.SWP_NOZORDER | WinNative.SWP_NOACTIVATE);
    }

    private void End(UIElement zone)
    {
        if (!_active) return;
        _active = false;
        zone.ReleaseMouseCapture();
        _completed();
    }
}
