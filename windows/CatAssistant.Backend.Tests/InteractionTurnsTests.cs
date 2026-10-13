using CatAssistant.Backend.Services;
using Xunit;

namespace CatAssistant.Backend.Tests;

public sealed class InteractionTurnsTests
{
    [Fact]
    public void StartingDesktopTurnDoesNotCancelCardTurn()
    {
        using var lifetime = new CancellationTokenSource();
        using var turns = new InteractionTurns(lifetime.Token);

        var card = turns.StartCard();
        var desktop = turns.StartDesktop();

        Assert.False(card.IsCancellationRequested);
        Assert.False(desktop.IsCancellationRequested);
    }

    [Fact]
    public void ReplacingDesktopTurnOnlyCancelsPreviousDesktopTurn()
    {
        using var lifetime = new CancellationTokenSource();
        using var turns = new InteractionTurns(lifetime.Token);
        var card = turns.StartCard();
        var previousDesktop = turns.StartDesktop();

        var currentDesktop = turns.StartDesktop();

        Assert.False(card.IsCancellationRequested);
        Assert.True(previousDesktop.IsCancellationRequested);
        Assert.False(currentDesktop.IsCancellationRequested);
    }

    [Fact]
    public void CancelAllCancelsBothInputSources()
    {
        using var lifetime = new CancellationTokenSource();
        using var turns = new InteractionTurns(lifetime.Token);
        var card = turns.StartCard();
        var desktop = turns.StartDesktop();

        turns.CancelAll();

        Assert.True(card.IsCancellationRequested);
        Assert.True(desktop.IsCancellationRequested);
    }
}
