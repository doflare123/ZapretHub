using System.IO.Pipes;
using ZapretHub.Core;
using ZapretHub.Windows;

namespace ZapretHub.Tests;

public sealed class EgorikRecoveryApprovalTests
{
    [Theory]
    [InlineData(1, true, RecoveryDecision.Declined)]
    [InlineData(2, true, RecoveryDecision.Approved)]
    [InlineData(2, false, RecoveryDecision.Unavailable)]
    [InlineData(0, true, RecoveryDecision.Unavailable)]
    public async Task ReadsOnlyRepliesForTheCurrentRequestOverAWindowsPipe(int value, bool matchingId, RecoveryDecision expected)
    {
        string pipeName = $"ZapretHub.test.{Guid.NewGuid():N}";
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var client = new EgorikRecoveryApproval(pipeName, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        var requestId = Guid.NewGuid();

        Task<RecoveryDecision> pending = client.RequestAsync(requestId, deadline.Token);
        await server.WaitForConnectionAsync(deadline.Token);
        byte[] response = new byte[17];
        await server.ReadExactlyAsync(response.AsMemory(0, 16), deadline.Token);
        Assert.Equal(requestId, new Guid(response.AsSpan(0, 16)));
        if (!matchingId)
            response[0] ^= 1;
        response[16] = (byte)value;
        await server.WriteAsync(response, deadline.Token);

        Assert.Equal(expected, await pending);
    }

    [Fact]
    public async Task ReturnsUnavailableWhenAssistantIsNotRunning()
    {
        var client = new EgorikRecoveryApproval($"ZapretHub.test.{Guid.NewGuid():N}", TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1));

        Assert.Equal(RecoveryDecision.Unavailable, await client.RequestAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancellingARequestClosesItsConnectionWithoutWaitingForAnAnswer()
    {
        string pipeName = $"ZapretHub.test.{Guid.NewGuid():N}";
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var request = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var client = new EgorikRecoveryApproval(pipeName, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
        Task<RecoveryDecision> pending = client.RequestAsync(Guid.NewGuid(), request.Token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        await server.WaitForConnectionAsync(deadline.Token);
        await server.ReadExactlyAsync(new byte[16], deadline.Token);

        await request.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(0, await server.ReadAsync(new byte[1], deadline.Token));
    }
}
