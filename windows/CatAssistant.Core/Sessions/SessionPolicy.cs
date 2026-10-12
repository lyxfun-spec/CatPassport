namespace CatAssistant.Core.Sessions;

public sealed class SessionPolicy(TimeSpan timeout)
{
    public DateTimeOffset? LastCompletedAt { get; private set; }
    public bool ShouldStartNew(DateTimeOffset now) => LastCompletedAt is null || now - LastCompletedAt >= timeout;
    public void Complete(DateTimeOffset now) => LastCompletedAt = now;
    public void Reset() => LastCompletedAt = null;
}
