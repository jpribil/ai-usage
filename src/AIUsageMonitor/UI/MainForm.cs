using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using AIUsageMonitor.Core;
using AIUsageMonitor.Infrastructure;
using AIUsageMonitor.Services;

namespace AIUsageMonitor.UI;

internal sealed class MainForm : Form
{
    private const float UiScale = 1.2f;
    private readonly SettingsStore _settingsStore;
    private readonly DiagnosticLog _diagnosticLog;
    private readonly UsagePollingService _polling;
    private readonly NtfyNotifier _notifier;
    private readonly AutostartService _autostart;
    private readonly NotifyIcon _trayIcon;
    private readonly ContextMenuStrip _menu = new();
    private readonly Dictionary<UsageLimit, Rectangle> _checkboxBounds = [];
    private AppSettings _settings;
    private Point _dragOrigin;
    private Point _windowOrigin;
    private bool _dragging;
    private bool _exiting;
    private readonly System.Windows.Forms.Timer _pollTimer = new();
    private readonly System.Windows.Forms.Timer _countdownTimer = new();
    private readonly System.Windows.Forms.Timer _resetPollTimer = new() { Interval = 5_000 };
    private static readonly uint TaskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private CancellationTokenSource _pollCancellation = new();
    private UsageData? _claudeUsage;
    private UsageData? _codexUsage;
    private PollError? _claudeError;
    private PollError? _codexError;
    private int _consecutiveTransientFailures;

    internal MainForm(AppSettings settings, SettingsStore settingsStore, DiagnosticLog diagnosticLog, UsagePollingService polling, NtfyNotifier notifier, AutostartService autostart)
    {
        _settings = settings;
        _settingsStore = settingsStore;
        _diagnosticLog = diagnosticLog;
        _polling = polling;
        _notifier = notifier;
        _autostart = autostart;

        AutoScaleMode = AutoScaleMode.Dpi;
        DoubleBuffered = true;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = settings.AlwaysOnTop;
        Text = AppMetadata.Title;
        BackColor = Dark.WindowBackground;
        BuildMenu();

        _trayIcon = new NotifyIcon
        {
            Icon = AppIcon.Instance,
            Text = AppMetadata.ProductName,
            Visible = true,
            ContextMenuStrip = _menu
        };
        _trayIcon.MouseDoubleClick += (_, _) => ToggleWidgetVisibility();
        _pollTimer.Tick += async (_, _) => await PollAsync();
        _countdownTimer.Tick += (_, _) => OnCountdownTick();
        _resetPollTimer.Tick += async (_, _) => await PollAsync();
        Shown += async (_, _) => await PollAsync();

        ResizeToContent();
        PositionFromSettings();
        if (!settings.WidgetVisible)
        {
            BeginInvoke(Hide);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _trayIcon.Dispose();
            _menu.Dispose();
            _pollCancellation.Cancel();
            _pollCancellation.Dispose();
            _pollGate.Dispose();
            _pollTimer.Dispose();
            _countdownTimer.Dispose();
            _resetPollTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var palette = EffectivePalette;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(palette.WindowBackground);
        DrawTitleBar(e.Graphics, palette);

        var cards = ActiveServices();
        for (var index = 0; index < cards.Count; index++)
        {
            var cardRect = CardBounds(index, cards.Count);
            DrawCard(e.Graphics, cardRect, cards[index], palette);
        }
    }

    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if (message.Msg == TaskbarCreatedMessage)
        {
            _trayIcon.Visible = false;
            _trayIcon.Visible = true;
            _diagnosticLog.Write("TaskbarCreated received; tray icon restored.");
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Right)
        {
            _menu.Show(this, e.Location);
            return;
        }

        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        if (CloseButtonBounds.Contains(e.Location))
        {
            SetWidgetVisible(false);
            return;
        }

        foreach (var (limit, bounds) in _checkboxBounds)
        {
            if (bounds.Contains(e.Location))
            {
                ToggleNotification(limit);
                return;
            }
        }

        _dragging = true;
        _dragOrigin = e.Location;
        _windowOrigin = Location;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging)
        {
            return;
        }

