using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Ember.Models;

namespace Ember.Services;

public sealed class TrayService : IDisposable
{
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);

    private readonly NotifyIcon _icon;
    private readonly Icon _appIcon;
    private IntPtr _dynIconHandle;
    private string _lastTempText = "";

    private readonly ToolStripMenuItem _overlayItem, _lockItem, _unitC, _unitF;
    private readonly ToolStripMenuItem _pill, _card, _rings;
    private readonly ToolStripMenuItem _updateItem;
    private readonly ToolStripSeparator _updateSeparator;

    public event Action? OpenRequested, ToggleOverlay, ToggleLock, ExitRequested, UpdateRequested;
    public event Action<OverlayLayout>? LayoutRequested;
    /// <summary>A preset chosen from the "Overlay presets" submenu (its id).</summary>
    public event Action<string>? PresetRequested;
    private readonly ToolStripMenuItem _presets;
    public event Action<TempUnit>? UnitRequested;

    public TrayService()
    {
        var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/ember.ico"))!.Stream;
        _appIcon = new Icon(stream, SystemInformation.SmallIconSize);

        var menu = new ContextMenuStrip { Renderer = new DarkRenderer(), ShowImageMargin = false, ShowCheckMargin = true };
        menu.Font = new Font("Segoe UI", 9.5f);

        var open = new ToolStripMenuItem("Open Ember") { Font = new Font("Segoe UI Semibold", 9.5f) };
        open.Click += (_, _) => OpenRequested?.Invoke();
        _overlayItem = new ToolStripMenuItem("Show overlay");
        _overlayItem.Click += (_, _) => ToggleOverlay?.Invoke();
        _lockItem = new ToolStripMenuItem("Lock overlay (click-through)");
        _lockItem.Click += (_, _) => ToggleLock?.Invoke();

        var layout = new ToolStripMenuItem("Overlay style");
        _pill = new ToolStripMenuItem("Pill"); _pill.Click += (_, _) => LayoutRequested?.Invoke(OverlayLayout.Pill);
        _card = new ToolStripMenuItem("Card"); _card.Click += (_, _) => LayoutRequested?.Invoke(OverlayLayout.Card);
        _rings = new ToolStripMenuItem("Rings"); _rings.Click += (_, _) => LayoutRequested?.Invoke(OverlayLayout.Rings);
        layout.DropDownItems.AddRange(new ToolStripItem[] { _pill, _card, _rings });
        StyleDropDown(layout);

        // Filled each time the menu opens (presets can change at any time).
        _presets = new ToolStripMenuItem("Overlay presets") { Visible = false };
        _presets.DropDownItems.Add(new ToolStripMenuItem("(none)")); // a submenu needs an item to show its arrow
        StyleDropDown(_presets);

        var unit = new ToolStripMenuItem("Units");
        _unitC = new ToolStripMenuItem("Celsius (°C)"); _unitC.Click += (_, _) => UnitRequested?.Invoke(TempUnit.Celsius);
        _unitF = new ToolStripMenuItem("Fahrenheit (°F)"); _unitF.Click += (_, _) => UnitRequested?.Invoke(TempUnit.Fahrenheit);
        unit.DropDownItems.AddRange(new ToolStripItem[] { _unitC, _unitF });
        StyleDropDown(unit);

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => ExitRequested?.Invoke();

        _updateItem = new ToolStripMenuItem("Update Ember") { Visible = false };
        _updateItem.Click += (_, _) => UpdateRequested?.Invoke();
        _updateSeparator = new ToolStripSeparator { Visible = false };

        menu.Items.AddRange(new ToolStripItem[]
        {
            open, new ToolStripSeparator(), _overlayItem, _lockItem, layout, _presets, unit, new ToolStripSeparator(),
            _updateItem, _updateSeparator, exit
        });
        menu.Opening += (_, _) => Sync();

        _icon = new NotifyIcon
        {
            Icon = _appIcon,
            Text = "Ember",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) OpenRequested?.Invoke(); };
        _icon.BalloonTipClicked += (_, _) => OpenRequested?.Invoke();
    }

    /// <summary>Shows "Update to x.y.z" in the menu, or hides it (null).</summary>
    public void SetUpdate(string? version)
    {
        _updateItem.Visible = _updateSeparator.Visible = version != null;
        if (version != null) _updateItem.Text = $"Update to Ember {version}";
    }

    private static void StyleDropDown(ToolStripMenuItem item)
    {
        if (item.DropDown is ToolStripDropDownMenu dd) { dd.ShowImageMargin = false; dd.ShowCheckMargin = true; dd.Renderer = new DarkRenderer(); }
    }

    private void Sync()
    {
        var s = App.Settings;
        _overlayItem.Checked = s.OverlayVisible;
        // Read fresh every time the menu opens, so it always shows the hotkey as currently set.
        _overlayItem.Text = s.Hotkey.Count > 0 ? $"Show overlay ({HotkeyService.Describe(s.Hotkey)})" : "Show overlay";
        _lockItem.Checked = s.OverlayLocked;
        _pill.Checked = s.OverlayLayout == OverlayLayout.Pill;
        _card.Checked = s.OverlayLayout == OverlayLayout.Card;
        _rings.Checked = s.OverlayLayout == OverlayLayout.Rings;
        _unitC.Checked = s.Unit == TempUnit.Celsius;
        _unitF.Checked = s.Unit == TempUnit.Fahrenheit;

        _presets.Visible = s.OverlayPresets.Count > 0;
        _presets.DropDownItems.Clear();
        foreach (var p in s.OverlayPresets)
        {
            var id = p.Id;
            var text = p.Hotkey.Count > 0 ? $"{p.Name} ({HotkeyService.Describe(p.Hotkey)})" : p.Name;
            var item = new ToolStripMenuItem(text.Replace("&", "&&")) { Checked = s.OverlayVisible && p.Matches(s) };
            item.Click += (_, _) => PresetRequested?.Invoke(id);
            _presets.DropDownItems.Add(item);
        }
        if (_presets.DropDownItems.Count == 0) _presets.DropDownItems.Add(new ToolStripMenuItem("(none)"));
    }

    public void ShowBalloon(string title, string text, bool warning = true)
    {
        var icon = warning ? ToolTipIcon.Warning : ToolTipIcon.Info;
        _icon.BalloonTipIcon = icon;
        _icon.ShowBalloonTip(5000, title, text, icon);
    }

    public void Update(SensorSnapshot snap)
    {
        var tip = $"Ember\nCPU {Temps.Format(snap.CpuTemp)}  ·  GPU {Temps.Format(snap.GpuTemp)}";
        if (snap.RamTemps.Count > 0) tip += $"\nRAM {Temps.Format(snap.RamTemp)}";
        if (tip.Length > 127) tip = tip[..127];
        if (_icon.Text != tip) _icon.Text = tip; // each change is a call into Explorer

        if (!App.Settings.TrayShowsTemp)
        {
            if (_dynIconHandle != IntPtr.Zero) { _icon.Icon = _appIcon; DestroyIcon(_dynIconHandle); _dynIconHandle = IntPtr.Zero; _lastTempText = ""; }
            return;
        }

        var text = Temps.Number(snap.CpuTemp);
        var sev = Temps.Rate(snap.CpuTemp, App.Settings.CpuThresholds);
        var key = text + sev;
        if (key == _lastTempText) return;
        _lastTempText = key;

        int size = Math.Max(32, SystemInformation.SmallIconSize.Width * 2); // render at 2x, shell scales down
        using var bmp = RenderTempIcon(text, sev, size);
        var h = bmp.GetHicon();
        var newIcon = Icon.FromHandle(h);
        _icon.Icon = newIcon;
        if (_dynIconHandle != IntPtr.Zero) DestroyIcon(_dynIconHandle);
        _dynIconHandle = h;
    }

    /// <summary>Draws a temperature number that fills a square tray icon.</summary>
    public static Bitmap RenderTempIcon(string text, Severity sev, int size)
    {
        var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            var col = sev switch
            {
                Severity.Hot => Color.FromArgb(255, 0xFF, 0x4D, 0x5E),
                Severity.Warm => Color.FromArgb(255, 0xFF, 0xB0, 0x3B),
                _ => Color.White,
            };
            // Build the digits as a path, then scale that path to fill the icon. This never wraps or
            // clips ("51" used to wrap and show only "5") and keeps 3-digit °F values legible.
            using var path = new GraphicsPath();
            using (var family = new FontFamily("Segoe UI"))
                path.AddString(text, family, (int)FontStyle.Bold, 100f, PointF.Empty, StringFormat.GenericTypographic);
            var b = path.GetBounds();
            if (b.Width > 0 && b.Height > 0)
            {
                float pad = size * 0.04f;
                float scale = Math.Min((size - 2 * pad) / b.Width, (size * 0.78f) / b.Height);
                using var m = new Matrix();
                m.Translate(size / 2f, size / 2f);
                m.Scale(scale, scale);
                m.Translate(-(b.X + b.Width / 2f), -(b.Y + b.Height / 2f));
                path.Transform(m);
            }
            // Dark outline keeps the number readable on light taskbars too.
            using var outline = new Pen(Color.FromArgb(170, 0, 0, 0), size / 10f) { LineJoin = LineJoin.Round };
            g.DrawPath(outline, path);
            using var brush = new SolidBrush(col);
            g.FillPath(brush, path);
        }
        return bmp;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        if (_dynIconHandle != IntPtr.Zero) DestroyIcon(_dynIconHandle);
    }

    // ---------------- dark menu renderer ----------------
    private sealed class DarkRenderer : ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Color.FromArgb(240, 240, 245) : Color.FromArgb(120, 120, 130);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Color.FromArgb(200, 200, 210);
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = e.ImageRectangle;
            var ac = ThemeService.ParseColor(App.Settings.AccentColor, System.Windows.Media.Color.FromRgb(0xB0, 0x7C, 0xFF));
            using var pen = new Pen(Color.FromArgb(255, ac.R, ac.G, ac.B), 2f);
            g.DrawLines(pen, new[]
            {
                new PointF(r.Left + r.Width * 0.2f, r.Top + r.Height * 0.55f),
                new PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.75f),
                new PointF(r.Left + r.Width * 0.8f, r.Top + r.Height * 0.28f),
            });
        }
    }

    private sealed class DarkColors : ProfessionalColorTable
    {
        private static readonly Color Bg = Color.FromArgb(32, 33, 38);
        private static readonly Color Hover = Color.FromArgb(52, 54, 62);
        private static readonly Color Border = Color.FromArgb(60, 62, 70);
        public override Color ToolStripDropDownBackground => Bg;
        public override Color ImageMarginGradientBegin => Bg;
        public override Color ImageMarginGradientMiddle => Bg;
        public override Color ImageMarginGradientEnd => Bg;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Hover;
        public override Color MenuItemPressedGradientEnd => Hover;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Bg;
        public override Color CheckBackground => Bg;
        public override Color CheckSelectedBackground => Hover;
        public override Color CheckPressedBackground => Hover;
    }
}
