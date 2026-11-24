using ZapretHub.Core;
using ZapretHub.Windows;

namespace ZapretHub.App;

internal sealed class MainWindow : Form
{
    private readonly Hub hub;
    private readonly Func<IReadOnlyList<string>> profiles;
    private readonly WindowsStartup startup;
    private readonly CancellationTokenSource lifetime = new();
    private readonly NotifyIcon tray;
    private readonly ContextMenuStrip trayMenu = new();
    private readonly Label status = new() { AutoSize = true, ForeColor = Color.FromArgb(25, 45, 57) };
    private readonly Label details = new() { AutoSize = true, ForeColor = Color.FromArgb(72, 82, 93) };
    private readonly Label vpnStatus = new() { AutoSize = true, ForeColor = Color.FromArgb(72, 82, 93) };
    private readonly TextBox log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private readonly Button check = new() { Text = "Проверить сейчас", AutoSize = true };
    private readonly Button findStable = new() { Text = "Найти стабильный", AutoSize = true, Visible = false };
    private readonly Button resume = new() { Text = "Вернуться к Zapret", AutoSize = true, Enabled = false };
    private readonly Button openVpn = new() { Text = "Открыть VPN", AutoSize = true, Enabled = false };
    private readonly Button retryVpn = new() { Text = "Подключить VPN", AutoSize = true, Visible = false };
    private readonly ToolStripMenuItem menuCheck = new("Проверить сейчас");
    private readonly ToolStripMenuItem menuFindStable = new("Найти стабильный") { Visible = false };
    private readonly ToolStripMenuItem menuResume = new("Вернуться к Zapret") { Enabled = false };
    private readonly ToolStripMenuItem menuVpn = new("Открыть VPN") { Enabled = false };
    private readonly ToolStripMenuItem menuRetry = new("Подключить VPN") { Visible = false };
    private HubSettings settings;
    private Task? run;
    private bool exiting;
    private bool readyToClose;
    private VpnState? previousVpnState;

