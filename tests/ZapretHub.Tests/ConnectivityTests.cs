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
        Task responders = Task.WhenAll(RespondAsync(), RespondAsync());
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
        var probe = new HttpConnectivity(client, TimeSpan.FromSeconds(5), TimeProvider.System);
        var settings = new HubSettings
        {
            ControlUrl = new Uri($"http://127.0.0.1:{port}/control"),
            Targets = [new($"http://127.0.0.1:{port}/target")]
        };

        Assert.Equal(expected, await probe.CheckAsync(settings, TestContext.Current.CancellationToken));
        await responders.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        async Task RespondAsync()
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            using var peer = await server.AcceptTcpClientAsync(token);
            using var stream = peer.GetStream();
            using var reader = new StreamReader(stream, leaveOpen: true);
            string line = (await reader.ReadLineAsync(token))!;
            int code = line.Contains(" /control ", StringComparison.Ordinal) ? control : target;
            while (await reader.ReadLineAsync(token) is { Length: > 0 }) { }
            using var writer = new StreamWriter(stream, leaveOpen: true);
            await writer.WriteAsync($"HTTP/1.1 {code} Test\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await writer.FlushAsync(token);
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
