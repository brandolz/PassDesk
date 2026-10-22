namespace PassDesk.Browser.Broker;

/// <summary>Limitador de ventana deslizante: como mucho <c>limit</c> usos en cada <c>window</c>.</summary>
internal sealed class SlidingWindowLimiter(int limit, TimeSpan window, TimeProvider time)
{
    private readonly Queue<long> _stamps = new();
    private readonly Lock _gate = new();

    public bool TryAcquire()
    {
        long now = time.GetTimestamp();
        lock (_gate)
        {
            while (_stamps.Count > 0 && time.GetElapsedTime(_stamps.Peek(), now) >= window)
            {
                _stamps.Dequeue();
            }

            if (_stamps.Count >= limit)
            {
                return false;
            }

            _stamps.Enqueue(now);
            return true;
        }
    }
}
