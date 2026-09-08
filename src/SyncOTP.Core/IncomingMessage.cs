using System.Text.RegularExpressions;

namespace SyncOTP.Core;

/// <summary>A text message forwarded from the phone, normalised by a message source.</summary>
/// <param name="Id">Source-assigned unique id (ntfy message id). Used for dedupe and for resuming.</param>
/// <param name="Text">The full body of the SMS.</param>
/// <param name="From">Sender, if the phone was able to supply it. May be null.</param>
/// <param name="SentAt">When the source says the message was published.</param>
/// <param name="SourceName">Which <c>IMessageSource</c> produced this, for logging.</param>
public sealed partial record IncomingMessage(
    string Id,
    string Text,
    string? From,
    DateTimeOffset SentAt,
    string SourceName)
{
    /// <summary>How old the message was by the time we saw it.</summary>
    public TimeSpan Age => DateTimeOffset.UtcNow - SentAt;

    /// <summary>
    /// A single-line, length-capped version of <see cref="Text"/>. The sender an SMS gateway
    /// reports is usually an anonymous five-digit short code, so the body is the only thing that
    /// says which service the code is actually for; this is what goes in the log and the toast.
    /// </summary>
    public string Snippet(int maxChars) => Summarise(Text, maxChars);

    /// <summary>Collapses whitespace and truncates on a word boundary where one is close enough.</summary>
    public static string Summarise(string? text, int maxChars)
    {
        if (maxChars <= 0 || string.IsNullOrWhiteSpace(text)) return "";

        var flat = WhitespaceRegex().Replace(text, " ").Trim();
        if (flat.Length <= maxChars) return flat;

        var cut = flat[..maxChars];
        var lastSpace = cut.LastIndexOf(' ');

        // Only back up to the word boundary when doing so does not throw most of the line away.
        if (lastSpace > maxChars / 2) cut = cut[..lastSpace];

        return cut.TrimEnd() + "…";
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
