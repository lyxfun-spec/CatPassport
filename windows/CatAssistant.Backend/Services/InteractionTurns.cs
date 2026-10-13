namespace CatAssistant.Backend.Services;

internal sealed class InteractionTurns(CancellationToken lifetimeToken) : IDisposable
{
    private readonly object _lock = new();
    private CancellationTokenSource? _card;
    private CancellationTokenSource? _desktop;

    public CancellationToken StartCard()
    {
        lock (_lock) return Replace(ref _card);
    }

    public CancellationToken StartDesktop()
    {
        lock (_lock) return Replace(ref _desktop);
    }

    public CancellationToken CardToken()
    {
        lock (_lock) return _card?.Token ?? throw new InvalidOperationException("卡片当前没有活动轮次。");
    }

    public void CancelCard()
    {
        lock (_lock) _card?.Cancel();
    }

    public void CancelDesktop()
    {
        lock (_lock) _desktop?.Cancel();
    }

    public void CancelAll()
    {
        lock (_lock)
        {
            _card?.Cancel();
            _desktop?.Cancel();
        }
    }

    private CancellationToken Replace(ref CancellationTokenSource? current)
    {
        current?.Cancel();
        current?.Dispose();
        current = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        return current.Token;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _card?.Cancel();
            _desktop?.Cancel();
            _card?.Dispose();
            _desktop?.Dispose();
            _card = null;
            _desktop = null;
        }
    }
}
