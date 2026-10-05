namespace Mtp.Host;

public enum RecoveryState { Available, Recovering, Exhausted }

public sealed record RecoverySnapshot(RecoveryState State, int Attempts, int Restarts, bool CanRetry, string? Error);

internal sealed class RecoveryOperation
{
    public RecoveryState State = RecoveryState.Available;
    public int Attempts;
    public int Restarts;
    public string? Error;
    public Task<Mtp.Contracts.ProtocolResult>? Task;
    public RecoverySnapshot Snapshot => new(State, Attempts, Restarts, State == RecoveryState.Exhausted, Error);
    public void Reset() { Attempts = 0; Restarts = 0; Error = null; State = RecoveryState.Recovering; }
}