    public MainWindow(Hub hub, HubSettings settings, Func<IReadOnlyList<string>> profiles, WindowsStartup startup)
    {
        this.hub = hub;
        this.settings = settings;
        this.profiles = profiles;
        this.startup = startup;
        Text = "ZapretHub";
        Icon = SystemIcons.Shield;
        Font = new Font("Segoe UI", 10);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(700, 455);
        MinimumSize = new Size(640, 360);

        var rows = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), RowCount = 7, ColumnCount = 1 };
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int index = 0; index < 6; index++)
            rows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rows.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        rows.Controls.Add(new Label { Text = "ZapretHub", AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
        status.Margin = new Padding(3, 16, 3, 4);
        details.Margin = new Padding(3, 3, 3, 4);
        vpnStatus.Margin = new Padding(3, 3, 3, 12);
        rows.Controls.Add(status);
        rows.Controls.Add(details);
        rows.Controls.Add(vpnStatus);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        var configure = new Button { Text = "Настройки", AutoSize = true };
        buttons.Controls.AddRange([check, findStable, resume, retryVpn, openVpn, configure]);
        rows.Controls.Add(buttons);
        rows.Controls.Add(new Label { Text = "Журнал", AutoSize = true, Margin = new Padding(3, 14, 3, 4) });
        rows.Controls.Add(log);
        Controls.Add(rows);

        trayMenu.Items.Add("Открыть ZapretHub", null, (_, _) => ShowDashboard());
        trayMenu.Items.AddRange([menuCheck, menuFindStable, menuResume, menuRetry, menuVpn, new ToolStripSeparator()]);
        trayMenu.Items.Add("Выход", null, async (_, _) => await ExitAsync());
        tray = new NotifyIcon { Icon = Icon, Text = "ZapretHub", ContextMenuStrip = trayMenu, Visible = true };
        tray.DoubleClick += (_, _) => ShowDashboard();
        tray.BalloonTipClicked += (_, _) => ShowDashboard();

        check.Click += async (_, _) => await PerformAsync(hub.CheckNowAsync);
        menuCheck.Click += async (_, _) => await PerformAsync(hub.CheckNowAsync);
        findStable.Click += async (_, _) => await FindStableAsync();
        menuFindStable.Click += async (_, _) => await FindStableAsync();
        resume.Click += async (_, _) => await ResumeAsync();
        menuResume.Click += async (_, _) => await ResumeAsync();
        openVpn.Click += async (_, _) => await PerformAsync(hub.OpenVpnManuallyAsync);
        menuVpn.Click += async (_, _) => await PerformAsync(hub.OpenVpnManuallyAsync);
        retryVpn.Click += async (_, _) => await PerformAsync(hub.RetryAutomaticVpnAsync);
        menuRetry.Click += async (_, _) => await PerformAsync(hub.RetryAutomaticVpnAsync);
        configure.Click += async (_, _) => await ConfigureAsync();
        hub.StatusChanged += UpdateStatus;
        hub.Logged += AppendLog;
        hub.RecoveryNotification += NotifyRecovery;
        Render(hub.Status);
        Shown += (_, _) => Start();
        FormClosing += OnClosing;
        SizeChanged += (_, _) => status.MaximumSize = new Size(Math.Max(1, ClientSize.Width - 52), 0);
        status.MaximumSize = new Size(ClientSize.Width - 52, 0);
    }

    public void StartHidden()
    {
        _ = Handle;
        BeginInvoke(() => Start());
    }

    private void Start() => run ??= Task.Run(() => hub.RunAsync(lifetime.Token));

    private void ShowDashboard()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private async Task ConfigureAsync()
    {
        bool? autoStart = null;
        try { autoStart = startup.IsEnabled(); }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException)
        {
            AppendLog($"Планировщик заданий: {error.Message}");
        }
        using var dialog = new SettingsWindow(settings, profiles(), autoStart);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Settings is not { } updated)
            return;
        await PerformAsync(async token =>
        {
            await hub.ApplySettingsAsync(updated, token);
            settings = updated;
            Render(hub.Status);
            if (autoStart is bool previous && dialog.AutoStart != previous)
                startup.SetEnabled(dialog.AutoStart);
        });
    }

    private async Task ResumeAsync()
    {
        if (hub.Status.Vpn?.State is VpnState.WaitingForUser or VpnState.Unknown && MessageBox.Show(this,
                "Отключите VPN в его клиенте перед возвратом к Zapret. VPN уже отключён?",
                "Возврат к Zapret", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        await PerformAsync(hub.ResumeZapretAsync);
    }

    private async Task PerformAsync(Func<CancellationToken, Task> action)
    {
        if (exiting)
            return;
        try { await action(lifetime.Token); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { MessageBox.Show(this, error.Message, "ZapretHub", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private Task FindStableAsync() => hub.Status.RecoveryRequestId is { } requestId
        ? PerformAsync(token => hub.FindStableAsync(requestId, token)) : Task.CompletedTask;

    private void NotifyRecovery(Guid requestId) => Dispatch(() =>
    {
        if (!settings.AutoRecover && hub.Status.RecoveryRequestId == requestId)
            tray.ShowBalloonTip(6000, "ZapretHub", "Обход нестабилен. Откройте ZapretHub и нажмите «Найти стабильный».",
                ToolTipIcon.Warning);
    });

    private void UpdateStatus(HubStatus value) => Dispatch(() => Render(value));

    private void Render(HubStatus value)
    {
        status.Text = value.Message;
        string active = value.Strategy is { } config ? $"{config.Version} · {config.Name}" : "—";
        string time = value.CheckedAt?.ToLocalTime().ToString("HH:mm:ss") ?? "—";
        details.Text = $"Zapret {active}    Последняя проверка {time}";
        string selected = settings.Vpn.Mode == VpnMode.Automatic
            ? $"WireGuard · {settings.Vpn.Profile}" : Path.GetFileNameWithoutExtension(settings.Vpn.Application) ?? "не выбран";
        vpnStatus.Text = $"Резервный VPN · {selected}";
        bool vpn = value.State == HubState.Vpn;
        check.Enabled = menuCheck.Enabled = !vpn && value.State is not (HubState.Starting or HubState.Recovering or HubState.Checking);
        findStable.Visible = menuFindStable.Visible = !settings.AutoRecover && value.State == HubState.AwaitingRecovery
            && value.RecoveryRequestId is not null;
        resume.Enabled = menuResume.Enabled = vpn && value.Vpn?.State != VpnState.Connecting;
        retryVpn.Visible = menuRetry.Visible = vpn && settings.Vpn.Mode == VpnMode.Automatic
            && value.Vpn?.State is VpnState.Failed or VpnState.NotConfigured or VpnState.Unknown;
        openVpn.Enabled = menuVpn.Enabled = vpn && !string.IsNullOrWhiteSpace(settings.Vpn.Application)
            && value.Vpn?.State is VpnState.Failed or VpnState.NotConfigured or VpnState.WaitingForUser or VpnState.Unknown;
        string tip = $"ZapretHub · {value.Strategy?.Name ?? (vpn ? "VPN" : "мониторинг")}";
        tray.Text = tip.Length > 63 ? tip[..63] : tip;
        VpnState? currentVpnState = value.Vpn?.State;
        if (!settings.AutoRecover && currentVpnState != previousVpnState && currentVpnState is VpnState.WaitingForUser or VpnState.Failed or VpnState.NotConfigured)
            tray.ShowBalloonTip(5000, "ZapretHub", value.Message, ToolTipIcon.Warning);
        previousVpnState = currentVpnState;
    }

    private void AppendLog(string message) => Dispatch(() =>
    {
        if (log.TextLength > 22000)
            log.Text = log.Text[^16000..];
        log.AppendText($"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}");
    });

    private void Dispatch(Action action)
    {
        if (!IsHandleCreated || IsDisposed)
            return;
        try { BeginInvoke(action); }
        catch (InvalidOperationException) when (IsDisposed || exiting) { }
    }

    private void OnClosing(object? sender, FormClosingEventArgs args)
    {
        if (!readyToClose && args.CloseReason == CloseReason.UserClosing)
        {
            args.Cancel = true;
            if (!exiting)
                Hide();
        }
        else if (!exiting)
        {
            lifetime.Cancel();
        }
    }

    private async Task ExitAsync()
    {
        if (exiting)
            return;
        exiting = true;
        lifetime.Cancel();
        try { if (run is not null) await run; }
        catch (Exception error) { MessageBox.Show(error.Message, "Ошибка остановки ZapretHub"); }
        finally
        {
            readyToClose = true;
            Close();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            hub.StatusChanged -= UpdateStatus;
            hub.Logged -= AppendLog;
            hub.RecoveryNotification -= NotifyRecovery;
            tray.Dispose();
            trayMenu.Dispose();
            lifetime.Dispose();
        }
        base.Dispose(disposing);
    }
}
