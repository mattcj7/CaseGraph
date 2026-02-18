using CaseGraph.Core.Models;
using CaseGraph.Core.Diagnostics;

namespace CaseGraph.Infrastructure.Services;

internal sealed class JobInfoObservable : IObservable<JobInfo>
{
    private readonly object _sync = new();
    private readonly List<IObserver<JobInfo>> _observers = new();

    public IDisposable Subscribe(IObserver<JobInfo> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);

        lock (_sync)
        {
            _observers.Add(observer);
        }

        return new Subscription(this, observer);
    }

    public void Publish(JobInfo info)
    {
        IObserver<JobInfo>[] observers;
        lock (_sync)
        {
            observers = _observers.ToArray();
        }

        foreach (var observer in observers)
        {
            try
            {
                observer.OnNext(info);
            }
            catch (Exception ex)
            {
                AppFileLogger.LogException(
                    "[JobQueue] Observer OnNext failure contained.",
                    ex
                );
            }
        }
    }

    private void Unsubscribe(IObserver<JobInfo> observer)
    {
        lock (_sync)
        {
            _observers.Remove(observer);
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly JobInfoObservable _owner;
        private IObserver<JobInfo>? _observer;

        public Subscription(JobInfoObservable owner, IObserver<JobInfo> observer)
        {
            _owner = owner;
            _observer = observer;
        }

        public void Dispose()
        {
            var observer = Interlocked.Exchange(ref _observer, null);
            if (observer is null)
            {
                return;
            }

            _owner.Unsubscribe(observer);
        }
    }
}
