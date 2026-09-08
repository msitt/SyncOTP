using System.Security.Cryptography;
using System.Text;

namespace SyncOTP.Core;

/// <summary>
/// Suppresses repeats. Two things cause them: a reconnect that replays messages we already saw
/// (same id), and the same SMS arriving twice through different paths or a Shortcut retry
/// (different id, identical text). The second case is only treated as a duplicate inside a short
/// window, because a service legitimately re-sending the same code minutes later is a new event.
/// </summary>
public sealed class MessageDeduper
{
    private readonly TimeSpan _window;
    private readonly int _capacity;
    private readonly Dictionary<string, DateTimeOffset> _seenIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _seenTexts = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public MessageDeduper(TimeSpan? window = null, int capacity = 200)
    {
        _window = window ?? TimeSpan.FromSeconds(90);
        _capacity = capacity;
    }

    /// <summary>True the first time a message is seen, false for a repeat.</summary>
    public bool IsNew(IncomingMessage message, DateTimeOffset? now = null)
    {
        var stamp = now ?? DateTimeOffset.UtcNow;

        lock (_gate)
        {
            Prune(stamp);

            if (!string.IsNullOrEmpty(message.Id) && _seenIds.ContainsKey(message.Id))
                return false;

            var textKey = HashText(message.Text);
            if (_seenTexts.TryGetValue(textKey, out var lastSeen) && stamp - lastSeen < _window)
            {
                // Refresh so a burst of retries stays collapsed.
                _seenTexts[textKey] = stamp;
                if (!string.IsNullOrEmpty(message.Id)) _seenIds[message.Id] = stamp;
                return false;
            }

            if (!string.IsNullOrEmpty(message.Id)) _seenIds[message.Id] = stamp;
            _seenTexts[textKey] = stamp;
            return true;
        }
    }

    private void Prune(DateTimeOffset now)
    {
        // Ids are kept far longer than the text window: ntfy can replay an old id after a long
        // outage, and re-copying a stale code would be worse than missing it.
        Trim(_seenIds, TimeSpan.FromHours(6));
        Trim(_seenTexts, _window);

        void Trim(Dictionary<string, DateTimeOffset> map, TimeSpan maxAge)
        {
            if (map.Count == 0) return;

            foreach (var key in map.Where(kv => now - kv.Value > maxAge).Select(kv => kv.Key).ToList())
                map.Remove(key);

            if (map.Count <= _capacity) return;

            foreach (var key in map.OrderBy(kv => kv.Value).Take(map.Count - _capacity).Select(kv => kv.Key).ToList())
                map.Remove(key);
        }
    }

    private static string HashText(string text)
    {
        var normalised = text.Trim().ToLowerInvariant();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalised));
        return Convert.ToHexString(bytes);
    }
}
