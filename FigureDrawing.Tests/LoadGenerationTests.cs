using FigureDrawing.Core;

namespace FigureDrawing.Tests;

// The abandonment arithmetic behind INV-X-13, "a library load is abandonable".
//
// This is the tier the invariant was missing: LibraryLoadContractTests can pin *where* the guard is
// called but not whether it answers correctly — source text cannot tell a working comparison from
// an inverted one. The rule itself is Android-free, so it is tested here for real.
public class LoadGenerationTests
{
    [Fact]
    public void ATicket_IsCurrentUntilAnotherIsTaken()
    {
        var generation = new LoadGeneration();

        var first = generation.Take();
        Assert.True(generation.IsCurrent(first));

        var second = generation.Take();

        Assert.False(generation.IsCurrent(first));
        Assert.True(generation.IsCurrent(second));
    }

    // The case the invariant is named for: a slow load must not write the pool after a newer one
    // has claimed the screen.
    [Fact]
    public void ASupersededTicket_IsNeverCurrentAgain()
    {
        var generation = new LoadGeneration();

        var superseded = generation.Take();
        generation.Take();
        generation.Abandon();
        generation.Take();

        Assert.False(generation.IsCurrent(superseded));
    }

    [Fact]
    public void Abandoning_MakesEveryOutstandingTicketStale()
    {
        var generation = new LoadGeneration();
        var inFlight = generation.Take();

        generation.Abandon();

        Assert.False(generation.IsCurrent(inFlight));
    }

    // A screen that stopped and started again must be able to load: abandoning invalidates the work
    // that was running, never the work that starts afterwards.
    [Fact]
    public void ATicketTakenAfterAbandoning_IsCurrent()
    {
        var generation = new LoadGeneration();
        generation.Take();
        generation.Abandon();

        var restarted = generation.Take();

        Assert.True(generation.IsCurrent(restarted));
    }

    // Tickets are issued from one upwards, so no caller ever holds the counter's initial value.
    // Stated as a test because "0 means no ticket" is only true while Take() never returns it.
    [Fact]
    public void EveryIssuedTicket_IsAboveTheInitialValue()
    {
        var generation = new LoadGeneration();

        Assert.True(generation.Take() > 0);
        Assert.False(generation.IsCurrent(1_000_000));
    }

    // Tickets are taken on the main thread today, but the counter is the one piece of this design
    // that would silently produce two "current" loads if the increment were not atomic.
    [Fact]
    public void TicketsTakenConcurrently_AreAllDistinct()
    {
        var generation = new LoadGeneration();
        var tickets = new System.Collections.Concurrent.ConcurrentBag<int>();

        Parallel.For(0, 500, _ => tickets.Add(generation.Take()));

        Assert.Equal(500, tickets.Distinct().Count());

        // Exactly one of them may write the screen.
        Assert.Single(tickets.Where(generation.IsCurrent));
    }
}
