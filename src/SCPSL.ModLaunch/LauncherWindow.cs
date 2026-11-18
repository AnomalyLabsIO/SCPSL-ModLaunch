using SCPSL.ModLaunch.Core;

namespace SCPSL.ModLaunch;

internal sealed class LauncherWindow : Form
{
    private readonly string root;
    private readonly List<Label> mutedLabels = [];
    private readonly ThemeToggleButton themeToggle = new() { Size = new Size(36, 36), Margin = Padding.Empty, Anchor = AnchorStyles.Top | AnchorStyles.Right };
    private readonly ToolTip themeToolTip = new();
    private LauncherTheme theme;
    private readonly Label gameVersion = ValueLabel();
    private readonly Label melonVersion = ValueLabel();
    private readonly Label patchStatus = ValueLabel();
    private readonly Label nextStatus = ValueLabel();
    private readonly Label details = new() { AutoSize = true };
    private readonly Label modCount = new() { AutoSize = true, Text = "Checking mods…" };
    private readonly CheckedListBox mods = new() { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle, HorizontalScrollbar = true, AccessibleName = "Detected MelonLoader mods" };
    private readonly CheckBox restoreVanilla = new() { Text = "Restore Vanilla game files on exit.", Checked = true, AutoSize = true, FlatStyle = SystemInformation.HighContrast ? FlatStyle.Standard : FlatStyle.Flat, UseVisualStyleBackColor = SystemInformation.HighContrast, Margin = new Padding(0, 10, 0, 14) };
    private readonly ThemeButton launch = ActionButton("Launch", "Launch game");
    private readonly Button prepare = ActionButton("Patch game", "Prepare game files");
    private readonly Button repair = ActionButton("Repair", "Rebuild preparation");
    private readonly Button restore = ActionButton("Restore originals", "Restore original game files");
    private LauncherOverview? overview;
    private bool updatingMods;
    private bool busy;
    private bool changingMod;
    private bool modListAvailable;

    internal GuiLaunchAction? SelectedAction { get; private set; }
    internal bool RestoreVanillaOnExit => restoreVanilla.Checked;

