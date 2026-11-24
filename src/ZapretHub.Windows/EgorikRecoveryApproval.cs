using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using ZapretHub.Core;

namespace ZapretHub.Windows;

public sealed class EgorikRecoveryApproval : IRecoveryApproval
{
    private readonly string pipeName;
    private readonly TimeSpan connectTimeout;
    private readonly TimeSpan answerTimeout;

    public EgorikRecoveryApproval() : this($"Egorik.ZapretHub.v1.{CurrentUser().Value}", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(35)) { }

    internal EgorikRecoveryApproval(string pipeName, TimeSpan connectTimeout, TimeSpan answerTimeout)
    {
        this.pipeName = pipeName;
        this.connectTimeout = connectTimeout;
        this.answerTimeout = answerTimeout;
    }

    public async Task<RecoveryDecision> RequestAsync(Guid requestId, CancellationToken cancellationToken)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            timeout.CancelAfter(connectTimeout);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            if (!IsCurrentUserServer(pipe.SafePipeHandle))
                return RecoveryDecision.Unavailable;

            timeout.CancelAfter(answerTimeout);
            byte[] request = requestId.ToByteArray();
            await pipe.WriteAsync(request, timeout.Token).ConfigureAwait(false);
            byte[] response = new byte[17];
            await pipe.ReadExactlyAsync(response, timeout.Token).ConfigureAwait(false);
            if (!response.AsSpan(0, 16).SequenceEqual(request))
                return RecoveryDecision.Unavailable;
            return response[16] switch
            {
                1 => RecoveryDecision.Declined,
                2 => RecoveryDecision.Approved,
                _ => RecoveryDecision.Unavailable
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return RecoveryDecision.Unavailable; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or TimeoutException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return RecoveryDecision.Unavailable;
        }
    }

    private static SecurityIdentifier CurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User ?? throw new InvalidOperationException("Не удалось определить пользователя Windows.");
    }

    private static bool IsCurrentUserServer(SafePipeHandle pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe, out uint pid))
            return false;
        using var process = OpenProcess(0x1000, false, pid);
        if (process.IsInvalid || !OpenProcessToken(process, 0x0008, out var token))
            return false;
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            return identity.User is { } user && CurrentUser().Equals(user);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inheritHandle, uint processId);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
}
