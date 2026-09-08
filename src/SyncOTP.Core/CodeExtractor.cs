using System.Text;
using System.Text.RegularExpressions;

namespace SyncOTP.Core;

/// <summary>
/// Pulls a one-time code out of an SMS body. Deterministic and dependency-free so it can be
/// exercised by a large unit-test corpus.
///
/// Strategy: mask the spans that are definitely not codes (phone numbers, prices, times, order
/// numbers, URLs), collect candidates from the surviving text, then score them by pattern quality
/// and by how close a verification keyword sits.
/// </summary>
public sealed partial class CodeExtractor
{
    private const int KeywordProximity = 30;
    private const int ShortMessageChars = 200;
    private const char Mask = '·';

    private readonly bool _acceptLowConfidence;

    public CodeExtractor(bool acceptLowConfidence = true) => _acceptLowConfidence = acceptLowConfidence;

    public CodeExtractor(ExtractorConfig config) : this(config.AcceptLowConfidence) { }

    // ---- keywords -------------------------------------------------------------------------

    [GeneratedRegex(
        @"code|passcode|password|otp|verification|verify|verifying|pin\b|token|2fa|one[\s-]?time|security|authenticat|login|log in|sign[\s-]?in|confirm|access|验证码|驗證碼|認証|인증",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeywordRegex();

    /// <summary>Phrases that all but guarantee the adjacent token is the code.</summary>
    [GeneratedRegex(
        @"(code|otp|pin|password|passcode|验证码|驗證碼)\s*(is|:|=)|is your\s|use\s|enter\s|输入",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StrongPhraseRegex();

    // ---- exclusions -----------------------------------------------------------------------

    [GeneratedRegex(@"https?://\S+|www\.\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();

    /// <summary>Phone-number-shaped runs. Verified afterwards to hold at least 9 digits.</summary>
    [GeneratedRegex(@"\+?\d[\d\s().-]{7,}\d", RegexOptions.CultureInvariant)]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"[$€£¥]\s?\d[\d,]*(\.\d+)?|\d[\d,]*(\.\d+)?\s?(USD|EUR|GBP|CAD|dollars?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MoneyRegex();

    [GeneratedRegex(@"\d{1,2}:\d{2}(:\d{2})?(\s?[ap]\.?m\.?)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TimeRegex();

    [GeneratedRegex(@"\b\d{1,4}\s?(minutes?|mins?|seconds?|secs?|hours?|hrs?|days?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DurationRegex();

    [GeneratedRegex(@"\b\d{1,4}[/-]\d{1,2}([/-]\d{2,4})?\b", RegexOptions.CultureInvariant)]
    private static partial Regex DateRegex();

    /// <summary>Order / reference / account numbers: "#48213", "order 48213", "ending in 4821".</summary>
    [GeneratedRegex(@"#\s?\d+|\b(order|ref|reference|invoice|ticket|acct|account|case|tracking)\s*#?\s*\d+|ending in\s*\d+|x{2,}\d+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReferenceRegex();

    /// <summary>Carrier boilerplate: "Reply STOP to 12345".</summary>
    [GeneratedRegex(@"(reply|text|send)\s+(stop|help|start|end|quit|unsubscribe)(\s+to\s+\d+)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CarrierBoilerplateRegex();

    // ---- candidates -----------------------------------------------------------------------

    // Digit-run boundaries use lookarounds rather than \b so that codes butted straight up
    // against CJK text (for example 验证码123456) still match: CJK characters are word
    // characters, so \b would not fire between them and a digit.
    [GeneratedRegex(@"(?<![0-9])G-(\d{4,8})(?![0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GoogleRegex();

    [GeneratedRegex(@"(?<![0-9])(\d{3})[-\s](\d{3})(?![0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex SplitDigitsRegex();

    [GeneratedRegex(@"(?<![0-9])\d{4,8}(?![0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex DigitsRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?=[A-Z0-9]{5,8}(?![A-Za-z0-9]))[A-Z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex AlnumRegex();

    /// <summary>Uppercase words that look like alphanumeric codes but never are.</summary>
    private static readonly HashSet<string> AlnumStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "PLEASE", "REPLY", "THANKS", "AMAZON", "GOOGLE", "APPLE", "PAYPAL", "VENMO", "CHASE",
        "STOP", "START", "HELP", "UNSUB", "ALERT", "FRAUD", "URGENT", "NOTICE", "EXPIRE",
        "SECURITY", "ACCOUNT", "SUPPORT", "DELIVERY", "SHIPPED", "ORDER", "VERIFY", "LOGIN",
    };

    private sealed record Candidate(string Code, int Start, int Length, int PatternScore, string Source)
    {
        public int End => Start + Length;
    }

    /// <summary>Returns the best code in <paramref name="text"/>, or null if there is not one.</summary>
    public ExtractedCode? Extract(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var normalised = Regex.Replace(text, @"\s+", " ").Trim();
        var masked = MaskExclusions(normalised);

        var candidates = CollectCandidates(masked);
        if (candidates.Count == 0) return null;

        var hasKeyword = KeywordRegex().IsMatch(normalised);

        ExtractedCode? best = null;
        var bestScore = int.MinValue;

        foreach (var candidate in candidates)
        {
            var keywordDistance = DistanceToNearest(KeywordRegex(), normalised, candidate);
            var strongDistance = DistanceToNearest(StrongPhraseRegex(), normalised, candidate);

            var nearKeyword = keywordDistance is >= 0 and <= KeywordProximity;
            var nearStrong = strongDistance is >= 0 and <= KeywordProximity;

            // An alphanumeric candidate is too weak to stand on its own; it needs a keyword nearby.
            if (candidate.Source == "alnum" && !nearKeyword) continue;

            var score = candidate.PatternScore
                        + (nearKeyword ? 40 : 0)
                        + (nearStrong ? 30 : 0)
                        + LengthScore(candidate.Code.Length);

            CodeConfidence confidence;
            string reason;

            if (candidate.Source == "google")
            {
                confidence = CodeConfidence.High;
                reason = "Google G- code format";
            }
            else if (nearStrong)
            {
                confidence = CodeConfidence.High;
                reason = "verification phrase adjacent";
            }
            else if (nearKeyword)
            {
                confidence = CodeConfidence.Medium;
                reason = "verification keyword nearby";
            }
            else if (hasKeyword)
            {
                confidence = CodeConfidence.Low;
                reason = "verification keyword elsewhere in the message";
            }
            else
            {
                // No keyword anywhere. Only trust this if the message is short and the number is
                // the single plausible candidate in it.
                if (!_acceptLowConfidence) continue;
                if (normalised.Length > ShortMessageChars) continue;
                if (candidates.Count != 1) continue;
                if (candidate.Code.Length is < 5 or > 8) continue;

                confidence = CodeConfidence.Low;
                reason = "lone number in a short message";
                score -= 20;
            }

            if (score <= bestScore) continue;

            bestScore = score;
            best = new ExtractedCode(candidate.Code, confidence, reason);
        }

        return best;
    }

    /// <summary>
    /// Blanks out spans that are never codes, preserving length so offsets stay comparable to the
    /// original string.
    /// </summary>
    private static string MaskExclusions(string text)
    {
        var buffer = new StringBuilder(text);

        MaskAll(UrlRegex());
        MaskAll(MoneyRegex());
        MaskAll(TimeRegex());
        MaskAll(DurationRegex());
        MaskAll(DateRegex());
        MaskAll(ReferenceRegex());
        MaskAll(CarrierBoilerplateRegex());

        // Phone numbers last, and only when the run really holds enough digits: the pattern is
        // loose enough to swallow a grouped code such as "123 456" otherwise.
        foreach (Match m in PhoneRegex().Matches(buffer.ToString()))
        {
            var digits = m.Value.Count(char.IsAsciiDigit);
            if (digits >= 9) Blank(m.Index, m.Length);
        }

        return buffer.ToString();

        void MaskAll(Regex regex)
        {
            foreach (Match m in regex.Matches(buffer.ToString()))
                Blank(m.Index, m.Length);
        }

        void Blank(int start, int length)
        {
            for (var i = start; i < start + length && i < buffer.Length; i++)
                buffer[i] = Mask;
        }
    }

    private static List<Candidate> CollectCandidates(string masked)
    {
        var found = new List<Candidate>();
        var claimed = new List<(int Start, int End)>();

        foreach (Match m in GoogleRegex().Matches(masked))
            Add(new Candidate(m.Groups[1].Value, m.Index, m.Length, 100, "google"));

        foreach (Match m in SplitDigitsRegex().Matches(masked))
            Add(new Candidate(m.Groups[1].Value + m.Groups[2].Value, m.Index, m.Length, 70, "split"));

        foreach (Match m in DigitsRegex().Matches(masked))
            Add(new Candidate(m.Value, m.Index, m.Length, 50, "digits"));

        foreach (Match m in AlnumRegex().Matches(masked))
        {
            var value = m.Value;

            // Needs both a digit and a letter, or it is just a shouted word or a plain number.
            if (!value.Any(char.IsAsciiDigit)) continue;
            if (!value.Any(char.IsAsciiLetterUpper)) continue;
            if (AlnumStopWords.Contains(value)) continue;

            Add(new Candidate(value, m.Index, m.Length, 40, "alnum"));
        }

        return found;

        // Higher-priority patterns run first and claim their span, so "G-123456" is not also
        // reported as the bare digits "123456".
        void Add(Candidate candidate)
        {
            if (claimed.Any(c => candidate.Start < c.End && c.Start < candidate.End)) return;
            claimed.Add((candidate.Start, candidate.End));
            found.Add(candidate);
        }
    }

    /// <summary>Characters between the candidate and the closest match of the regex, or -1 for none.</summary>
    private static int DistanceToNearest(Regex regex, string text, Candidate candidate)
    {
        var nearest = -1;

        foreach (Match m in regex.Matches(text))
        {
            // Ignore a keyword that overlaps the candidate itself.
            if (m.Index < candidate.End && candidate.Start < m.Index + m.Length) continue;

            var distance = m.Index >= candidate.End
                ? m.Index - candidate.End
                : candidate.Start - (m.Index + m.Length);

            if (distance < 0) distance = 0;
            if (nearest < 0 || distance < nearest) nearest = distance;
        }

        return nearest;
    }

    /// <summary>Six digits is the overwhelmingly common length; four is the weakest.</summary>
    private static int LengthScore(int length) => length switch
    {
        6 => 25,
        5 => 15,
        7 => 12,
        8 => 10,
        4 => 4,
        _ => 0,
    };
}
