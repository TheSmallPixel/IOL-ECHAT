namespace ECHAT.Server.Core.Services;

public class QuotaService
{
    private readonly int _maxTokens;
    private readonly TimeSpan _refillInterval;
    private readonly Dictionary<string, TokenBucket> _buckets = new();
    private readonly object _lock = new();

    public QuotaService(int maxTokens = 100, TimeSpan? refillInterval = null)
    {
        _maxTokens = maxTokens;
        _refillInterval = refillInterval ?? TimeSpan.FromSeconds(1);
    }

    public bool TryConsume(string key, int tokens = 1)
    {
        lock (_lock)
        {
            if (!_buckets.TryGetValue(key, out var bucket))
            {
                bucket = new TokenBucket(_maxTokens, _refillInterval);
                _buckets[key] = bucket;
            }

            return bucket.TryConsume(tokens);
        }
    }

    private class TokenBucket
    {
        private readonly int _maxTokens;
        private readonly TimeSpan _refillInterval;
        private double _tokens;
        private DateTime _lastRefill;

        public TokenBucket(int maxTokens, TimeSpan refillInterval)
        {
            _maxTokens = maxTokens;
            _refillInterval = refillInterval;
            _tokens = maxTokens;
            _lastRefill = DateTime.UtcNow;
        }

        public bool TryConsume(int tokens)
        {
            Refill();
            if (_tokens >= tokens)
            {
                _tokens -= tokens;
                return true;
            }
            return false;
        }

        private void Refill()
        {
            var now = DateTime.UtcNow;
            var elapsed = now - _lastRefill;
            var refillCount = elapsed / _refillInterval * _maxTokens;
            _tokens = Math.Min(_maxTokens, _tokens + refillCount);
            _lastRefill = now;
        }
    }
}
