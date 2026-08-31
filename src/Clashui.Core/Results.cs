namespace Clashui.Core;

public enum PolicyResultKind { Ok, CancelledByUser, NeedsElevation, Failed }

public sealed record PolicyResult(PolicyResultKind Kind, string? Cause = null);

public sealed record OrchestratorResult(bool Ok, string? Cause = null)
{
    public static OrchestratorResult Success => new(true);
    public static OrchestratorResult Fail(string? cause = null) => new(false, cause);
}
