using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using ZapretHub.Core;

namespace ZapretHub.Tests;

public sealed class HubTests
{
    [Fact]
    public async Task StartupUsesLastKnownStrategyWhileGitHubIsStillPendingAndRemembersIt()
    {
        await using var fixture = new Fixture();
        fixture.Releases.Strategies.AddRange([Candidate("1.2", "general"), Candidate("1.1", "known")]);
        var history = new StrategyHistory();
        history.Remember("home", new("1.1", "known"));
        fixture.Storage.SaveHistory(history);
        var release = new TaskCompletionSource<string?>();
        fixture.Releases.Refresh = token => release.Task.WaitAsync(token);

        fixture.Start();
        HubStatus status = await fixture.WaitForAsync(status => status.State == HubState.Zapret);

        Assert.Equal(new StrategyId("1.1", "known"), status.Strategy);
        Assert.Equal(new StrategyId("1.1", "known"), fixture.Storage.LoadHistory().LastWorking);
        Assert.Contains("1.1", fixture.Releases.Protected);
        Assert.False(release.Task.IsCompleted);
        release.SetResult(null);
    }

    [Fact]
    public async Task TransientFailureKeepsCurrentConfiguration()
    {
        await using var fixture = new Fixture();
        fixture.Releases.Strategies.Add(Candidate("1.1", "first"));
        fixture.Start();
        await fixture.WaitForAsync(status => status.State == HubState.Zapret);
        fixture.Connectivity.Results.Enqueue(Health.TargetUnavailable);
        fixture.Connectivity.Results.Enqueue(Health.Available);

        await fixture.Hub.CheckNowAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HubState.Zapret, fixture.Hub.Status.State);
        Assert.Equal(["start:first"], fixture.Runner.Events);
    }

    [Fact]
    public async Task ConfirmedFailureRotatesWithoutStartingTwoStrategiesAtOnce()
    {
        var assistant = new Approval();
        await using var fixture = new Fixture(autoRecover: true, approval: assistant);
        fixture.Releases.Strategies.AddRange([Candidate("1.1", "first"), Candidate("1.1", "second")]);
        fixture.Start();
        await fixture.WaitForAsync(status => status.State == HubState.Zapret);
        fixture.Connectivity.Results.Enqueue(Health.TargetUnavailable);
        fixture.Connectivity.Results.Enqueue(Health.TargetUnavailable);

        await fixture.Hub.CheckNowAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["start:first", "stop:first", "start:second"], fixture.Runner.Events);
        Assert.Equal(new StrategyId("1.1", "second"), fixture.Storage.LoadHistory().LastWorking);
        Assert.Empty(assistant.Requests);
        Assert.Empty(fixture.Notifications);
    }

    [Fact]
    public async Task DefaultModeNotifiesOnceAndOnlyTheManualActionStartsRecovery()
    {
        await using var fixture = new Fixture();
        await StartWithTwoStrategiesAsync(fixture);

        Guid request = await ConfirmFailureAsync(fixture);
        Assert.Equal(request, await ConfirmFailureAsync(fixture));
        Assert.Equal([request], fixture.Notifications);
        Assert.Equal(["start:first"], fixture.Runner.Events);

        fixture.Connectivity.Results.Enqueue(Health.TargetUnavailable);
        await fixture.Hub.FindStableAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(["start:first", "stop:first", "start:second"], fixture.Runner.Events);
    }

    [Fact]
    public async Task AssistantCanApproveRecoveryWithoutAHubNotification()
    {
        var assistant = new Approval();
        await using var fixture = new Fixture(approval: assistant);
        await StartWithTwoStrategiesAsync(fixture);

        Guid request = await ConfirmFailureAsync(fixture);
        Assert.Equal([request], assistant.Requests);
        Assert.Equal(["start:first"], fixture.Runner.Events);
        fixture.Connectivity.Results.Enqueue(Health.TargetUnavailable);
        assistant.Response.SetResult(RecoveryDecision.Approved);

        await fixture.WaitForAsync(status => status is { State: HubState.Zapret, Strategy.Name: "second" });
        Assert.Equal(["start:first", "stop:first", "start:second"], fixture.Runner.Events);
        Assert.Empty(fixture.Notifications);
    }

    [Fact]
    public async Task UnavailableAssistantFallsBackToOneNotificationAndKeepsTheConfig()
    {
        var assistant = new Approval();
        await using var fixture = new Fixture(approval: assistant);
        await StartWithTwoStrategiesAsync(fixture);

        Guid request = await ConfirmFailureAsync(fixture);
        assistant.Response.SetResult(RecoveryDecision.Unavailable);
        Assert.Equal(request, await fixture.Notification.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        await ConfirmFailureAsync(fixture);

        Assert.Equal([request], fixture.Notifications);
        Assert.Equal([request], assistant.Requests);
        Assert.Equal(["start:first"], fixture.Runner.Events);
    }

    [Fact]
    public async Task VoiceRejectionKeepsStrategyAndDoesNotAskAgainDuringTheSameFailure()
    {
        var assistant = new Approval();
        await using var fixture = new Fixture(approval: assistant);
        await StartWithTwoStrategiesAsync(fixture);

        Guid request = await ConfirmFailureAsync(fixture);
        assistant.Response.SetResult(RecoveryDecision.Declined);
        Assert.Equal(request, await ConfirmFailureAsync(fixture));

        Assert.Equal([request], assistant.Requests);
        Assert.Empty(fixture.Notifications);
        Assert.Equal(["start:first"], fixture.Runner.Events);
    }

    [Fact]
    public async Task RecoveredConnectionCancelsVoicePromptAndRejectsStaleApprovals()
    {
        var assistant = new Approval();
        await using var fixture = new Fixture(approval: assistant);
        await StartWithTwoStrategiesAsync(fixture);

        Guid request = await ConfirmFailureAsync(fixture);
        await fixture.Hub.CheckNowAsync(TestContext.Current.CancellationToken);
        assistant.Response.SetResult(RecoveryDecision.Approved);
        await fixture.Hub.FindStableAsync(request, TestContext.Current.CancellationToken);

        Assert.True(assistant.Token.IsCancellationRequested);
        Assert.Null(fixture.Hub.Status.RecoveryRequestId);
        Assert.Equal(["start:first"], fixture.Runner.Events);
    }

    [Fact]
    public async Task ApprovalRechecksTheCurrentStrategyBeforeStoppingIt()
    {
        var assistant = new Approval();
        await using var fixture = new Fixture(approval: assistant);
        await StartWithTwoStrategiesAsync(fixture);
        await ConfirmFailureAsync(fixture);

        assistant.Response.SetResult(RecoveryDecision.Approved);

        await fixture.WaitForAsync(status => status.State == HubState.AwaitingRecovery);
        await fixture.WaitForAsync(status => status.State == HubState.Zapret);
        Assert.Equal(["start:first"], fixture.Runner.Events);
    }

    private static async Task StartWithTwoStrategiesAsync(Fixture fixture)
    {
        fixture.Releases.Strategies.AddRange([Candidate("1.1", "first"), Candidate("1.1", "second")]);
        fixture.Start();
        await fixture.WaitForAsync(status => status.State == HubState.Zapret);
    }

    private static async Task<Guid> ConfirmFailureAsync(Fixture fixture)
    {
        fixture.Connectivity.Results.Enqueue(Health.TargetUnavailable);
        fixture.Connectivity.Results.Enqueue(Health.TargetUnavailable);
        await fixture.Hub.CheckNowAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HubState.AwaitingRecovery, fixture.Hub.Status.State);
        return Assert.IsType<Guid>(fixture.Hub.Status.RecoveryRequestId);
    }

    [Fact]
    public async Task ExhaustedLocalStrategiesTryFreshReleaseBeforeOpeningVpn()
    {
        await using var fixture = new Fixture();
        fixture.Releases.Strategies.Add(Candidate("1.1", "old"));
        var release = new TaskCompletionSource<string?>();
        fixture.Releases.Refresh = async token =>
        {
            string? version = await release.Task.WaitAsync(token);
            fixture.Releases.Strategies.Add(Candidate(version!, "new"));
            return version;
        };
        fixture.Connectivity.Results.Enqueue(Health.TargetUnavailable);
        fixture.Start();
        await fixture.WaitForAsync(status => status is { State: HubState.Recovering, Strategy.Name: "old" });
        release.SetResult("1.2");

        HubStatus status = await fixture.WaitForAsync(status => status.State == HubState.Zapret);

        Assert.Equal(new StrategyId("1.2", "new"), status.Strategy);
        Assert.Equal(["start:old", "stop:old", "start:new"], fixture.Runner.Events);
        Assert.Empty(fixture.Manual.Opened);
    }

    [Fact]
    public async Task GitHubFailureStillStopsZapretBeforeOpeningManualVpnAndPausesChecks()
    {
        await using var fixture = new Fixture();
        fixture.Releases.Strategies.Add(Candidate("1.1", "old"));
        fixture.Connectivity.Results.Enqueue(Health.TargetUnavailable);
        fixture.Releases.Refresh = _ => throw new HttpRequestException("offline");
        fixture.Manual.Opening = () => Assert.False(fixture.Runner.IsRunning);
        fixture.Start();

        HubStatus status = await fixture.WaitForAsync(status => status.Vpn?.State == VpnState.WaitingForUser);
        await fixture.Hub.CheckNowAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["vpn.exe"], fixture.Manual.Opened);
        Assert.Equal(["start:old", "stop:old"], fixture.Runner.Events);
        Assert.Contains("неизвестно", status.Message);
        Assert.Equal(1, fixture.Connectivity.Checks);

        await fixture.Hub.ResumeZapretAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HubState.Zapret, fixture.Hub.Status.State);
    }

    [Fact]
    public async Task OverallOutagePausesSearchAndRecoversOnNextMaintenanceTick()
    {
        await using var fixture = new Fixture();
        fixture.Releases.Strategies.AddRange([Candidate("1.1", "first"), Candidate("1.1", "second")]);
        fixture.Connectivity.Results.Enqueue(Health.NetworkUnavailable);
        fixture.Connectivity.Results.Enqueue(Health.NetworkUnavailable);
        fixture.Start();
        await fixture.WaitForAsync(status => status.State == HubState.NetworkUnavailable);

        Assert.Equal(["start:first", "stop:first"], fixture.Runner.Events);
        Assert.Empty(fixture.Manual.Opened);

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.WaitForAsync(status => status.State == HubState.Zapret);
        Assert.Equal("start:first", fixture.Runner.Events.Last());
    }

    [Fact]
    public async Task DefaultMonitoringAndUpdateSchedulesRunAtTenMinutesAndOneDay()
    {
        await using var fixture = new Fixture();
        fixture.Releases.Strategies.Add(Candidate("1.1", "first"));
        fixture.Start();
        await fixture.WaitForAsync(status => status.State == HubState.Zapret);
        fixture.Clock.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal(2, fixture.Connectivity.Checks);

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.WaitForAsync(status => status.State == HubState.Checking);
        await fixture.WaitForAsync(status => status.State == HubState.Zapret);
        Assert.Equal(3, fixture.Connectivity.Checks);
        Assert.Equal(1, fixture.Releases.Checks);

        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Releases.Refresh = _ => { refreshed.SetResult(); return Task.FromResult<string?>(null); };
        fixture.Clock.Advance(TimeSpan.FromDays(1) - TimeSpan.FromMinutes(10));
        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(2, fixture.Releases.Checks);
    }

    [Fact]
    public async Task ConcurrentCheckDoesNotStartSecondRecoveryAndTimeoutHandsOffToVpn()
    {
        await using var fixture = new Fixture();
        var probing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Releases.Strategies.Add(Candidate("1.1", "slow"));
        fixture.Connectivity.Probe = async token =>
        {
            probing.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Health.Available;
        };
        fixture.Start();
        await probing.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await fixture.Hub.CheckNowAsync(TestContext.Current.CancellationToken);
        fixture.Clock.Advance(TimeSpan.FromMinutes(3));
        await fixture.WaitForAsync(status => status.Vpn?.State == VpnState.WaitingForUser);

        Assert.Equal(["start:slow", "stop:slow"], fixture.Runner.Events);
        Assert.Single(fixture.Manual.Opened);
    }

    [Fact]
    public async Task AutomaticFallbackStartsOnlyAfterZapretStopsAndDisconnectsOnReturn()
    {
        await using var fixture = new Fixture(new(VpnMode.Automatic, "vpn.exe", "home-wg"));
        fixture.Releases.Strategies.Add(Candidate("1.1", "old"));
        fixture.Connectivity.Results.Enqueue(Health.TargetUnavailable);
        fixture.Automatic.Connecting = () => Assert.False(fixture.Runner.IsRunning);
        fixture.Start();

        await fixture.WaitForAsync(status => status.Vpn?.State == VpnState.Connected);
        await fixture.Hub.ResumeZapretAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["connect:home-wg", "disconnect:home-wg"], fixture.Automatic.Events);
        Assert.Empty(fixture.Manual.Opened);
        Assert.Equal(HubState.Zapret, fixture.Hub.Status.State);
    }

    [Fact]
    public async Task RestartWhileInVpnModeDoesNotTreatVpnTrafficAsAWorkingZapretStrategy()
    {
        await using var fixture = new Fixture();
        fixture.Storage.SaveFallback(new VpnSettings(Application: "vpn.exe"));
        fixture.Releases.Strategies.Add(Candidate("1.1", "old"));
        fixture.Start();

        await fixture.WaitForAsync(status => status.Vpn?.State == VpnState.Unknown);
        await fixture.Hub.CheckNowAsync(TestContext.Current.CancellationToken);

        Assert.Empty(fixture.Runner.Events);
        Assert.Empty(fixture.Manual.Opened);
        Assert.Equal(0, fixture.Connectivity.Checks);

        await fixture.Hub.ResumeZapretAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HubState.Zapret, fixture.Hub.Status.State);
        Assert.Null(fixture.Storage.LoadFallback());
    }

    private static Strategy Candidate(string version, string name) => new(new(version, name), $"{name}.bat");

    private sealed class Fixture(VpnSettings? vpn = null, bool autoRecover = false, IRecoveryApproval? approval = null) : IAsyncDisposable
    {
        private readonly TestDirectory directory = new();
        private readonly Channel<HubStatus> statuses = Channel.CreateUnbounded<HubStatus>();
        private readonly CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        private Task? run;
        public readonly FakeTimeProvider Clock = new();
        public readonly Releases Releases = new();
        public readonly Runner Runner = new();
        public readonly Connectivity Connectivity = new();
        public readonly ManualClient Manual = new();
        public readonly AutomaticClient Automatic = new();
        public readonly List<Guid> Notifications = [];
        public readonly TaskCompletionSource<Guid> Notification = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LocalState Storage => new(directory.Path);
        public Hub Hub { get; private set; } = null!;

        public void Start()
        {
            Hub = new(new HubSettings { Vpn = vpn ?? new(Application: "vpn.exe"), AutoRecover = autoRecover }, Releases, Runner, Connectivity,
                new(Manual, Automatic, Clock, TimeSpan.FromSeconds(25)), Storage, () => "home", Clock,
                new() { Retry = TimeSpan.Zero }, approval);
            Hub.StatusChanged += status => statuses.Writer.TryWrite(status);
            Hub.RecoveryNotification += request => { Notifications.Add(request); Notification.TrySetResult(request); };
            run = Hub.RunAsync(stop.Token);
        }

        public async Task<HubStatus> WaitForAsync(Func<HubStatus, bool> predicate)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            while (true)
            {
                HubStatus status = await statuses.Reader.ReadAsync(timeout.Token);
                if (predicate(status))
                    return status;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            if (run is not null)
                await run.WaitAsync(TimeSpan.FromSeconds(5));
            stop.Dispose();
            directory.Dispose();
        }
    }

    private sealed class Approval : IRecoveryApproval
    {
        public readonly List<Guid> Requests = [];
        public readonly TaskCompletionSource<RecoveryDecision> Response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }

        public Task<RecoveryDecision> RequestAsync(Guid requestId, CancellationToken cancellationToken)
        {
            Requests.Add(requestId);
            Token = cancellationToken;
            return Response.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class Releases : IReleaseStore
    {
        public List<Strategy> Strategies { get; } = [];
        public Func<CancellationToken, Task<string?>> Refresh { get; set; } = _ => Task.FromResult<string?>(null);
        public IReadOnlySet<string> Protected { get; private set; } = new HashSet<string>();
        public int Checks { get; private set; }
        public IReadOnlyList<Strategy> ListStrategies() => Strategies.ToArray();
        public Task<string?> RefreshAsync(CancellationToken cancellationToken) { Checks++; return Refresh(cancellationToken); }
        public void Prune(int keep, IReadOnlySet<string> protectedVersions) => Protected = protectedVersions;
    }

    private sealed class Runner : IStrategyRunner
    {
        private StrategyId? current;
        public List<string> Events { get; } = [];
        public bool IsRunning => current is not null;
        public Task StartAsync(Strategy strategy, CancellationToken cancellationToken)
        {
            Assert.False(IsRunning);
            current = strategy.Id;
            Events.Add($"start:{current.Name}");
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken cancellationToken)
        {
            if (current is not null)
                Events.Add($"stop:{current.Name}");
            current = null;
            return Task.CompletedTask;
        }
    }

    private sealed class Connectivity : IConnectivity
    {
        public Queue<Health> Results { get; } = new();
        public Func<CancellationToken, Task<Health>>? Probe { get; set; }
        public int Checks { get; private set; }
        public Task<Health> CheckAsync(HubSettings settings, CancellationToken cancellationToken)
        {
            Checks++;
            return Probe?.Invoke(cancellationToken) ?? Task.FromResult(Results.TryDequeue(out var health) ? health : Health.Available);
        }
        public Task<bool> IsControlReachableAsync(HubSettings settings, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class ManualClient : IManualVpnLauncher
    {
        public List<string> Opened { get; } = [];
        public Action? Opening { get; set; }
        public Task OpenAsync(string application, CancellationToken cancellationToken)
        {
            Opening?.Invoke();
            Opened.Add(application);
            return Task.CompletedTask;
        }
    }

    private sealed class AutomaticClient : IAutomaticVpn
    {
        public List<string> Events { get; } = [];
        public Action? Connecting { get; set; }
        public Task ConnectAsync(string profile, CancellationToken cancellationToken)
        {
            Connecting?.Invoke();
            Events.Add($"connect:{profile}");
            return Task.CompletedTask;
        }
        public Task DisconnectAsync(string profile, CancellationToken cancellationToken)
        {
            Events.Add($"disconnect:{profile}");
            return Task.CompletedTask;
        }
    }
}
