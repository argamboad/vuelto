namespace Vuelto.Api.Tests.Infrastructure;

/// <summary>
/// The harness's concurrency runner (v4 T54, R7): starts every operation, lines them up behind one gate so they
/// genuinely overlap (not "the first one finished before the second was even created"), and returns their
/// results in order. Each operation opens its own context — two writers on one connection are not concurrent.
/// Replaces the per-test copies of the same TaskCompletionSource + WhenAll pattern.
/// </summary>
public static class Concurrently
{
    /// <summary>Runs <paramref name="racers"/> copies of <paramref name="op"/> (each told its index) at once.</summary>
    public static Task<T[]> RunAsync<T>(int racers, Func<int, Task<T>> op) =>
        RunAsync(Enumerable.Range(0, racers).Select(i => (Func<Task<T>>)(() => op(i))).ToArray());

    /// <summary>Runs the given operations at once; results come back in the order the operations were given.</summary>
    public static async Task<T[]> RunAsync<T>(params Func<Task<T>>[] ops)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = ops.Select(async op =>
        {
            await start.Task; // everyone waits here, so the work below overlaps
            return await op();
        }).ToArray();
        start.SetResult();
        return await Task.WhenAll(tasks);
    }

    /// <summary>Runs the given operations at once (no results).</summary>
    public static Task RunAsync(params Func<Task>[] ops) =>
        RunAsync(ops.Select(op => (Func<Task<bool>>)(async () => { await op(); return true; })).ToArray());
}
