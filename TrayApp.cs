using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace NetSpeedTray;

sealed class TrayApp : ApplicationContext
{
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    static readonly (string name, FontStyle style) FontSel = PickFont();

    readonly Settings st = Settings.Load();
    readonly History hist = History.Load();
    readonly NotifyIcon ni = new();
    readonly NotifyIcon ni2 = new();   // second icon, only used in "separate icons" mode
    readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    readonly Dictionary<string, (long rx, long tx)> last = new();
    NetworkInterface[] nics = Array.Empty<NetworkInterface>();
    DateTime lastNicRefresh = DateTime.MinValue, lastTick = DateTime.UtcNow, lastSave = DateTime.UtcNow;
    double up, down;
    string lastKey = "";
    Icon curIcon, curIcon2;
    HistoryForm form;
    AppTracker tracker;
    bool trackingOn;
    readonly bool admin = IsAdmin();

    static (string, FontStyle) PickFont()
    {
        foreach (var n in new[] { "Bahnschrift SemiBold Condensed", "Arial Narrow" })
        {
            try { using var f = new FontFamily(n); return (n, n.StartsWith("Bahn") ? FontStyle.Regular : FontStyle.Bold); }
            catch { }
        }
        return ("Segoe UI", FontStyle.Bold);
    }

    static bool IsAdmin()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public TrayApp()
    {
        if (admin)
        {
            tracker = new AppTracker(hist);
            trackingOn = tracker.Start();
        }

        var menu = new ContextMenuStrip();
        menu.Opening += (_, _) => BuildMenu(menu);
        foreach (var n in new[] { ni, ni2 })
        {
            n.ContextMenuStrip = menu;
            n.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowHistory(); };
            n.Text = "Network speed";
        }
        ni.Visible = true;

