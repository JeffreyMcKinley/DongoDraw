namespace DongoDraw.Core;

public sealed class LoadGeneration
{
    int current;

    public int Take() => Interlocked.Increment(ref current);

    public void Abandon() => Interlocked.Increment(ref current);

    // Volatile: the worker polls this in a loop, and a hoisted read never stops the walk.
    public bool IsCurrent(int ticket) => Volatile.Read(ref current) == ticket;
}
