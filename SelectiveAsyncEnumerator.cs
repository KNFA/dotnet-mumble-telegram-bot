using Grpc.Core;

namespace Knfa.TGM;

public static class SelectiveAsyncEnumeratorExtensions
{
    public static IAsyncEnumerator<T> ToAsyncEnumerator<T>(this IAsyncStreamReader<T> asyncStreamReader)
        where T : class
        => new IteratorProxy<T>(asyncStreamReader);

    private class IteratorProxy<T> : IAsyncEnumerator<T>
        where T : class
    {
        private readonly IAsyncStreamReader<T> _streamReader;

        public IteratorProxy(IAsyncStreamReader<T> streamReader)
        {
            _streamReader = streamReader;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public async ValueTask<bool> MoveNextAsync() => await _streamReader.MoveNext();

        public T Current => _streamReader.Current;
    }
}

public class SelectiveAsyncEnumerator<T1, T2> : IAsyncEnumerable<(T1?, T2?)>
    where T1 : class
    where T2 : class
{
    private readonly IAsyncEnumerator<T1> _t1Enumerator;
    private readonly IAsyncEnumerator<T2> _t2Enumerator;

    public SelectiveAsyncEnumerator(IAsyncEnumerator<T1> t1Enumerator, IAsyncEnumerator<T2> t2Enumerator)
    {
        _t1Enumerator = t1Enumerator;
        _t2Enumerator = t2Enumerator;
    }

    async IAsyncEnumerator<(T1?, T2?)> IAsyncEnumerable<(T1?, T2?)>.GetAsyncEnumerator(CancellationToken ct)
    {
        var dummyTask = new Task<bool>(() => false);
        var t1Task = _t1Enumerator.MoveNextAsync().AsTask();
        var t2Task = _t2Enumerator.MoveNextAsync().AsTask();

        while (!ct.IsCancellationRequested)
        {
            if (t1Task == dummyTask && t2Task == dummyTask)
            {
                yield break;
            }

            var resultedTask = await Task.WhenAny(t1Task, t2Task);

            if (resultedTask == t1Task)
            {
                if (t1Task.Result)
                {
                    yield return (_t1Enumerator.Current, default);
                    t1Task = _t1Enumerator.MoveNextAsync().AsTask();
                    continue;
                }

                t1Task = dummyTask;
            }

            if (resultedTask == t2Task)
            {
                if (t2Task.Result)
                {
                    yield return (default, _t2Enumerator.Current);
                    t2Task = _t2Enumerator.MoveNextAsync().AsTask();
                    continue;
                }

                t2Task = dummyTask;
            }
        }
    }
}
