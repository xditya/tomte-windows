using System.Collections.Concurrent;

namespace Tomte.AI.Automation;

/// <summary>
/// The one thread a palette's UI Automation calls run on. They are cross-process COM calls that block while the
/// other app is busy or hung, so each one has a time limit, and none runs on the island's or the input thread. A call
/// still waiting in the queue when its caller gave up never runs, so a click the model was told had timed out cannot
/// happen later. The thread starts with the first call and ends when the palette disposes it; <paramref name="atEnd"/>
/// runs on the thread as it ends, so the COM objects are released where they were made.
/// </summary>
internal sealed class UiaWorker : IDisposable
{
    private readonly BlockingCollection<Action> _queue = [];

    public UiaWorker(Action atEnd)
    {
        var thread = new Thread(() =>
        {
            foreach (var work in _queue.GetConsumingEnumerable())
                work();
            atEnd();
        })
        { IsBackground = true, Name = "tomte-uia" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    /// <summary>Runs <paramref name="call"/> on the thread; a <see cref="TimeoutException"/> when it has not finished within <paramref name="limit"/>.</summary>
    public async Task<T> RunAsync<T>(Func<T> call, TimeSpan limit, CancellationToken ct)
    {
        var state = new int[1]; // 0 waiting, 1 started, 2 given up
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            if (Interlocked.CompareExchange(ref state[0], 1, 0) != 0)
                return;
            try
            {
                done.SetResult(call());
            }
            catch (Exception e)
            {
                done.SetException(e);
            }
        }, ct);
        try
        {
            return await done.Task.WaitAsync(limit, ct);
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException && !done.Task.IsCompleted)
        {
            bool started = Interlocked.CompareExchange(ref state[0], 2, 0) != 0;
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException(started
                ? $"UI Automation did not answer within {limit.TotalSeconds:0.#} s; the other app may be busy. The call had started, so an action may still take effect."
                : $"UI Automation was still busy with an earlier call after {limit.TotalSeconds:0.#} s, so nothing was done.");
        }
    }

    /// <summary>Lets the thread end after the call it is in, without waiting for it.</summary>
    public void Dispose() => _queue.CompleteAdding();
}
