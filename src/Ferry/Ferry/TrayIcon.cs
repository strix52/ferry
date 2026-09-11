using System.Drawing;
using System.Windows.Forms;

namespace Ferry;

// The notification-area icon. WPF has no tray control, so this is the one place
// Ferry reaches into Windows Forms; nothing else in the app touches it.
//
// Ok says whether the icon actually made it into the tray. MainWindow reads it
// before deciding to hide on close: with no tray icon, hiding would leave the
// user no way back to the window and no way to quit.
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon? _icon;
    private bool _disposed;

    public bool Ok => _icon is not null;

    public TrayIcon(string tooltip, Action onShow, Action onSendClipboard, Action onExit)
    {
        try
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Show Ferry", null, (_, _) => onShow());
            menu.Items.Add("Send clipboard", null, (_, _) => onSendClipboard());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (_, _) => onExit());

            _icon = new NotifyIcon
            {
                // The exe's own icon, so the tray, the taskbar and Explorer all
                // show the same mark without shipping a second copy of it.
                Icon = LoadIcon(),
                Text = tooltip,
                ContextMenuStrip = menu,
                Visible = true,
            };
            menu.Renderer = new PassageRenderer();
            _icon.DoubleClick += (_, _) => onShow();
        }
        catch (Exception ex)
        {
            // A missing icon or a shell that refuses the notification area is
            // not worth taking the app down for.
            App.Log($"tray icon failed: {ex.Message}");
            _icon = null;
        }
    }

    private static Icon LoadIcon()
    {
        var path = Environment.ProcessPath;
        if (path is not null && Icon.ExtractAssociatedIcon(path) is { } icon) return icon;
        return SystemIcons.Application;
    }

    // Windows may silently drop these when focus assist is on; they are a
    // courtesy, never the only feedback for an action.
    public void Notify(string title, string message)
    {
        if (_icon is null) return;
        try
        {
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = message;
            _icon.ShowBalloonTip(3000);
        }
        catch (Exception ex) { App.Log($"tray balloon failed: {ex.Message}"); }
    }

    // The tray menu is the only part of Ferry that Windows Forms paints, and the
    // default is a white strip with a grey gutter down its left edge — next to a
    // Mica window in Passage ink it looks like a menu borrowed from another app.
    //
    // Every colour is read from the live palette at paint time rather than
    // cached in a field, so the menu follows a theme switch the same way the
    // WPF side does, with no wiring to App.ThemeChanged.
    private static Color Token(string key, string fallback)
    {
        if (System.Windows.Application.Current?.TryFindResource(key)
            is System.Windows.Media.Color c)
            return Color.FromArgb(c.A, c.R, c.G, c.B);
        return ColorTranslator.FromHtml(fallback);
    }

    private sealed class PassageColors : ProfessionalColorTable
    {
        public PassageColors() => UseSystemColors = false;

        public override Color ToolStripDropDownBackground => Token("SurfaceColor", "#161D1B");
        public override Color MenuBorder => Token("BorderHiColor", "#3B4A45");

        // The image margin is the gutter WinForms reserves for item icons.
        // Ferry has none, so it is painted flat with the menu ground instead of
        // showing as a stripe.
        public override Color ImageMarginGradientBegin => Token("SurfaceColor", "#161D1B");
        public override Color ImageMarginGradientMiddle => Token("SurfaceColor", "#161D1B");
        public override Color ImageMarginGradientEnd => Token("SurfaceColor", "#161D1B");

        public override Color MenuItemSelected => Token("Surface3Color", "#222D29");
        public override Color MenuItemSelectedGradientBegin => Token("Surface3Color", "#222D29");
        public override Color MenuItemSelectedGradientEnd => Token("Surface3Color", "#222D29");
        public override Color MenuItemBorder => Token("Surface3Color", "#222D29");

        public override Color SeparatorDark => Token("BorderColor", "#2B3733");
        public override Color SeparatorLight => Token("BorderColor", "#2B3733");
    }

    private sealed class PassageRenderer : ToolStripProfessionalRenderer
    {
        public PassageRenderer() : base(new PassageColors()) { }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item?.Enabled == false
                ? Token("FaintColor", "#84918C")
                : Token("InkColor", "#ECF1EE");
            base.OnRenderItemText(e);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Without the explicit Visible = false the icon lingers in the tray
        // until the user waves the mouse over it.
        if (_icon is not null)
        {
            _icon.Visible = false;
            _icon.Dispose();
        }
    }
}
