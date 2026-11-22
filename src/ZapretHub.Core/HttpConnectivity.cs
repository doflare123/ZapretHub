namespace ZapretHub.Core;

public sealed class HttpConnectivity(HttpClient client, TimeSpan timeout, TimeProvider clock) : IConnectivity
{
    public async Task<Health> CheckAsync(HubSettings settings, CancellationToken cancellationToken)
    {
        Task<bool> control = IsControlReachableAsync(settings, cancellationToken);
        Task<bool>[] targets = settings.Targets.Select(url => ProbeAsync(url, cancellationToken)).ToArray();
        bool reachable = await control.ConfigureAwait(false);
        bool[] results = await Task.WhenAll(targets).ConfigureAwait(false);
        if (results.All(success => success))
            return Health.Available;
        return reachable ? Health.TargetUnavailable : Health.NetworkUnavailable;
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