        Location = ClampToVirtualScreen(new Point(
            _windowOrigin.X + e.X - _dragOrigin.X,
            _windowOrigin.Y + e.Y - _dragOrigin.Y));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragging)
        {
            _dragging = false;
            SaveSettings(_settings with { WindowX = Location.X, WindowY = Location.Y });
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            SetWidgetVisible(false);
            return;
        }

        base.OnFormClosing(e);
    }

    private void BuildMenu()
    {
        _menu.Opening += (_, _) => RebuildMenu();
        RebuildMenu();
    }

    private void RebuildMenu()
    {
        _menu.Items.Clear();
        _menu.Items.Add(CheckItem("Claude Code", _settings.ShowClaudeCode, ToggleClaude));
        _menu.Items.Add(CheckItem("ChatGPT", _settings.ShowCodex, ToggleCodex));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Item("Refresh", () => _ = PollAsync(force: true)));
        _menu.Items.Add(CreateFrequencyMenu());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(CreateAppearanceMenu());
        _menu.Items.Add(CreateLayoutMenu());
        _menu.Items.Add(Item("Language", null, enabled: false));
        _menu.Items.Add(CheckItem("Show Widget", _settings.WidgetVisible, ToggleWidgetVisibility));
        _menu.Items.Add(CheckItem("Always on Top", _settings.AlwaysOnTop, ToggleTopMost));
        _menu.Items.Add(Item("Reset Position", ResetPosition));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(CheckItem("Start with Windows", _autostart.IsEnabled, ToggleAutostart));
        _menu.Items.Add(Item("Notification channel…", ConfigureNotificationChannel));
        _menu.Items.Add(Item($"v{AppMetadata.DisplayVersion} - Check for Updates", () => _diagnosticLog.Write("Update check requested.")));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Item("Exit", ExitApplication));
    }

    private ToolStripMenuItem CreateFrequencyMenu()
    {
        var parent = new ToolStripMenuItem("Update Frequency");
        foreach (var (label, milliseconds) in new[]
                 {
                     ("1 Minute", 60_000), ("5 Minutes", 300_000),
                     ("15 Minutes", 900_000), ("1 Hour", 3_600_000)
                 })
        {
            parent.DropDownItems.Add(CheckItem(label, _settings.PollIntervalMilliseconds == milliseconds,
                () => SaveSettings(_settings with { PollIntervalMilliseconds = milliseconds })));
        }

        return parent;
    }

    private ToolStripMenuItem CreateAppearanceMenu()
    {
        var parent = new ToolStripMenuItem("Appearance");
        parent.DropDownItems.Add(CheckItem("System Default", _settings.Theme is null,
            () => SaveSettings(_settings with { Theme = null })));
        parent.DropDownItems.Add(CheckItem("Light", _settings.Theme == "light",
            () => SaveSettings(_settings with { Theme = "light" })));
        parent.DropDownItems.Add(CheckItem("Dark", _settings.Theme == "dark",
            () => SaveSettings(_settings with { Theme = "dark" })));
        return parent;
    }

    private ToolStripMenuItem CreateLayoutMenu()
    {
        var parent = new ToolStripMenuItem("Layout");
        parent.DropDownItems.Add(CheckItem("Side by Side", _settings.LayoutHorizontal,
            () => ChangeLayout(true)));
        parent.DropDownItems.Add(CheckItem("Stacked", !_settings.LayoutHorizontal,
            () => ChangeLayout(false)));
        return parent;
    }

    private static ToolStripMenuItem Item(string text, Action? action, bool enabled = true)
    {
        var item = new ToolStripMenuItem(text) { Enabled = enabled };
        if (action is not null)
        {
            item.Click += (_, _) => action();
        }

        return item;
    }

    private static ToolStripMenuItem CheckItem(string text, bool isChecked, Action action)
    {
        var item = new ToolStripMenuItem(text)
        {
            Checked = isChecked,
            CheckOnClick = false
        };
        return item.WithClick(action);
    }

    private void ToggleClaude()
    {
        if (!_settings.ShowCodex && _settings.ShowClaudeCode)
        {
            return;
        }

        SaveSettings(_settings with { ShowClaudeCode = !_settings.ShowClaudeCode });
        ResizeToContent();
        _ = PollAsync(force: true);
    }

    private void ToggleCodex()
    {
        if (!_settings.ShowClaudeCode && _settings.ShowCodex)
        {
            return;
        }

        SaveSettings(_settings with { ShowCodex = !_settings.ShowCodex });
        ResizeToContent();
        _ = PollAsync(force: true);
    }

    private void ChangeLayout(bool horizontal)
    {
        SaveSettings(_settings with { LayoutHorizontal = horizontal });
        ResizeToContent();
    }

    private void ToggleTopMost()
    {
        TopMost = !_settings.AlwaysOnTop;
        SaveSettings(_settings with { AlwaysOnTop = TopMost });
    }

    private void ToggleAutostart() => _autostart.SetEnabled(!_autostart.IsEnabled);

    private void ToggleWidgetVisibility() => SetWidgetVisible(!_settings.WidgetVisible);

    private void SetWidgetVisible(bool visible)
    {
        SaveSettings(_settings with { WidgetVisible = visible });
        if (visible)
        {
            Show();
            Activate();
        }
        else
        {
            Hide();
        }
    }

    private void ResetPosition()
    {
        Location = DefaultLocation();
        SaveSettings(_settings with { WindowX = Location.X, WindowY = Location.Y });
    }

    private void ConfigureNotificationChannel()
    {
        var result = TopicDialog.Prompt(this, _settings.NtfyTopic);
        if (result is not null)
        {
            SaveSettings(_settings with { NtfyTopic = string.IsNullOrEmpty(result) ? null : result });
        }
    }

    private void ToggleNotification(UsageLimit limit)
    {
        if (string.IsNullOrWhiteSpace(_settings.NtfyTopic))
        {
            var topic = TopicDialog.Prompt(this, null);
            if (string.IsNullOrWhiteSpace(topic))
            {
                return;
            }

            SaveSettings(_settings.WithArmedLimit(limit, true) with { NtfyTopic = topic });
            return;
        }

        SaveSettings(_settings.WithArmedLimit(limit, !_settings.ArmedLimits[(int)limit]));
    }

    private void ExitApplication()
    {
        _exiting = true;
        _trayIcon.Visible = false;
        Close();
    }

    private void SaveSettings(AppSettings settings)
    {
        _settings = settings.Normalize();
        _settingsStore.Save(_settings);
        Invalidate();
    }

    private async Task PollAsync(bool force = false)
    {
        if (_exiting || (!force && _pollGate.CurrentCount == 0))
        {
            return;
        }

        await _pollGate.WaitAsync();
        try
        {
            _diagnosticLog.Write("Starting usage poll.");
            var result = await _polling.PollAsync(_settings.ShowClaudeCode, _settings.ShowCodex, _pollCancellation.Token);
            if (result.Data.ClaudeCode is not null)
            {
                DetectResets(_claudeUsage, result.Data.ClaudeCode, UsageLimit.ClaudeSession);
                _claudeUsage = result.Data.ClaudeCode;
                _claudeError = null;
            }
            else if (_settings.ShowClaudeCode)
            {
                _claudeError = result.ClaudeError;
            }
            if (result.Data.Codex is not null)
            {
                DetectResets(_codexUsage, result.Data.Codex, UsageLimit.CodexSession);
                _codexUsage = result.Data.Codex;
                _codexError = null;
            }
            else if (_settings.ShowCodex)
            {
                _codexError = result.CodexError;
            }

            _consecutiveTransientFailures = result.HasSuccess ? 0 : _consecutiveTransientFailures + 1;
            ScheduleNextPoll(result.HasSuccess);
            ConfigureCountdownTimer();
            _resetPollTimer.Enabled = AnyKnownResetIsDue();
            Invalidate();
            _diagnosticLog.Write(result.HasSuccess ? "Usage poll succeeded." : "Usage poll did not return any service data.");
        }
        catch (OperationCanceledException) when (_exiting)
        {
            // Closing the application cancels in-flight work deliberately.
        }
        catch (Exception exception)
        {
            _consecutiveTransientFailures++;
            ScheduleNextPoll(success: false);
            _diagnosticLog.Write($"Unhandled usage poll error: {exception.Message}");
        }
        finally
        {
            _pollGate.Release();
        }
    }

    private void ScheduleNextPoll(bool success)
    {
        var interval = success
            ? _settings.PollIntervalMilliseconds
            : Math.Min(_settings.PollIntervalMilliseconds, 30_000 * (1 << Math.Min(_consecutiveTransientFailures - 1, 5)));
        _pollTimer.Interval = Math.Max(1_000, interval);
        _pollTimer.Start();
    }

    private void ConfigureCountdownTimer()
    {
        var resets = new[]
        {
            _claudeUsage?.Session.ResetsAt, _claudeUsage?.Weekly.ResetsAt,
            _codexUsage?.Session.ResetsAt, _codexUsage?.Weekly.ResetsAt
        }.Where(time => time is not null).Select(time => time!.Value).ToList();
        var seconds = resets.Count == 0
            ? 60
            : resets.Select(reset =>
            {
                var remaining = (long)Math.Floor((reset - DateTimeOffset.UtcNow).TotalSeconds);
                return remaining <= 0 ? 1 : (int)((remaining - 1) % 60 + 1);
            }).Min();
        _countdownTimer.Interval = Math.Clamp(seconds * 1_000, 1_000, 60_000);
        _countdownTimer.Start();
    }

    private void OnCountdownTick()
    {
        Invalidate();
        ConfigureCountdownTimer();
        _resetPollTimer.Enabled = AnyKnownResetIsDue();
    }

    private bool AnyKnownResetIsDue() => new[]
    {
        _claudeUsage?.Session.ResetsAt, _claudeUsage?.Weekly.ResetsAt,
        _codexUsage?.Session.ResetsAt, _codexUsage?.Weekly.ResetsAt
    }.Any(time => time is not null && time <= DateTimeOffset.UtcNow);

    private void DetectResets(UsageData? previous, UsageData current, UsageLimit sessionLimit)
    {
        if (previous is null || string.IsNullOrWhiteSpace(_settings.NtfyTopic))
        {
            return;
        }

        DetectReset(previous.Session, current.Session, sessionLimit);
        DetectReset(previous.Weekly, current.Weekly, sessionLimit + 1);
    }

    private void DetectReset(UsageSection previous, UsageSection current, UsageLimit limit)
    {
        if (!_settings.ArmedLimits[(int)limit] || previous.ResetsAt is not { } oldReset || current.ResetsAt is not { } newReset ||
            newReset < oldReset.AddSeconds(60))
        {
            return;
        }

        // Disarm before sending: delivery is explicitly best-effort and may never block repeat detection.
        var topic = _settings.NtfyTopic;
        SaveSettings(_settings.WithArmedLimit(limit, false));
        _ = _notifier.SendResetNotificationAsync(topic, limit, _pollCancellation.Token);
    }

    private void ResizeToContent()
    {
        var count = ActiveServices().Count;
        var scale = DeviceDpi / 96f * UiScale;
        var cardWidth = Scale(count == 1 ? 274 : 214, scale);
        var cardHeight = Scale(126, scale);
        var padding = Scale(14, scale);
        var titleHeight = Scale(34, scale);
        var gap = Scale(12, scale);
        var horizontal = count > 1 && _settings.LayoutHorizontal;
        ClientSize = horizontal
            ? new Size(2 * padding + count * cardWidth + (count - 1) * gap, titleHeight + 2 * padding + cardHeight)
            : new Size(2 * padding + cardWidth, titleHeight + 2 * padding + count * cardHeight + (count - 1) * gap);
        Location = ClampToVirtualScreen(Location);
    }

    private List<ServiceKind> ActiveServices()
    {
        var services = new List<ServiceKind>(2);
        if (_settings.ShowClaudeCode)
        {
            services.Add(ServiceKind.Claude);
        }
        if (_settings.ShowCodex)
        {
            services.Add(ServiceKind.Codex);
        }
        return services;
    }

    private Rectangle CardBounds(int index, int count)
    {
        var scale = DeviceDpi / 96f * UiScale;
        var cardWidth = Scale(count == 1 ? 274 : 214, scale);
        var cardHeight = Scale(126, scale);
        var padding = Scale(14, scale);
        var titleHeight = Scale(34, scale);
        var gap = Scale(12, scale);
        var horizontal = count > 1 && _settings.LayoutHorizontal;
        return horizontal
            ? new Rectangle(padding + index * (cardWidth + gap), titleHeight + padding, cardWidth, cardHeight)
            : new Rectangle(padding, titleHeight + padding + index * (cardHeight + gap), cardWidth, cardHeight);
    }

    private void DrawTitleBar(Graphics graphics, Palette palette)
    {
        using var background = new SolidBrush(palette.TitleBackground);
        graphics.FillRectangle(background, new Rectangle(Point.Empty, new Size(ClientSize.Width, Scale(34))));
        using var border = new Pen(palette.Border);
        graphics.DrawLine(border, 0, Scale(34) - 1, ClientSize.Width, Scale(34) - 1);
        DrawGauge(graphics, new Rectangle(Scale(12), Scale(7), Scale(20), Scale(20)));
        using var font = CreateFont(14, FontStyle.Bold);
        using var text = new SolidBrush(palette.PrimaryText);
        graphics.DrawString(AppMetadata.Title, font, text, Scale(40), Scale(8));

        using var closeBackground = new SolidBrush(palette.CloseBackground);
        graphics.FillPath(closeBackground, RoundedRectangle(CloseButtonBounds, Scale(5)));
        using var closeFont = CreateFont(15, FontStyle.Regular);
        using var closeText = new SolidBrush(palette.CloseGlyph);
        graphics.DrawString("×", closeFont, closeText, CloseButtonBounds, CenteredFormat);
    }

    private void DrawGauge(Graphics graphics, Rectangle bounds)
    {
        var center = new PointF(bounds.Left + bounds.Width / 2f, bounds.Top + bounds.Height * .60f);
        var radius = bounds.Width * .34f;
        using var green = new Pen(Color.FromArgb(0x22, 0xC5, 0x5E), Math.Max(2, bounds.Width * .135f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var amber = new Pen(Color.FromArgb(0xF5, 0x9E, 0x0B), green.Width) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var red = new Pen(Color.FromArgb(0xEF, 0x44, 0x44), green.Width) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        var arc = RectangleF.FromLTRB(center.X - radius, center.Y - radius, center.X + radius, center.Y + radius);
        graphics.DrawArc(green, arc, 135, 90);
        graphics.DrawArc(amber, arc, 225, 90);
        graphics.DrawArc(red, arc, 315, 90);
        var needleAngle = 252 * Math.PI / 180;
        var needle = new PointF(center.X + (float)Math.Cos(needleAngle) * radius * .82f, center.Y + (float)Math.Sin(needleAngle) * radius * .82f);
        graphics.DrawLine(red, center, needle);
        using var pivot = new SolidBrush(red.Color);
        var pivotRadius = green.Width * .62f;
        graphics.FillEllipse(pivot, center.X - pivotRadius, center.Y - pivotRadius, pivotRadius * 2, pivotRadius * 2);
    }

    private void DrawCard(Graphics graphics, Rectangle bounds, ServiceKind service, Palette palette)
    {
        using var card = new SolidBrush(palette.CardBackground);
        graphics.FillPath(card, RoundedRectangle(bounds, Scale(8)));
        using var border = new Pen(palette.CardBorder);
        graphics.DrawPath(border, RoundedRectangle(bounds, Scale(8)));
        using var titleFont = CreateFont(15, FontStyle.Bold);
        using var titleBrush = new SolidBrush(palette.PrimaryText);
        var serviceName = service == ServiceKind.Claude ? "Claude Code" : "ChatGPT";
        var credits = service == ServiceKind.Codex ? _codexUsage?.ResetCreditsAvailable : null;
        var titleBounds = new Rectangle(bounds.Left + Scale(12), bounds.Top + Scale(8), bounds.Width - Scale(24), Scale(22));
        if (credits is > 0)
        {
            using var badgeFont = CreateFont(10, FontStyle.Regular);
            var badgeText = $"Resets: {credits}";
            var badgeWidth = (int)Math.Ceiling(graphics.MeasureString(badgeText, badgeFont).Width) + Scale(12);
            var badge = new Rectangle(bounds.Right - Scale(12) - badgeWidth, bounds.Top + Scale(9), badgeWidth, Scale(16));
            using var badgeBrush = new SolidBrush(palette.Track);
            graphics.FillPath(badgeBrush, RoundedRectangle(badge, Scale(8)));
            using var muted = new SolidBrush(palette.MutedText);
            graphics.DrawString(badgeText, badgeFont, muted, badge, CenteredFormat);
            titleBounds.Width -= badgeWidth + Scale(6);
        }
        graphics.DrawString(serviceName, titleFont, titleBrush, titleBounds, EllipsisFormat);
        var baseLimit = service == ServiceKind.Claude ? UsageLimit.ClaudeSession : UsageLimit.CodexSession;
        var usage = service == ServiceKind.Claude ? _claudeUsage : _codexUsage;
        var error = service == ServiceKind.Claude ? _claudeError : _codexError;
        DrawUsageRow(graphics, bounds, Scale(43), "5h", baseLimit, usage?.Session, error, palette);
        DrawUsageRow(graphics, bounds, Scale(82), "7d", baseLimit + 1, usage?.Weekly, error, palette);
    }

    private void DrawUsageRow(Graphics graphics, Rectangle card, int offset, string label, UsageLimit limit, UsageSection? usage, PollError? error, Palette palette)
    {
        var row = new Rectangle(card.Left + Scale(12), card.Top + offset, card.Width - Scale(24), Scale(31));
        using var labelFont = CreateFont(12, FontStyle.Regular);
        using var muted = new SolidBrush(palette.MutedText);
        using var primary = new SolidBrush(palette.PrimaryText);
        graphics.DrawString(label, labelFont, muted, new Rectangle(row.Left, row.Top, Scale(28), Scale(18)), StringFormat.GenericDefault);
        graphics.DrawString(DisplayText(usage, error), labelFont, primary, new Rectangle(row.Left + Scale(28), row.Top, row.Width - Scale(28), Scale(18)), RightFormat);
        var check = new Rectangle(row.Right - Scale(14), row.Top + Scale(21) - Scale(2), Scale(14), Scale(14));
        _checkboxBounds[limit] = check;
        var track = new Rectangle(row.Left, row.Top + Scale(21), row.Width - Scale(22), Scale(10));
        using var trackBrush = new SolidBrush(palette.Track);
        graphics.FillPath(trackBrush, RoundedRectangle(track, Scale(5)));
        if (usage is { Available: true })
        {
            var fillWidth = (int)Math.Round(track.Width * Math.Clamp(usage.Percentage, 0, 100) / 100d);
            if (fillWidth > 0)
            {
                using var fill = new SolidBrush(UsageColor(usage.Percentage));
                graphics.FillPath(fill, RoundedRectangle(new Rectangle(track.Left, track.Top, fillWidth, track.Height), Scale(5)));
            }
        }
        DrawCheckbox(graphics, check, _settings.ArmedLimits[(int)limit], palette);
    }

    private void DrawCheckbox(Graphics graphics, Rectangle bounds, bool armed, Palette palette)
    {
        if (armed)
        {
            using var fill = new SolidBrush(Color.FromArgb(0x22, 0xC5, 0x5E));
            graphics.FillPath(fill, RoundedRectangle(bounds, Scale(3)));
            using var pen = new Pen(Color.White, Math.Max(1, Scale(2))) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            graphics.DrawLines(pen,
            [
                new PointF(bounds.Left + bounds.Width * .26f, bounds.Top + bounds.Height * .52f),
                new PointF(bounds.Left + bounds.Width * .44f, bounds.Top + bounds.Height * .70f),
                new PointF(bounds.Left + bounds.Width * .74f, bounds.Top + bounds.Height * .30f)
            ]);
            return;
        }

        using var outer = new SolidBrush(palette.MutedText);
        graphics.FillPath(outer, RoundedRectangle(bounds, Scale(3)));
        var inset = Math.Max(1, bounds.Width / 7);
        using var inner = new SolidBrush(palette.CardBackground);
        graphics.FillPath(inner, RoundedRectangle(Rectangle.Inflate(bounds, -inset, -inset), Scale(2)));
    }

    private static string DisplayText(UsageSection? usage, PollError? error)
    {
        if (usage is null)
        {
            return error is PollError.AuthRequired or PollError.TokenExpired or PollError.NoCredentials ? "!" : "...";
        }
        if (!usage.Available)
        {
            return "–";
        }

        var percentage = $"{Math.Round(usage.Percentage):0}%";
        if (usage.ResetsAt is not { } reset)
        {
            return percentage;
        }
        var remaining = reset - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return $"{percentage} · now";
        }
        return remaining.TotalDays >= 1
            ? $"{percentage} · {(int)remaining.TotalDays}:{remaining.Hours:00}:{remaining.Minutes:00}"
            : $"{percentage} · {(int)remaining.TotalHours}:{remaining.Minutes:00}";
    }

    private static Color UsageColor(double percentage)
    {
        var green = Color.FromArgb(0x22, 0xC5, 0x5E);
        var amber = Color.FromArgb(0xF5, 0x9E, 0x0B);
        var red = Color.FromArgb(0xEF, 0x44, 0x44);
        var value = Math.Clamp(percentage, 0, 100);
        return value <= 50 ? Lerp(green, amber, value / 50) : Lerp(amber, red, (value - 50) / 50);
    }

    private static Color Lerp(Color start, Color end, double amount) => Color.FromArgb(
        (int)Math.Round(start.R + (end.R - start.R) * amount),
        (int)Math.Round(start.G + (end.G - start.G) * amount),
        (int)Math.Round(start.B + (end.B - start.B) * amount));

    private Rectangle CloseButtonBounds => new(ClientSize.Width - Scale(12) - Scale(22), Scale(6), Scale(22), Scale(22));

    private void PositionFromSettings()
    {
        Location = _settings.WindowX is int x && _settings.WindowY is int y
            ? ClampToVirtualScreen(new Point(x, y))
            : DefaultLocation();
    }

    private Point DefaultLocation()
    {
        var screen = SystemInformation.VirtualScreen;
        return ClampToVirtualScreen(new Point(
            screen.Right - Width - Scale(16),
            screen.Bottom - Height - Scale(64)));
    }

    private Point ClampToVirtualScreen(Point location)
    {
        var screen = SystemInformation.VirtualScreen;
        return new Point(
            Math.Clamp(location.X, screen.Left, Math.Max(screen.Left, screen.Right - Width)),
            Math.Clamp(location.Y, screen.Top, Math.Max(screen.Top, screen.Bottom - Height)));
    }

    private Palette EffectivePalette => IsLightTheme ? Light : Dark;
    private bool IsLightTheme
    {
        get
        {
            if (_settings.Theme == "light") return true;
            if (_settings.Theme == "dark") return false;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize", writable: false);
                return key?.GetValue("SystemUsesLightTheme") is int value && value == 1;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
    private int Scale(int value) => Scale(value, DeviceDpi / 96f * UiScale);
    private static int Scale(int value, float scale) => (int)Math.Round(value * scale);
    private Font CreateFont(float size, FontStyle style) => new("Bahnschrift SemiCondensed", Scale((int)size), style, GraphicsUnit.Pixel);
    private static GraphicsPath RoundedRectangle(Rectangle rectangle, int radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Max(1, radius * 2);
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static readonly StringFormat CenteredFormat = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
    private static readonly StringFormat RightFormat = new() { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Near, Trimming = StringTrimming.EllipsisCharacter };
    private static readonly StringFormat EllipsisFormat = new() { LineAlignment = StringAlignment.Near, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };

    private enum ServiceKind { Claude, Codex }

    private sealed record Palette(Color WindowBackground, Color TitleBackground, Color Border, Color CardBackground,
        Color CardBorder, Color PrimaryText, Color MutedText, Color Track, Color CloseBackground, Color CloseGlyph);

    private static readonly Palette Dark = new(
        Color.FromArgb(0x0B, 0x0D, 0x10), Color.FromArgb(0x13, 0x16, 0x1B), Color.FromArgb(0x23, 0x27, 0x2F),
        Color.FromArgb(0x15, 0x18, 0x1E), Color.FromArgb(0x26, 0x2B, 0x34), Color.FromArgb(0xF4, 0xF6, 0xFA),
        Color.FromArgb(0x8B, 0x94, 0xA1), Color.FromArgb(0x24, 0x2A, 0x33), Color.FromArgb(0x1E, 0x23, 0x2B), Color.FromArgb(0xC8, 0xCE, 0xD7));
    private static readonly Palette Light = new(
        Color.FromArgb(0xF2, 0xF4, 0xF8), Color.White, Color.FromArgb(0xE1, 0xE5, 0xEC), Color.White,
        Color.FromArgb(0xE4, 0xE8, 0xEF), Color.FromArgb(0x11, 0x15, 0x1B), Color.FromArgb(0x5B, 0x65, 0x73),
        Color.FromArgb(0xE7, 0xEB, 0xF1), Color.FromArgb(0xEB, 0xEE, 0xF3), Color.FromArgb(0x45, 0x4D, 0x58));

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);
}

internal static class ToolStripMenuItemExtensions
{
    internal static ToolStripMenuItem WithClick(this ToolStripMenuItem item, Action action)
    {
        item.Click += (_, _) => action();
        return item;
    }
}
