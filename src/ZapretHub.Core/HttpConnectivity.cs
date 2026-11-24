namespace ZapretHub.Core;

public sealed class HttpConnectivity(HttpClient client, TimeSpan timeout, TimeProvider clock) : IConnectivity
{
    public async Task<Health> CheckAsync(HubSettings settings, CancellationToken cancellationToken)
    {
        using var controlStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var targetsStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<bool> control = IsControlReachableAsync(settings, controlStop.Token);
        Task<bool>[] targets = settings.Targets.Select(url => ProbeAsync(url, targetsStop.Token)).ToArray();
        try
        {
            await foreach (Task<bool> finished in Task.WhenEach(targets).ConfigureAwait(false))
            {
                if (!await finished.ConfigureAwait(false))
                {
                    await targetsStop.CancelAsync().ConfigureAwait(false);
                    return await control.ConfigureAwait(false) ? Health.TargetUnavailable : Health.NetworkUnavailable;
                }
            }
            return Health.Available;
        }
        finally
        {
            await controlStop.CancelAsync().ConfigureAwait(false);
            await targetsStop.CancelAsync().ConfigureAwait(false);
            // Finish canceled requests before the caller can replace the running strategy.
            try { await Task.WhenAll(targets.Append(control)).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    public Task<bool> IsControlReachableAsync(HubSettings settings, CancellationToken cancellationToken) =>
        ProbeAsync(settings.ControlUrl, cancellationToken);

    private async Task<bool> ProbeAsync(Uri url, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(timeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.CacheControl = new() { NoCache = true };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
    }
}
