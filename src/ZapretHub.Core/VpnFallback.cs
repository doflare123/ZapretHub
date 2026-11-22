namespace ZapretHub.Core;

public enum VpnState { NotConfigured, WaitingForUser, Connecting, Connected, Failed, Unknown }

public sealed record VpnResult(VpnState State, string Message, bool CanOpenManually = false);

public interface IManualVpnLauncher
{
    Task OpenAsync(string application, CancellationToken cancellationToken);
}

public interface IAutomaticVpn
{
    Task ConnectAsync(string profile, CancellationToken cancellationToken);
    Task DisconnectAsync(string profile, CancellationToken cancellationToken);
}

public sealed class VpnFallback(IManualVpnLauncher launcher, IAutomaticVpn automatic, TimeProvider clock, TimeSpan timeout)
{
    public async Task<VpnResult> ConnectAsync(VpnSettings settings, Action connecting, CancellationToken cancellationToken)
    {
        if (settings.Mode == VpnMode.Manual)
            return await OpenManuallyAsync(settings, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(settings.Profile))
            return new(VpnState.NotConfigured, "Выберите профиль VPN в настройках.", CanOpen(settings));

        connecting();
        using var deadline = new CancellationTokenSource(timeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            await automatic.ConnectAsync(settings.Profile, linked.Token).ConfigureAwait(false);
            return new(VpnState.Connected, $"VPN подключён · {settings.Profile}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(VpnState.Failed, "VPN не подтвердил подключение вовремя.", CanOpen(settings));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new(VpnState.Failed, $"Не удалось подключить VPN. {error.Message}", CanOpen(settings));
        }
    }

    public async Task<VpnResult> OpenManuallyAsync(VpnSettings settings, CancellationToken cancellationToken)
    {
        if (!CanOpen(settings))
            return new(VpnState.NotConfigured, "Выберите VPN-приложение в настройках.");
        try
        {
            await launcher.OpenAsync(settings.Application!, cancellationToken).ConfigureAwait(false);
            return new(VpnState.WaitingForUser, "Нажмите «Подключить» в VPN-клиенте. Состояние туннеля неизвестно.");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new(VpnState.Failed, $"Не удалось открыть VPN-клиент. {error.Message}");
        }
    }

    public async Task DisconnectAsync(VpnSettings settings, CancellationToken cancellationToken)
    {
        if (settings.Mode != VpnMode.Automatic || string.IsNullOrWhiteSpace(settings.Profile))
            return;
        using var deadline = new CancellationTokenSource(timeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            await automatic.DisconnectAsync(settings.Profile, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("VPN не подтвердил отключение вовремя.");
        }
    }

    private static bool CanOpen(VpnSettings settings) => !string.IsNullOrWhiteSpace(settings.Application);
}