    internal LauncherWindow(string root)
    {
        this.root = root;
        theme = LauncherThemePreferences.Load(root);
        Text = "SCPSL ModLaunch";
        using (var iconStream = typeof(LauncherWindow).Assembly.GetManifestResourceStream("SCPSL.ModLaunch.Icon.ico")!)
        using (var icon = new Icon(iconStream)) Icon = (Icon)icon.Clone();
        Font = new Font("Segoe UI", 10f);
        launch.Font = new Font(Font, FontStyle.Bold);
        launch.MinimumSize = new Size(112, 40);
        launch.TextImageRelation = TextImageRelation.ImageBeforeText;
        launch.AccessibleDescription = "Start SCP: Secret Laboratory with mods. Main launcher action.";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(576, 570);
        ClientSize = new Size(560, 600);
        AutoScaleMode = AutoScaleMode.Dpi;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 8 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var header = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 0, 0, 8) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(new Label { Text = "SCPSL ModLaunch", Font = new Font(Font.FontFamily, 21f, FontStyle.Bold), AutoSize = true, Margin = Padding.Empty }, 0, 0);
        header.Controls.Add(themeToggle, 1, 0);
        layout.Controls.Add(header, 0, 0);
        var directoryLabel = new Label { Text = root, AutoSize = false, Height = 34, Dock = DockStyle.Top, AutoEllipsis = true, Margin = new Padding(0, 0, 0, 14), AccessibleName = "Game directory" };
        layout.Controls.Add(directoryLabel, 0, 1);
        mutedLabels.AddRange([directoryLabel, details]);
        var summary = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2, RowCount = 4, Margin = new Padding(0, 0, 0, 10) };
        summary.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125));
        summary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddStatus(summary, 0, "Game version", gameVersion);
        AddStatus(summary, 1, "MelonLoader", melonVersion);
        AddStatus(summary, 2, "ModPatch", patchStatus);
        AddStatus(summary, 3, "Next action", nextStatus);
        nextStatus.Font = new Font(Font, FontStyle.Bold);
        layout.Controls.Add(summary, 0, 2);
        details.Text = "Inspecting game files…";
        details.Margin = new Padding(0, 0, 0, 16);
        layout.Controls.Add(details, 0, 3);
        modCount.Font = new Font(Font, FontStyle.Bold);
        modCount.Margin = new Padding(0, 0, 0, 8);
        layout.Controls.Add(modCount, 0, 4);
        layout.Controls.Add(mods, 0, 5);
        layout.Controls.Add(restoreVanilla, 0, 6);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = Padding.Empty };
        actions.Controls.AddRange([launch, prepare, repair, restore]);
        layout.Controls.Add(actions, 0, 7);
        // Keep the existing initial keyboard focus; the theme button remains reachable with Tab.
        header.TabIndex = layout.Controls.Count;
        Controls.Add(layout);
        ClientSizeChanged += (_, _) => details.MaximumSize = new Size(Math.Max(1, layout.ClientSize.Width - layout.Padding.Horizontal), 0);
        details.MaximumSize = new Size(ClientSize.Width - layout.Padding.Horizontal, 0);
        AcceptButton = launch;
        launch.EnabledChanged += (_, _) => ApplyLaunchStyle();
        DpiChanged += (_, _) => ApplyLaunchStyle();
        launch.Click += (_, _) => SelectAction(GuiLaunchAction.Launch);
        prepare.Click += (_, _) => SelectAction(GuiLaunchAction.Prepare);
        repair.Click += (_, _) => SelectAction(GuiLaunchAction.Repair);
        restore.Click += (_, _) => SelectAction(GuiLaunchAction.Restore);
        themeToggle.Click += (_, _) =>
        {
            theme = theme == LauncherTheme.Dark ? LauncherTheme.Light : LauncherTheme.Dark;
            ApplyTheme();
            if (!LauncherThemePreferences.TrySave(root, theme))
            {
                const string message = "This theme could not be saved and applies only to this window.";
                themeToggle.AccessibleDescription = message;
                themeToolTip.SetToolTip(themeToggle, themeToggle.AccessibleName + Environment.NewLine + message);
            }
        };
        mods.ItemCheck += ChangeMod;
        Shown += async (_, _) => await RefreshAsync();
        // Read-only inspection must not prevent closing the window; an in-flight rename must finish.
        FormClosing += (_, e) => { if (changingMod) e.Cancel = true; };
        ApplyTheme();
        SetBusy(true);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            themeToolTip.Dispose();
            var image = launch.Image;
            launch.Image = null;
            image?.Dispose();
        }
        base.Dispose(disposing);
    }

    private void ApplyTheme()
    {
        var palette = ThemePalette.For(theme);
        SuspendLayout();
        try
        {
            ApplyColors(this);
            foreach (var label in mutedLabels) label.ForeColor = palette.MutedText;
            mods.BackColor = palette.Control;
            ApplyLaunchStyle();
            restoreVanilla.FlatStyle = SystemInformation.HighContrast ? FlatStyle.Standard : FlatStyle.Flat;
            restoreVanilla.UseVisualStyleBackColor = SystemInformation.HighContrast;
            themeToggle.ShowSun = theme == LauncherTheme.Dark;
            themeToggle.AccessibleName = themeToggle.ShowSun ? "Switch to light theme" : "Switch to dark theme";
            themeToggle.AccessibleDescription = "Change the launcher theme and remember the selection.";
            themeToolTip.SetToolTip(themeToggle, themeToggle.AccessibleName);
        }
        finally { ResumeLayout(); }
        Invalidate(true);

        void ApplyColors(Control control)
        {
            control.BackColor = palette.Window;
            control.ForeColor = palette.Text;
            if (control is ThemeButton button)
            {
                button.FlatStyle = SystemInformation.HighContrast ? FlatStyle.Standard : FlatStyle.Flat;
                button.BackColor = SystemInformation.HighContrast ? SystemColors.Control : palette.Control;
                button.ForeColor = SystemInformation.HighContrast ? SystemColors.ControlText : palette.Text;
                button.UseVisualStyleBackColor = SystemInformation.HighContrast;
                button.DisabledTextColor = palette.MutedText;
                button.FlatAppearance.BorderColor = palette.Border;
                button.FlatAppearance.MouseOverBackColor = palette.Hover;
                button.FlatAppearance.MouseDownBackColor = palette.Pressed;
            }
            foreach (Control child in control.Controls) ApplyColors(child);
        }
    }

    private void ApplyLaunchStyle()
    {
        var palette = ThemePalette.For(theme);
        bool highContrast = SystemInformation.HighContrast;
        launch.FlatStyle = highContrast ? FlatStyle.Standard : FlatStyle.Flat;
        launch.BackColor = highContrast ? SystemColors.Control : launch.Enabled ? Color.FromArgb(43, 101, 185) : palette.Control;
        launch.ForeColor = highContrast ? SystemColors.ControlText : launch.Enabled ? Color.White : palette.MutedText;
        launch.UseVisualStyleBackColor = highContrast;
        launch.FlatAppearance.BorderColor = highContrast ? SystemColors.WindowText : launch.Enabled ? Color.FromArgb(36, 86, 160) : palette.Border;
        launch.FlatAppearance.MouseOverBackColor = Color.FromArgb(53, 116, 200);
        launch.FlatAppearance.MouseDownBackColor = Color.FromArgb(36, 86, 160);
        launch.DisabledTextColor = palette.MutedText;

        // A vector-drawn play symbol avoids font/emoji dependencies and scales with the window DPI.
        int size = Math.Max(16, (int)Math.Round(16f * DeviceDpi / 96f));
        var play = new Bitmap(size, size);
        play.SetResolution(DeviceDpi, DeviceDpi);
        using (var graphics = Graphics.FromImage(play))
        using (var brush = new SolidBrush(launch.Enabled || highContrast ? launch.ForeColor : palette.MutedText))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.FillPolygon(brush, [new PointF(size * 0.25f, size * 0.125f),
                new PointF(size * 0.875f, size * 0.5f), new PointF(size * 0.25f, size * 0.875f)]);
        }
        var previous = launch.Image;
        launch.Image = play;
        previous?.Dispose();
    }

    private async Task RefreshAsync()
    {
        SetBusy(true);
        try
        {
            var snapshot = await Task.Run(() =>
            {
                var info = LauncherInspection.Inspect(root);
                try { return (Overview: info, Mods: ModCatalog.Read(root), ModError: (string?)null); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
                { return (Overview: info, Mods: (IReadOnlyList<ModEntry>)Array.Empty<ModEntry>(), ModError: ex.Message); }
            });
            if (IsDisposed) return;
            overview = snapshot.Overview;
            gameVersion.Text = overview.GameVersion ?? "Detected during preparation";
            melonVersion.Text = overview.MelonLoaderVersion ?? "Not installed";
            patchStatus.Text = overview.ModPatchStatus;
            nextStatus.Text = overview.NextActionText;
            details.Text = string.Join(Environment.NewLine, overview.Details);
            if (snapshot.ModError is not null) details.Text += Environment.NewLine + "Mods unavailable: " + snapshot.ModError;
            modListAvailable = snapshot.ModError is null;
            updatingMods = true;
            mods.Items.Clear();
            foreach (var entry in snapshot.Mods) mods.Items.Add(new ModItem(entry), entry.Enabled);
            updatingMods = false;
            if (modListAvailable) UpdateModCount();
            else modCount.Text = "Mods unavailable";
        }
        catch (Exception ex)
        {
            if (IsDisposed) return;
            try
            {
                FileOps.WriteBytes(FileOps.SafePath(root, ".modlaunch/logs/gui-inspection-error.log"),
                    System.Text.Encoding.UTF8.GetBytes(ex.ToString()));
            }
            catch (Exception loggingError) when (loggingError is IOException or UnauthorizedAccessException or InvalidDataException) { }
            overview = new LauncherOverview(root, null, null, null, "Inspection unavailable", LauncherNextAction.Repair,
                File.Exists(Path.Combine(root, ".modlaunch", "session.json")), false, false, [ex.Message]);
            gameVersion.Text = "Unknown";
            melonVersion.Text = "Unknown";
            patchStatus.Text = overview.ModPatchStatus;
            details.Text = ex.Message;
            nextStatus.Text = "Repair";
            modCount.Text = "Mods unavailable";
        }
        finally { if (!IsDisposed) SetBusy(false); }
    }

    private async void ChangeMod(object? sender, ItemCheckEventArgs e)
    {
        if (updatingMods || busy || mods.Items[e.Index] is not ModItem item) return;
        bool enabled = e.NewValue == CheckState.Checked;
        e.NewValue = e.CurrentValue;
        int index = e.Index;
        changingMod = true;
        SetBusy(true);
        try
        {
            // Let CheckedListBox finish its current ItemCheck event before updating the row.
            await Task.Yield();
            var updated = await Task.Run(() => ModCatalog.SetEnabled(root, item.Entry, enabled));
            if (IsDisposed) return;
            updatingMods = true;
            mods.Items[index] = new ModItem(updated);
            mods.SetItemChecked(index, updated.Enabled);
            updatingMods = false;
            UpdateModCount();
        }
        catch (Exception ex)
        {
            if (!IsDisposed) MessageBox.Show(this, ex.Message, "Could not change mod", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { changingMod = false; if (!IsDisposed) SetBusy(false); }
    }

    private void SetBusy(bool value)
    {
        busy = value;
        UseWaitCursor = value;
        mods.Enabled = !value && modListAvailable;
        launch.Enabled = !value && overview is not null;
        prepare.Enabled = !value && overview is not null;
        repair.Enabled = !value && overview is not null;
        restore.Enabled = !value && overview?.HasSession == true;
        prepare.Visible = overview?.NextActionText != "Ready";
        if (overview?.NextActionText == "Repair") prepare.Visible = false;
    }

    private void UpdateModCount() => modCount.Text = $"Mods   {mods.Items.Cast<ModItem>().Count(item => item.Entry.Enabled)} / {mods.Items.Count} enabled";

    private void SelectAction(GuiLaunchAction action)
    {
        if (busy) return;
        try { GameProcessGuard.EnsureStopped(root); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Game is running", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        SelectedAction = action;
        Close();
    }

    private static Label ValueLabel() => new() { AutoSize = true, Text = "Checking…", Margin = new Padding(0, 3, 0, 8) };
    private static ThemeButton ActionButton(string text, string accessible) => new()
        {
            Text = text, AutoSize = true, MinimumSize = new Size(80, 36),
            Padding = new Padding(10, 0, 10, 0), Margin = new Padding(0, 0, 8, 0), AccessibleName = accessible
        };

    private class ThemeButton : Button
    {
        internal Color DisabledTextColor { get; set; }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Enabled || SystemInformation.HighContrast)
            {
                base.OnPaint(e);
                return;
            }

            // The flat button renderer ignores ForeColor when disabled. Keep its text readable
            // using the same managed WinForms drawing primitives, without native theme APIs.
            e.Graphics.Clear(BackColor);
            ControlPaint.DrawBorder(e.Graphics, ClientRectangle, FlatAppearance.BorderColor, ButtonBorderStyle.Solid);
            var textBounds = new Rectangle(Padding.Left, Padding.Top,
                Math.Max(0, ClientSize.Width - Padding.Horizontal), Math.Max(0, ClientSize.Height - Padding.Vertical));
            var alignment = TextFormatFlags.HorizontalCenter;
            if (Image is not null)
            {
                int gap = Math.Max(4, DeviceDpi / 24);
                int textWidth = TextRenderer.MeasureText(e.Graphics, Text, Font).Width;
                int left = textBounds.Left + Math.Max(0, (textBounds.Width - Image.Width - gap - textWidth) / 2);
                e.Graphics.DrawImageUnscaled(Image, left, textBounds.Top + (textBounds.Height - Image.Height) / 2);
                int textLeft = left + Image.Width + gap;
                textBounds = new Rectangle(textLeft, textBounds.Top, Math.Max(0, textBounds.Right - textLeft), textBounds.Height);
                alignment = TextFormatFlags.Left;
            }
            TextRenderer.DrawText(e.Graphics, Text, Font, textBounds, DisabledTextColor,
                alignment | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.HidePrefix);
        }
    }

    private sealed class ThemeToggleButton : ThemeButton
    {
        internal bool ShowSun { get; set; }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var state = e.Graphics.Save();
            try
            {
                // Draw both symbols ourselves so Wine does not need an emoji or symbol font.
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                float size = Math.Min(ClientSize.Width, ClientSize.Height) * 0.5f;
                float x = (ClientSize.Width - size) / 2f, y = (ClientSize.Height - size) / 2f;
                using var pen = new Pen(Enabled ? ForeColor : DisabledTextColor, Math.Max(1.5f, size / 12f));
                if (ShowSun)
                {
                    e.Graphics.DrawEllipse(pen, x + size / 3, y + size / 3, size / 3, size / 3);
                    for (int ray = 0; ray < 8; ray++)
                    {
                        double angle = ray * Math.PI / 4;
                        float dx = (float)Math.Cos(angle), dy = (float)Math.Sin(angle);
                        e.Graphics.DrawLine(pen,
                            x + size / 2 + dx * size * 0.32f, y + size / 2 + dy * size * 0.32f,
                            x + size / 2 + dx * size * 0.5f, y + size / 2 + dy * size * 0.5f);
                    }
                }
                else
                {
                    using var moon = new System.Drawing.Drawing2D.GraphicsPath();
                    moon.AddArc(x, y, size, size, 45, 270);
                    moon.AddBezier(x + size * 0.8536f, y + size * 0.1464f,
                        x + size * 0.3f, y + size * 0.15f, x + size * 0.3f, y + size * 0.85f,
                        x + size * 0.8536f, y + size * 0.8536f);
                    moon.CloseFigure();
                    e.Graphics.DrawPath(pen, moon);
                }
            }
            finally { e.Graphics.Restore(state); }
        }
    }

    private sealed record ThemePalette(Color Window, Color Control, Color Text, Color MutedText, Color Border, Color Hover, Color Pressed)
    {
        internal static ThemePalette For(LauncherTheme theme)
        {
            if (SystemInformation.HighContrast)
                return new(SystemColors.Window, SystemColors.Window, SystemColors.WindowText, SystemColors.WindowText,
                    SystemColors.WindowText, SystemColors.Highlight, SystemColors.Highlight);
            return theme == LauncherTheme.Dark
                ? new(Color.FromArgb(24, 26, 31), Color.FromArgb(34, 37, 44), Color.FromArgb(235, 237, 242),
                    Color.FromArgb(172, 180, 194), Color.FromArgb(87, 96, 112), Color.FromArgb(48, 54, 65), Color.FromArgb(61, 70, 85))
                : new(Color.FromArgb(248, 249, 252), Color.White, Color.FromArgb(30, 34, 42),
                    Color.FromArgb(88, 94, 104), Color.FromArgb(142, 150, 164), Color.FromArgb(231, 235, 242), Color.FromArgb(213, 220, 232));
        }
    }

    private void AddStatus(TableLayoutPanel panel, int row, string name, Label value)
    {
        var label = new Label { Text = name, AutoSize = true, Margin = new Padding(0, 3, 0, 8) };
        mutedLabels.Add(label);
        panel.Controls.Add(label, 0, row);
        panel.Controls.Add(value, 1, row);
    }

    private sealed record ModItem(ModEntry Entry)
    {
        public override string ToString() => Entry.RelativePath.EndsWith(".DISABLED", StringComparison.OrdinalIgnoreCase)
            ? Entry.RelativePath[..^9] : Entry.RelativePath;
    }
}
