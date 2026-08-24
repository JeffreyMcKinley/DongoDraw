namespace FigureDrawing.Core;

// Which piece of background work is still allowed to publish its result (INV-X-13).
//
// Infrastructure, not a domain object: it belongs beside BitmapMath and GridContrast in the "pure
// calculation the Android layer needs" group rather than in the object catalogue of
// DOMAIN-MODEL.md §1. It holds no domain rule and names nothing in the ubiquitous language.
//
// It lives in Core because it is Android-free arithmetic and the rule it carries is the one this
// app most needs to be able to test: a load that has been superseded or abandoned must never write
// the screen. Left in the Android layer it would be reachable only by reading source text, which
// cannot tell a working guard from an inverted one (ARCHITECTURE.md §11, §14).
//
// Every method is safe to call from any thread: tickets are taken and abandoned on the main thread,
// but the worker reads IsCurrent to decide whether to keep going.
public sealed class LoadGeneration
{
    int current;

    // Claims the right to publish, and supersedes every ticket taken before it. Callers take a
    // ticket before starting work, never after — work that never begins must still supersede
    // whatever was already running, or a slow predecessor overwrites the folder that replaced it.
    public int Take() => Interlocked.Increment(ref current);

    // Abandons every outstanding ticket without issuing one. What the screen calls when it stops,
    // is destroyed, or deliberately drops the work it was showing.
    public void Abandon() => Interlocked.Increment(ref current);

    // Whether this ticket is still the most recent. Volatile because the worker polls it: a hoisted
    // read would leave an abandoned walk running to the end of the folder.
    public bool IsCurrent(int ticket) => Volatile.Read(ref current) == ticket;
}
