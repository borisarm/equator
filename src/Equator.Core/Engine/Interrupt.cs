namespace Equator.Core.Engine;

/// <summary>Cooperative cancellation for long-running symbolic algorithms (checked at recursion points).</summary>
public static class Interrupt
{
    [ThreadStatic] private static CancellationToken _token;

    public static IDisposable Scope(CancellationToken token)
    {
        var previous = _token;
        _token = token;
        return new Restore(previous);
    }

    public static void Check() => _token.ThrowIfCancellationRequested();

    private sealed class Restore(CancellationToken previous) : IDisposable
    {
        public void Dispose() => _token = previous;
    }
}
