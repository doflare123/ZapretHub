namespace ZapretHub.Core;

public enum HubState { Starting, Checking, Recovering, Zapret, NetworkUnavailable, Vpn, Error }

public sealed record HubStatus(HubState State, string Message, StrategyId? Strategy = null,
    DateTimeOffset? CheckedAt = null, VpnResult? Vpn = null);

public sealed record HubTiming
{
    public TimeSpan Retry { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan UpdateTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan RecoveryTimeout { get; init; } = TimeSpan.FromMinutes(3);
    public TimeSpan UpdateInterval { get; init; } = TimeSpan.FromDays(1);
    public TimeSpan MaintenanceInterval { get; init; } = TimeSpan.FromMinutes(1);
}

public sealed class Hub
{
    private readonly IReleaseStore releases;
    private readonly IStrategyRunner runner;
    private readonly IConnectivity connectivity;
    private readonly VpnFallback vpn;
    private readonly LocalState storage;
    private readonly Func<string> currentNetwork;
    private readonly TimeProvider clock;
    private readonly HubTiming timing;
    private readonly SemaphoreSlim operation = new(1);
    private readonly StrategyHistory history;
    private HubSettings settings;
    private StrategyId? active;
    private bool pausedForVpn;
    private VpnSettings? fallbackSettings;
    private DateTimeOffset nextUpdate = DateTimeOffset.MinValue;
    private DateTimeOffset nextMonitor = DateTimeOffset.MinValue;
    private DateTimeOffset? checkedAt;

    public Hub(HubSettings settings, IReleaseStore releases, IStrategyRunner runner, IConnectivity connectivity,
        VpnFallback vpn, LocalState storage, Func<string> currentNetwork, TimeProvider clock, HubTiming timing)
    {
        this.settings = settings;
        this.releases = releases;
        this.runner = runner;
        this.connectivity = connectivity;
        this.vpn = vpn;
        this.storage = storage;
        this.currentNetwork = currentNetwork;
        this.clock = clock;
        this.timing = timing;
        history = storage.LoadHistory();
        fallbackSettings = storage.LoadFallback();
        pausedForVpn = fallbackSettings is not null;
    }

    public event Action<HubStatus>? StatusChanged;
    public event Action<string>? Logged;

    public HubStatus Status { get; private set; } = new(HubState.Starting, "Запуск ZapretHub…");

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(timing.MaintenanceInterval, clock);
        try
        {
            await ExecuteAsync(InitializeAsync, cancellationToken, wait: true).ConfigureAwait(false);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                await ExecuteAsync(MaintainAsync, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            await operation.WaitAsync().ConfigureAwait(false);
            try { await StopStrategyAsync().ConfigureAwait(false); }
            finally { operation.Release(); }
        }
    }

    public Task CheckNowAsync(CancellationToken cancellationToken) => ExecuteAsync(MonitorAsync, cancellationToken);

    public async Task ApplySettingsAsync(HubSettings newSettings, CancellationToken cancellationToken)
    {
        await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            storage.SaveSettings(newSettings);
            settings = newSettings;
            nextMonitor = clock.GetUtcNow().AddMinutes(settings.MonitorMinutes);
            Prune();
        }
        finally { operation.Release(); }
    }

    public Task OpenVpnManuallyAsync(CancellationToken cancellationToken) => ExecuteAsync(async token =>
    {
        if (!pausedForVpn || Status.Vpn?.State == VpnState.Connected)
            return;
        if (fallbackSettings is { Mode: VpnMode.Automatic } previous
            && !await TryDisconnectAsync(previous, token).ConfigureAwait(false))
            return;
        fallbackSettings = settings.Vpn with { Mode = VpnMode.Manual };
        RememberFallback(fallbackSettings);
        SetVpn(await vpn.OpenManuallyAsync(fallbackSettings, token).ConfigureAwait(false));
    }, cancellationToken);

    public Task RetryAutomaticVpnAsync(CancellationToken cancellationToken) => ExecuteAsync(async token =>
    {
        if (!pausedForVpn || settings.Vpn.Mode != VpnMode.Automatic || Status.Vpn?.State == VpnState.Connected)
            return;
        if (fallbackSettings is { Mode: VpnMode.Automatic } previous && previous.Profile != settings.Vpn.Profile
            && !await TryDisconnectAsync(previous, token).ConfigureAwait(false))
            return;
        await ConnectFallbackAsync(token).ConfigureAwait(false);
    }, cancellationToken);

