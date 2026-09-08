namespace SyncOTP.App.Output;

public sealed record CodeEntry(string Code, string? From, DateTimeOffset ReceivedAt)
{
    public string MenuLabel
    {
        get
        {
            var age = DateTimeOffset.Now - ReceivedAt;
            var when = age < TimeSpan.FromMinutes(1)
                ? "just now"
                : age < TimeSpan.FromHours(1)
                    ? $"{age.TotalMinutes:0}m ago"
                    : ReceivedAt.ToLocalTime().ToString("HH:mm");

            return From is null ? $"{Code}  ({when})" : $"{Code}  {From}, {when}";
        }
    }
}

/// <summary>The last few codes, so one that was overwritten on the clipboard can be recovered.</summary>
public sealed class CodeHistory
{
    private readonly int _capacity;
    private readonly LinkedList<CodeEntry> _entries = new();
    private readonly object _gate = new();

    public CodeHistory(int capacity = 5) => _capacity = capacity;

    public void Add(CodeEntry entry)
    {
        lock (_gate)
        {
            _entries.AddFirst(entry);
            while (_entries.Count > _capacity) _entries.RemoveLast();
        }
    }

    public IReadOnlyList<CodeEntry> Recent()
    {
        lock (_gate) return _entries.ToList();
    }

    public CodeEntry? Latest()
    {
        lock (_gate) return _entries.First?.Value;
    }
}
