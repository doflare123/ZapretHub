namespace ZapretHub.Core;

public enum VpnMode { Manual, Automatic }

public sealed record VpnSettings(VpnMode Mode = VpnMode.Manual, string? Application = null, string? Profile = null);

public sealed record HubSettings
{
    public int KeepVersions { get; init; } = 2;
    public int MonitorMinutes { get; init; } = 10;
    public Uri ControlUrl { get; init; } = new("https://example.com/");
    public Uri[] Targets { get; init; } = [new("https://www.youtube.com/generate_204"), new("https://discord.com/api/v10/gateway")];
    public VpnSettings Vpn { get; init; } = new();

    public void Validate()
    {
        if (KeepVersions is < 1 or > 20 || MonitorMinutes is < 1 or > 1440)
            throw new ArgumentException("Количество версий должно быть от 1 до 20, интервал проверки — от 1 до 1440 минут.");
        if (!IsHttp(ControlUrl) || Targets is not { Length: > 0 and <= 20 } || Targets.Any(url => !IsHttp(url)))
            throw new ArgumentException("Укажите контрольный URL и от 1 до 20 адресов HTTP(S) без данных для входа.");
        if (Vpn is null || !Enum.IsDefined(Vpn.Mode))
            throw new ArgumentException("Выберите режим VPN.");
        if (Vpn.Mode == VpnMode.Automatic && string.IsNullOrWhiteSpace(Vpn.Profile))
            throw new ArgumentException("Выберите профиль автоматического подключения.");
    }

    private static bool IsHttp(Uri? uri) => uri is { IsAbsoluteUri: true, Scheme: "http" or "https", UserInfo.Length: 0 };
}

public sealed record StrategyId(string Version, string Name);
public sealed record Strategy(StrategyId Id, string ScriptPath);

public interface IStrategyRunner
{
    bool IsRunning { get; }
    Task StartAsync(Strategy strategy, CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

public interface IReleaseStore
{
    IReadOnlyList<Strategy> ListStrategies();
    Task<string?> RefreshAsync(CancellationToken cancellationToken);
    void Prune(int keep, IReadOnlySet<string> protectedVersions);
}

public enum Health { Available, TargetUnavailable, NetworkUnavailable }

public interface IConnectivity
{
    Task<Health> CheckAsync(HubSettings settings, CancellationToken cancellationToken);
    Task<bool> IsControlReachableAsync(HubSettings settings, CancellationToken cancellationToken);
}