    public Task ResumeZapretAsync(CancellationToken cancellationToken) => ExecuteAsync(async token =>
    {
        if (!pausedForVpn)
            return;
        if (fallbackSettings is { } fallback && !await TryDisconnectAsync(fallback, token).ConfigureAwait(false))
            return;
        try { storage.ClearFallback(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            SetVpn(new(VpnState.Failed, $"Не удалось сохранить выход из режима VPN. {error.Message}"));
            return;
        }
        fallbackSettings = null;
        pausedForVpn = false;
        await MonitorAsync(token).ConfigureAwait(false);
    }, cancellationToken);

    private async Task InitializeAsync(CancellationToken token)
    {
        Task<string?> refresh = RefreshAsync(token);
        try
        {
            if (pausedForVpn)
                SetVpn(new(VpnState.Unknown, "Резервный режим сохранён. Состояние туннеля неизвестно."));
            else
                await RecoverAsync(refresh, checkNetwork: false, failed: null, token).ConfigureAwait(false);
        }
        finally
        {
            await refresh.ConfigureAwait(false);
            Prune();
        }
    }

    private async Task MaintainAsync(CancellationToken token)
    {
        DateTimeOffset now = clock.GetUtcNow();
        if (!pausedForVpn && now >= nextMonitor)
            await MonitorAsync(token).ConfigureAwait(false);
        if (now >= nextUpdate)
        {
            await RefreshAsync(token).ConfigureAwait(false);
            Prune();
        }
    }

    private async Task MonitorAsync(CancellationToken token)
    {
        if (pausedForVpn)
            return;
        nextMonitor = clock.GetUtcNow().AddMinutes(settings.MonitorMinutes);
        if (active is null || !runner.IsRunning)
        {
            await RecoverAsync(null, checkNetwork: true, failed: null, token).ConfigureAwait(false);
            return;
        }

        Set(HubState.Checking, "Проверка активной конфигурации…");
        Health health = await connectivity.CheckAsync(settings, token).ConfigureAwait(false);
        if (health != Health.Available)
        {
            await Task.Delay(timing.Retry, clock, token).ConfigureAwait(false);
            health = await connectivity.CheckAsync(settings, token).ConfigureAwait(false);
        }
        checkedAt = clock.GetUtcNow();
        switch (health)
        {
            case Health.Available when runner.IsRunning:
                Set(HubState.Zapret, "Zapret работает");
                break;
            case Health.NetworkUnavailable:
                WaitForNetwork();
                break;
            default:
                await RecoverAsync(null, checkNetwork: false, failed: active, token).ConfigureAwait(false);
                break;
        }
    }

    private async Task RecoverAsync(Task<string?>? startupRefresh, bool checkNetwork, StrategyId? failed, CancellationToken token)
    {
        nextMonitor = clock.GetUtcNow().AddMinutes(settings.MonitorMinutes);
        using var deadline = new CancellationTokenSource(timing.RecoveryTimeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        CancellationToken recovery = linked.Token;
        try
        {
            if (checkNetwork && !await connectivity.IsControlReachableAsync(settings, recovery).ConfigureAwait(false))
            {
                WaitForNetwork();
                return;
            }

            string network = currentNetwork();
            var attempted = new HashSet<StrategyId>();
            if (failed is not null)
                attempted.Add(failed);
            switch (await TryLocalAsync(network, attempted, recovery).ConfigureAwait(false))
            {
                case StrategySearch.Working or StrategySearch.NetworkUnavailable:
                    return;
            }
            if (!await connectivity.IsControlReachableAsync(settings, recovery).ConfigureAwait(false))
            {
                await StopStrategyAsync().ConfigureAwait(false);
                WaitForNetwork();
                return;
            }

            if (startupRefresh is null)
                await RefreshAsync(recovery).ConfigureAwait(false);
            else
                await startupRefresh.WaitAsync(recovery).ConfigureAwait(false);
            switch (await TryLocalAsync(network, attempted, recovery).ConfigureAwait(false))
            {
                case StrategySearch.Working or StrategySearch.NetworkUnavailable:
                    return;
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            Logged?.Invoke("Истекло время подбора конфигурации.");
        }

        await StopStrategyAsync().ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        Prune();
        pausedForVpn = true;
        await ConnectFallbackAsync(token).ConfigureAwait(false);
    }

    private async Task ConnectFallbackAsync(CancellationToken token)
    {
        fallbackSettings = settings.Vpn;
        RememberFallback(fallbackSettings);
        var result = await vpn.ConnectAsync(fallbackSettings,
            () => SetVpn(new(VpnState.Connecting, "Подключение VPN…")), token).ConfigureAwait(false);
        SetVpn(result);
    }

    private void RememberFallback(VpnSettings fallback)
    {
        try { storage.SaveFallback(fallback); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Logged?.Invoke($"Не удалось сохранить режим VPN. {error.Message}");
        }
    }

    private async Task<bool> TryDisconnectAsync(VpnSettings fallback, CancellationToken token)
    {
        try { await vpn.DisconnectAsync(fallback, token).ConfigureAwait(false); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            SetVpn(new(VpnState.Failed, $"Не удалось отключить VPN. {error.Message}"));
            return false;
        }
        return true;
    }

    private enum StrategySearch { Exhausted, Working, NetworkUnavailable }

    private async Task<StrategySearch> TryLocalAsync(string network, HashSet<StrategyId> attempted, CancellationToken token)
    {
        var available = releases.ListStrategies();
        var byId = available.ToDictionary(strategy => strategy.Id);
        var preferred = history.Preferred(network).Where(byId.ContainsKey).Select(id => byId[id]);
        foreach (Strategy strategy in preferred.Concat(available).Where(strategy => attempted.Add(strategy.Id)))
        {
            await StopStrategyAsync().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            active = strategy.Id;
            Set(HubState.Recovering, $"Проверка {strategy.Id.Name} · {strategy.Id.Version}");
            try { await runner.StartAsync(strategy, token).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Logged?.Invoke($"{strategy.Id.Name}: {error.Message}");
                continue;
            }

            Health first = await connectivity.CheckAsync(settings, token).ConfigureAwait(false);
            checkedAt = clock.GetUtcNow();
            if (first == Health.TargetUnavailable)
                continue;
            await Task.Delay(timing.Retry, clock, token).ConfigureAwait(false);
            Health second = await connectivity.CheckAsync(settings, token).ConfigureAwait(false);
            checkedAt = clock.GetUtcNow();
            if (first == Health.Available && second == Health.Available && runner.IsRunning)
            {
                history.Remember(network, strategy.Id);
                try { storage.SaveHistory(history); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    Logged?.Invoke($"Не удалось сохранить рабочую конфигурацию. {error.Message}");
                }
                Prune();
                Set(HubState.Zapret, "Zapret работает");
                return StrategySearch.Working;
            }
            if (first == Health.NetworkUnavailable && second == Health.NetworkUnavailable)
            {
                await StopStrategyAsync().ConfigureAwait(false);
                WaitForNetwork();
                return StrategySearch.NetworkUnavailable;
            }
        }
        return StrategySearch.Exhausted;
    }

    private async Task<string?> RefreshAsync(CancellationToken token)
    {
        nextUpdate = clock.GetUtcNow() + timing.UpdateInterval;
        using var deadline = new CancellationTokenSource(timing.UpdateTimeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try
        {
            string? version = await releases.RefreshAsync(linked.Token).ConfigureAwait(false);
            if (version is not null)
                Logged?.Invoke($"Релиз Zapret {version} готов к проверке.");
            return version;
        }
        catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
        {
            Logged?.Invoke(error is OperationCanceledException ? "Истекло время проверки GitHub." : $"GitHub: {error.Message}");
            return null;
        }
    }

    private async Task StopStrategyAsync()
    {
        await runner.StopAsync(CancellationToken.None).ConfigureAwait(false);
        active = null;
    }

    private void Prune()
    {
        var protectedVersions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (active is not null)
            protectedVersions.Add(active.Version);
        if (history.LastWorking is { } last)
            protectedVersions.Add(last.Version);
        try { releases.Prune(settings.KeepVersions, protectedVersions); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Logged?.Invoke($"Не удалось удалить старые версии. {error.Message}");
        }
    }

    private async Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken token, bool wait = false)
    {
        if (wait)
            await operation.WaitAsync(token).ConfigureAwait(false);
        else if (!await operation.WaitAsync(0, token).ConfigureAwait(false))
            return;
        try { await action(token).ConfigureAwait(false); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Set(HubState.Error, error.Message);
        }
        finally { operation.Release(); }
    }

    private void SetVpn(VpnResult result) => Set(HubState.Vpn, result.Message, result);

    private void WaitForNetwork()
    {
        checkedAt = clock.GetUtcNow();
        nextMonitor = checkedAt.Value + timing.MaintenanceInterval;
        Set(HubState.NetworkUnavailable, "Нет доступа к контрольному ресурсу. Ожидание сети.");
    }

    private void Set(HubState state, string message, VpnResult? result = null)
    {
        Status = new(state, message, active, checkedAt, result);
        StatusChanged?.Invoke(Status);
        if (state != HubState.Checking)
            Logged?.Invoke(message);
    }
}
