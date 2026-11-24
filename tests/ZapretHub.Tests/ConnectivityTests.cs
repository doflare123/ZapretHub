using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Time.Testing;
using ZapretHub.Core;

namespace ZapretHub.Tests;

public sealed class ConnectivityTests
{
    [Theory]
    [InlineData(200, 204, Health.Available)]
    [InlineData(200, 503, Health.TargetUnavailable)]
    [InlineData(503, 503, Health.NetworkUnavailable)]
    public async Task ClassifiesActualHttpResponses(int control, int target, Health expected)
    {
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        int port = ((IPEndPoint)server.LocalEndpoint).Port;
        using var listening = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task responders = Task.WhenAll(RespondAsync(), RespondAsync());
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
        var probe = new HttpConnectivity(client, TimeSpan.FromSeconds(5), TimeProvider.System);
        var settings = new HubSettings
        {
            ControlUrl = new Uri($"http://127.0.0.1:{port}/control"),
            Targets = [new($"http://127.0.0.1:{port}/target")]
        };

        try { Assert.Equal(expected, await probe.CheckAsync(settings, TestContext.Current.CancellationToken)); }
        finally
        {
            await listening.CancelAsync();
            try { await responders.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); }
            catch (OperationCanceledException) when (listening.IsCancellationRequested) { }
        }

        async Task RespondAsync()
        {
            CancellationToken token = listening.Token;
            using var peer = await server.AcceptTcpClientAsync(token);
            using var stream = peer.GetStream();
            using var reader = new StreamReader(stream, leaveOpen: true);
            string? line = await reader.ReadLineAsync(token);
            if (line is null)
                return;
            int code = line.Contains(" /control ", StringComparison.Ordinal) ? control : target;
            while (await reader.ReadLineAsync(token) is { Length: > 0 }) { }
            using var writer = new StreamWriter(stream, leaveOpen: true);
            await writer.WriteAsync($"HTTP/1.1 {code} Test\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await writer.FlushAsync(token);
        }
    }

    [Theory]
    [InlineData(true, Health.TargetUnavailable)]
    [InlineData(false, Health.Available)]
    public async Task FinishesWithoutWaitingForAnIrrelevantHungRequest(bool targetFails, Health expected)
    {
        var clock = new FakeTimeProvider();
        string pending = targetFails ? "/other-target" : "/control";
        using var handler = new MixedHttpHandler(pending, targetFails);
        using var client = new HttpClient(handler);
        var probe = new HttpConnectivity(client, TimeSpan.FromSeconds(4), clock);
        var settings = new HubSettings
        {
            ControlUrl = new("http://test.invalid/control"),
            Targets = [new("http://test.invalid/target"), new("http://test.invalid/other-target")]
        };
        DateTimeOffset start = clock.GetUtcNow();

        Health actual = await probe.CheckAsync(settings, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.Equal(expected, actual);
        Assert.True(handler.PendingCanceled);
        Assert.Equal(start, clock.GetUtcNow());
    }

    private sealed class MixedHttpHandler(string pendingPath, bool targetFails) : HttpMessageHandler
    {
        public bool PendingCanceled { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path == pendingPath)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                finally { PendingCanceled = cancellationToken.IsCancellationRequested; }
            }
            return new HttpResponseMessage(path == "/target" && targetFails ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task HungHttpProbeTimesOutIndependentlyOfTheCaller()
    {
        var clock = new FakeTimeProvider();
        using var client = new HttpClient(new HungHttpHandler());
        var probe = new HttpConnectivity(client, TimeSpan.FromSeconds(4), clock);
        Task<bool> result = probe.IsControlReachableAsync(new HubSettings(), TestContext.Current.CancellationToken);

        Assert.False(result.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(4));

        Assert.False(await result.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    private sealed class HungHttpHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }
    }
}
