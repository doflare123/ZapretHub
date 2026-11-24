namespace ZapretHub.Core;

public enum RecoveryDecision { Unavailable, Declined, Approved }

public interface IRecoveryApproval
{
    Task<RecoveryDecision> RequestAsync(Guid requestId, CancellationToken cancellationToken);
}