        timer.Tick += (_, _) => Tick();
        timer.Start();
        Tick();
    }

    string TrackStatus() =>
        trackingOn ? $"Per-app tracking is ON (method: {tracker.Mode}). {tracker.Events} network events captured so far. Counts start from when tracking was first enabled."
        : admin ? "Per-app tracking FAILED to start. Right-click the tray icon > \"Per-app tracking failed - show details\"."
        : "Per-app tracking is OFF. Right-click the tray icon > \"Enable per-app tracking\" (needs admin).";

    void Tick()
    {
        var now = DateTime.UtcNow;
        double dt = (now - lastTick).TotalSeconds;
        lastTick = now;
        if (dt < 0.1) return;

        if ((now - lastNicRefresh).TotalSeconds > 10)
        {
            try
            {
                nics = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                                n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                                n.NetworkInterfaceType != NetworkInterfaceType.Tunnel).ToArray();
            }
            catch { }
            lastNicRefresh = now;
        }

        long dRx = 0, dTx = 0;
        foreach (var n in nics)
        {
            try
            {
                var s = n.GetIPv4Statistics();
                long rx = s.BytesReceived, tx = s.BytesSent;
                if (last.TryGetValue(n.Id, out var p))
                {
                    if (rx >= p.rx) dRx += rx - p.rx;
                    if (tx >= p.tx) dTx += tx - p.tx;
                }
                last[n.Id] = (rx, tx);
            }
            catch { }
        }
        down = dRx / dt; up = dTx / dt;
        hist.Add(History.TotalKey, dRx, dTx);
        UpdateIcon();

        if ((now - lastSave).TotalSeconds > 60) { hist.Save(); lastSave = now; }
    }

    Line[] BuildLines()
    {
        if (st.TotalOnly && !st.SeparateIcons)
        {
            var (n, u) = Fmt.Parts(up + down, st);
            return new[] { new Line(n, u, st.TotalColorArgb, st.TotalArrowColorArgb, 2) };
        }
        var (un, uu) = Fmt.Parts(up, st);
        var (dn, du) = Fmt.Parts(down, st);
        var ul = new Line(un, uu, st.UploadColorArgb, st.UploadArrowColorArgb, 0);
        var dl = new Line(dn, du, st.DownloadColorArgb, st.DownloadArrowColorArgb, 1);
        return st.UploadOnTop ? new[] { ul, dl } : new[] { dl, ul };
    }

    void UpdateIcon()
    {
        var lines = BuildLines();
        string key = st.SeparateIcons + "|" + string.Join("|", lines.Select(l => l.Text));
        if (key == lastKey) return;
        lastKey = key;

        string tip = $"Upload \u2191 {Fmt.Long(up, st)}\nDownload \u2193 {Fmt.Long(down, st)}\nTotal {Fmt.Long(up + down, st)}";
        if (tip.Length > 63) tip = tip.Substring(0, 63);
        ni.Text = tip; ni2.Text = tip;

        if (st.SeparateIcons)
        {
            SetIcon(ref curIcon, ni, RenderBig(lines[0]));
            SetIcon(ref curIcon2, ni2, RenderBig(lines[1]));
            ni2.Visible = true;
        }
        else
        {
            ni2.Visible = false;
            SetIcon(ref curIcon, ni, lines.Length == 1 ? RenderBig(lines[0]) : RenderTwo(lines));
        }
    }

    static void SetIcon(ref Icon slot, NotifyIcon target, Icon icon)
    {
        var old = slot;
        slot = icon;
        target.Icon = icon;
        old?.Dispose();
    }

    static Icon ToIcon(Bitmap bmp)
    {
        IntPtr h = bmp.GetHicon();
        try { using var tmp = Icon.FromHandle(h); return (Icon)tmp.Clone(); }
        finally { DestroyIcon(h); }
    }

    static void Tri(Graphics g, Brush b, float cx, float cy, float w, float h, bool up)
    {
        PointF[] p = up
            ? new[] { new PointF(cx, cy - h / 2), new PointF(cx - w / 2, cy + h / 2), new PointF(cx + w / 2, cy + h / 2) }
            : new[] { new PointF(cx, cy + h / 2), new PointF(cx - w / 2, cy - h / 2), new PointF(cx + w / 2, cy - h / 2) };
        g.FillPolygon(b, p);
    }

    static void Prep(Graphics g)
    {
        g.Clear(Color.Transparent);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.SmoothingMode = SmoothingMode.AntiAlias;
    }

    // One icon, one big number on top, small arrow + unit letter underneath.
    Icon RenderBig(Line ln)
    {
        using var bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            Prep(g);
            var sf = StringFormat.GenericTypographic;
            Font f = null;
            if (st.FontPx > 0) f = new Font(FontSel.name, st.FontPx, FontSel.style, GraphicsUnit.Pixel);
            else
            {
                for (float size = 26; size >= 8; size -= 0.5f)
                {
                    f?.Dispose();
                    f = new Font(FontSel.name, size, FontSel.style, GraphicsUnit.Pixel);
                    if (g.MeasureString(ln.Num, f, 100, sf).Width <= 32) break;
                }
            }
            using var tb = new SolidBrush(Color.FromArgb(ln.TextArgb));
            using var ab = new SolidBrush(Color.FromArgb(ln.ArrowArgb));
            var sz = g.MeasureString(ln.Num, f, 100, sf);
            g.DrawString(ln.Num, f, tb, (32 - sz.Width) / 2f, (22 - sz.Height) / 2f, sf);
            f.Dispose();

            // bottom row: arrow(s) + unit letter
            using var uf = new Font(FontSel.name, 10, FontSel.style, GraphicsUnit.Pixel);
            var us = g.MeasureString(ln.Unit, uf, 100, sf);
            float arrowW = ln.Kind == 2 ? 11 : 6;
            float total = arrowW + 2 + us.Width;
            float x0 = (32 - total) / 2f, cy = 27;
            if (ln.Kind == 2) { Tri(g, ab, x0 + 2.5f, cy, 5, 6, true); Tri(g, ab, x0 + 8.5f, cy, 5, 6, false); }
            else Tri(g, ab, x0 + 3, cy, 6, 6, ln.Kind == 0);
            g.DrawString(ln.Unit, uf, tb, x0 + arrowW + 2, cy - us.Height / 2f, sf);
        }
        return ToIcon(bmp);
    }

    // One icon, two lines: upload and download.
    Icon RenderTwo(Line[] lines)
    {
        const int Arrow = 7, Band = 16;
        int textRight = 32 - Arrow - 1;
        using var bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            Prep(g);
            var sf = StringFormat.GenericTypographic;
            Font f = null;
            if (st.FontPx > 0) f = new Font(FontSel.name, st.FontPx, FontSel.style, GraphicsUnit.Pixel);
            else
            {
                for (float size = 18; size >= 7; size -= 0.5f)
                {
                    f?.Dispose();
                    f = new Font(FontSel.name, size, FontSel.style, GraphicsUnit.Pixel);
                    if (lines.Max(l => g.MeasureString(l.Text, f, 100, sf).Width) <= textRight) break;
                }
            }
            for (int i = 0; i < 2; i++)
            {
                var ln = lines[i];
                using var tb = new SolidBrush(Color.FromArgb(ln.TextArgb));
                using var ab = new SolidBrush(Color.FromArgb(ln.ArrowArgb));
                var sz = g.MeasureString(ln.Text, f, 100, sf);
                float y0 = i * Band;
                g.DrawString(ln.Text, f, tb, textRight - sz.Width, y0 + (Band - sz.Height) / 2f, sf);
                Tri(g, ab, 32 - Arrow / 2f, y0 + Band / 2f, 7, 7, ln.Kind == 0);
            }
            f.Dispose();
        }
        return ToIcon(bmp);
    }

    void BuildMenu(ContextMenuStrip m)
    {
        m.Items.Clear();
        m.Items.Add("Open history", null, (_, _) => ShowHistory());

        var show = new ToolStripMenuItem("Show");
        void AddShow(string text, bool total, bool sep) =>
            show.DropDownItems.Add(new ToolStripMenuItem(text, null, (_, _) => { st.TotalOnly = total; st.SeparateIcons = sep; Changed(); })
            { Checked = st.TotalOnly == total && st.SeparateIcons == sep });
        AddShow("Upload + download (one icon, two lines)", false, false);
        AddShow("Total only (one icon, bigger text)", true, false);
        AddShow("Upload and download as two icons (biggest text)", false, true);
        m.Items.Add(show);

        var colors = new ToolStripMenuItem("Colors");
        string[] names = { "Upload text", "Download text", "Upload arrow", "Download arrow", "Total text", "Total arrow" };
        for (int i = 0; i < names.Length; i++)
        {
            int which = i;
            colors.DropDownItems.Add(names[i] + "...", null, (_, _) => PickColor(which));
        }
        m.Items.Add(colors);

        var size = new ToolStripMenuItem("Text size");
        void AddSize(string text, int px) =>
            size.DropDownItems.Add(new ToolStripMenuItem(text, null, (_, _) => { st.FontPx = px; Changed(); }) { Checked = st.FontPx == px });
        AddSize("Auto (largest that fits)", 0);
        foreach (int px in new[] { 8, 10, 12, 14, 16, 18, 20, 22, 24, 26, 28, 30, 32 }) AddSize(px + " px", px);
        m.Items.Add(size);

        var unit = new ToolStripMenuItem("Unit");
        void AddUnit(string text, string val) =>
            unit.DropDownItems.Add(new ToolStripMenuItem(text, null, (_, _) => { st.Unit = val; Changed(); }) { Checked = st.Unit == val });
        AddUnit("Auto (KB \u2194 MB)", "Auto");
        AddUnit("Always KB / kbps", "K");
        AddUnit("Always MB / Mbps", "M");
        m.Items.Add(unit);

        m.Items.Add(new ToolStripMenuItem("Use bits (kbps / Mbps)", null, (_, _) => { st.Bits = !st.Bits; Changed(); }) { Checked = st.Bits });
        m.Items.Add(new ToolStripMenuItem("Upload first (off = download first)", null, (_, _) => { st.UploadOnTop = !st.UploadOnTop; Changed(); }) { Checked = st.UploadOnTop });
        m.Items.Add(new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleAutostart()) { Checked = IsAutostart() });
        m.Items.Add(new ToolStripSeparator());

        if (trackingOn) m.Items.Add(new ToolStripMenuItem("Per-app tracking: ON (" + tracker.Mode + ")") { Enabled = false });
        else if (admin) m.Items.Add("Per-app tracking failed - show details", null, (_, _) => ShowTrackError());
        else m.Items.Add("Enable per-app tracking (restart as admin)", null, (_, _) => RestartAsAdmin());

        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Exit", null, (_, _) => Quit());
    }

    void ShowTrackError()
    {
        string e = tracker?.Error ?? "Unknown error";
        if (e.Length > 1800) e = e.Substring(0, 1800) + "...";
        MessageBox.Show("Full log: " + Path.Combine(Settings.Dir, "tracking-error.log") + "\n\n" + e,
            "Per-app tracking error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    void Changed() { st.Save(); lastKey = ""; UpdateIcon(); }

    void PickColor(int which)
    {
        int cur = which switch
        {
            0 => st.UploadColorArgb, 1 => st.DownloadColorArgb, 2 => st.UploadArrowColorArgb,
            3 => st.DownloadArrowColorArgb, 4 => st.TotalColorArgb, _ => st.TotalArrowColorArgb
        };
        using var dlg = new ColorDialog { Color = Color.FromArgb(cur), FullOpen = true };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        int c = dlg.Color.ToArgb();
        switch (which)
        {
            case 0: st.UploadColorArgb = c; break;
            case 1: st.DownloadColorArgb = c; break;
            case 2: st.UploadArrowColorArgb = c; break;
            case 3: st.DownloadArrowColorArgb = c; break;
            case 4: st.TotalColorArgb = c; break;
            default: st.TotalArrowColorArgb = c; break;
        }
        Changed();
    }

    void ShowHistory()
    {
        if (form != null && !form.IsDisposed) { form.Activate(); return; }
        form = new HistoryForm(hist, TrackStatus);
        form.FormClosed += (_, _) => form = null;
        form.Show();
    }

    static bool IsAutostart()
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue("NetSpeedTray") != null; }
        catch { return false; }
    }

    static void ToggleAutostart()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (k == null) return;
            if (k.GetValue("NetSpeedTray") != null) k.DeleteValue("NetSpeedTray");
            else k.SetValue("NetSpeedTray", "\"" + Environment.ProcessPath + "\"");
        }
        catch { }
    }

    void RestartAsAdmin()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath)
            { UseShellExecute = true, Verb = "runas", Arguments = "--elevated" });
            Quit();
        }
        catch (Win32Exception) { /* user cancelled the UAC prompt */ }
    }

    void Quit()
    {
        timer.Stop();
        tracker?.Dispose();
        hist.Save();
        st.Save();
        ni.Visible = false; ni2.Visible = false;
        ni.Dispose(); ni2.Dispose();
        curIcon?.Dispose(); curIcon2?.Dispose();
        ExitThread();
    }
}
