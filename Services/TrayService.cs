using System;
using System.Drawing;
using System.Windows.Forms;

namespace Museek.Services;

/// <summary>System tray icon + menu (WinForms NotifyIcon).</summary>
public sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _icon;

    public TrayService(Action show, Action hide, Action toggleLock, Action openSettings, Action quit)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show overlay", null, (_, _) => show());
        menu.Items.Add("Hide overlay", null, (_, _) => hide());
        menu.Items.Add("Lock / unlock position", null, (_, _) => toggleLock());
        menu.Items.Add("Settings...", null, (_, _) => openSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit Museek", null, (_, _) => quit());

        _icon = new NotifyIcon
        {
            Icon = MakeIcon(),
            Text = "Museek",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => show();
        Log.Info("System tray icon started.");
    }

    public void Notify(string message, string title = "Museek")
    {
        try { _icon.ShowBalloonTip(3000, title, message, ToolTipIcon.None); }
        catch (Exception ex) { Log.Warn($"Tray notification failed: {ex.Message}"); }
    }

    // Green circle with a white music note, like the original's generated icon.
    private static Icon MakeIcon()
    {
        using var bmp = new Bitmap(64, 64);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            g.FillEllipse(new SolidBrush(Color.FromArgb(29, 185, 84)), 4, 4, 56, 56);
            g.FillEllipse(Brushes.White, 20, 34, 16, 12);
            g.FillRectangle(Brushes.White, 33, 14, 4, 26);
            g.FillRectangle(Brushes.White, 33, 14, 14, 5);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
