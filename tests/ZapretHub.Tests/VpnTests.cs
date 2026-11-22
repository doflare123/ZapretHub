using Microsoft.Extensions.Time.Testing;
using ZapretHub.Core;
using ZapretHub.Windows;

namespace ZapretHub.Tests;

public sealed class VpnTests
{
    [Fact]
    public async Task WireGuardReportsConnectedOnlyAfterAHandshakeAndStopsItsOwnTunnel()
    {
        var clock = new FakeTimeProvider();
        var system = new TunnelSystem { Handshake = clock.GetUtcNow() - TimeSpan.FromMinutes(3) };
        var vpn = new VpnFallback(new Manual(), new WireGuardVpn(system, _ => Task.CompletedTask, clock), clock, TimeSpan.FromSeconds(25));
        var settings = new VpnSettings(VpnMode.Automatic, Profile: "office");
        bool connecting = false;

        Task<VpnResult> result = vpn.ConnectAsync(settings, () => connecting = true, TestContext.Current.CancellationToken);

        Assert.True(connecting);
        Assert.Equal(["start:office"], system.Events);
        Assert.False(result.IsCompleted);
        system.Handshake = clock.GetUtcNow();
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(VpnState.Connected, (await result).State);

        await vpn.DisconnectAsync(settings, TestContext.Current.CancellationToken);
        Assert.Equal(["start:office", "stop:office"], system.Events);
        Assert.True(system.IsStopped("office"));
    }

    [Fact]
    public async Task HandshakeTimeoutStopsNewTunnelAndOffersManualFallback()
    {
        var clock = new FakeTimeProvider();
        var system = new TunnelSystem();
        var vpn = new VpnFallback(new Manual(), new WireGuardVpn(system, _ => Task.CompletedTask, clock), clock, TimeSpan.FromSeconds(25));

        Task<VpnResult> operation = vpn.ConnectAsync(new(VpnMode.Automatic, "fallback.exe", "office"), () => { }, TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(25));
        VpnResult result = await operation.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(VpnState.Failed, result.State);
        Assert.Contains("вовремя", result.Message);
        Assert.True(result.CanOpenManually);
        Assert.Equal(["start:office", "stop:office"], system.Events);
        Assert.True(system.IsStopped("office"));
    }

    [Fact]
    public async Task UnsupportedProfileDoesNotStartAnyTunnel()
    {
        var clock = new FakeTimeProvider();
        var system = new TunnelSystem();
        var vpn = new VpnFallback(new Manual(), new WireGuardVpn(system, _ => Task.CompletedTask, clock), clock, TimeSpan.FromSeconds(25));

        VpnResult result = await vpn.ConnectAsync(new(VpnMode.Automatic, Profile: "missing"), () => { }, TestContext.Current.CancellationToken);

        Assert.Equal(VpnState.Failed, result.State);
        Assert.Contains("отсутствует", result.Message);
        Assert.Empty(system.Events);
    }

    [Fact]
    public async Task AManuallyStartedTunnelIsNotStoppedByZapretHub()
    {
        var clock = new FakeTimeProvider();
        var system = new TunnelSystem { Stopped = false, Handshake = clock.GetUtcNow() };
        var vpn = new WireGuardVpn(system, _ => Task.CompletedTask, clock);
        await vpn.ConnectAsync("office", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => vpn.DisconnectAsync("office", TestContext.Current.CancellationToken));

        Assert.Empty(system.Events);
        Assert.False(system.Stopped);
    }

    [Fact]
    public async Task AnUnavailableManualClientProducesAnErrorInsteadOfConnectedState()
    {
        using var directory = new TestDirectory();
        var vpn = new VpnFallback(new ManualVpnLauncher(), null!, TimeProvider.System, TimeSpan.FromSeconds(25));

        VpnResult result = await vpn.OpenManuallyAsync(new(Application: Path.Combine(directory.Path, "missing.exe")),
            TestContext.Current.CancellationToken);

        Assert.Equal(VpnState.Failed, result.State);
        Assert.Contains("не найден", result.Message);
    }

    private sealed class Manual : IManualVpnLauncher
    {
        public Task OpenAsync(string application, CancellationToken cancellationToken) => throw new InvalidOperationException();
    }

    private sealed class TunnelSystem : IWireGuardSystem
    {
        public List<string> Events { get; } = [];
        public bool Stopped { get; set; } = true;
        public DateTimeOffset? Handshake { get; set; }
        public IReadOnlyList<string> Profiles() => ["office"];
        public bool IsStopped(string profile) => Stopped;
        public void Start(string profile) { Events.Add($"start:{profile}"); Stopped = false; }
        public void Stop(string profile) { Events.Add($"stop:{profile}"); Stopped = true; }
        public Task<DateTimeOffset?> LatestHandshakeAsync(string profile, CancellationToken token) => Task.FromResult(Handshake);
    }
}
