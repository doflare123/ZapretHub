using ZapretHub.Core;

namespace ZapretHub.Windows;

public interface IWireGuardSystem
{
    IReadOnlyList<string> Profiles();
    bool IsStopped(string profile);
    void Start(string profile);
    void Stop(string profile);
    Task<DateTimeOffset?> LatestHandshakeAsync(string profile, CancellationToken token);
}

public sealed class WireGuardVpn(IWireGuardSystem system, Func<CancellationToken, Task> sendProbe, TimeProvider clock) : IAutomaticVpn
{
    private string? owned;

    public async Task ConnectAsync(string profile, CancellationToken cancellationToken)
    {
        if (!system.Profiles().Contains(profile, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Этот профиль службы WireGuard отсутствует.");

        bool start = system.IsStopped(profile);
        DateTimeOffset earliestHandshake = clock.GetUtcNow() - TimeSpan.FromSeconds(start ? 2 : 120);
        try
        {
            if (start)
            {
                cancellationToken.ThrowIfCancellationRequested();
                system.Start(profile);
                owned = profile;
            }

            while (true)
            {
                await sendProbe(cancellationToken).ConfigureAwait(false);
                if (await system.LatestHandshakeAsync(profile, cancellationToken).ConfigureAwait(false) >= earliestHandshake)
                    return;
                if (system.IsStopped(profile))
                    throw new InvalidOperationException("Служба туннеля WireGuard остановилась.");
                await Task.Delay(TimeSpan.FromSeconds(1), clock, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            if (start && owned == profile)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(6), clock);
                await DisconnectAsync(profile, cleanup.Token).ConfigureAwait(false);
            }
            throw;
        }
    }

    public async Task DisconnectAsync(string profile, CancellationToken cancellationToken)
    {
        if (!system.Profiles().Contains(profile, StringComparer.OrdinalIgnoreCase))
        {
            owned = null;
            return;
        }
        if (!string.Equals(owned, profile, StringComparison.OrdinalIgnoreCase))
        {
            if (!system.IsStopped(profile))
                throw new InvalidOperationException("Отключите туннель, запущенный вне ZapretHub, вручную.");
            return;
        }

        while (!system.IsStopped(profile))
        {
            cancellationToken.ThrowIfCancellationRequested();
            system.Stop(profile);
            if (!system.IsStopped(profile))
                await Task.Delay(TimeSpan.FromMilliseconds(250), clock, cancellationToken).ConfigureAwait(false);
        }
        owned = null;
    }
}
