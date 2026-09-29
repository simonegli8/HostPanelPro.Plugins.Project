using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

public static class TaskExtensions
{
#if NETSTANDARD
    public static async IAsyncEnumerable<Task<T>> WhenEach<T>(
        IEnumerable<Task<T>> tasks,
        [EnumeratorCancellation] CancellationToken cancel = default)
    {
        var input = tasks.ToList();

        // One slot per task. Slots are filled in the order the tasks complete.
        var slots = new TaskCompletionSource<Task<T>>[input.Count];
        for (var i = 0; i < slots.Length; i++)
            slots[i] = new TaskCompletionSource<Task<T>>(TaskCreationOptions.RunContinuationsAsynchronously);

        var next = -1;
        foreach (var task in input)
        {
            _ = task.ContinueWith(
                t => slots[Interlocked.Increment(ref next)].TrySetResult(t),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        using (cancel.Register(() => { foreach (var s in slots) s.TrySetCanceled(cancel); }))
        {
            foreach (var slot in slots)
                yield return await slot.Task.ConfigureAwait(false);
        }
    }
#else
    public static async IAsyncEnumerable<Task<T>> WhenEach<T>(
        IEnumerable<Task<T>> tasks,
        [EnumeratorCancellation] CancellationToken cancel = default)
    {
        await foreach (var task in Task.WhenEach<T>(tasks)) yield return task;
    }
#endif
}