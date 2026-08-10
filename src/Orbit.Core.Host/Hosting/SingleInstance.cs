namespace Orbit.Core.Host.Hosting;

/// <summary>
/// Ensures only one Orbit Core Host process runs per user session.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    public const string MutexName = "Local\\Orbit.Core.Host";

    private readonly Mutex _mutex;
    private bool _owned;

    private SingleInstance(Mutex mutex, bool owned)
    {
        _mutex = mutex;
        _owned = owned;
    }

    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return null;
        }

        return new SingleInstance(mutex, owned: true);
    }

    public void Dispose()
    {
        if (_owned)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // already released
            }

            _owned = false;
        }

        _mutex.Dispose();
    }
}
