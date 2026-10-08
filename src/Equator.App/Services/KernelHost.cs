using System.Collections.Concurrent;
using Equator.Core.Engine;

namespace Equator.App.Services;

/// <summary>
/// Runs the kernel on a dedicated thread with a large stack (deeply recursive symbolic algorithms),
/// serialising evaluations and supporting cooperative abort.
/// </summary>
public sealed class KernelHost : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private CancellationTokenSource? _current;

    public Kernel Kernel { get; } = new();

    public KernelHost()
    {
        _thread = new Thread(Run, 512 * 1024 * 1024) { IsBackground = true, Name = "Equator kernel" };
        _thread.Start();
    }

    private void Run()
    {
        foreach (var work in _queue.GetConsumingEnumerable()) work();
    }

    public Task<EvaluationResult> EvaluateAsync(string input)
    {
        var tcs = new TaskCompletionSource<EvaluationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            var cts = new CancellationTokenSource();
            Interlocked.Exchange(ref _current, cts)?.Dispose();
            try
            {
                tcs.SetResult(Kernel.Evaluate(input, cts.Token));
            }
            catch (Exception ex)
            {
                // Last-resort guard: an engine bug must not take down the notebook.
                tcs.SetResult(new EvaluationResult(Kernel.ExecutionCount, [new CellOutput(OutputKind.Error, Localization.Loc.Format("error.internal", ex.Message))], TimeSpan.Zero));
            }
            finally
            {
                Interlocked.CompareExchange(ref _current, null, cts);
                cts.Dispose();
            }
        });
        return tcs.Task;
    }

    public Task RunAsync(Action action)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            try { action(); tcs.SetResult(); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    /// <summary>Requests cancellation of the evaluation in progress (algorithms check periodically).</summary>
    public void Abort()
    {
        try { _current?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose() => _queue.CompleteAdding();
}
