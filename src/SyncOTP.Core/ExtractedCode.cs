namespace SyncOTP.Core;

public enum CodeConfidence
{
    /// <summary>No verification keyword; a single plausible number in a short message.</summary>
    Low,

    /// <summary>A verification keyword appears near the candidate.</summary>
    Medium,

    /// <summary>A strong phrase ("your code is", "G-123456") sits right next to the candidate.</summary>
    High,
}

/// <param name="Code">The code itself, punctuation stripped (e.g. "123456" for "123-456").</param>
/// <param name="Confidence">How sure the extractor is.</param>
/// <param name="Reason">Short human-readable explanation, for the log and tray tooltip.</param>
public sealed record ExtractedCode(string Code, CodeConfidence Confidence, string Reason);
