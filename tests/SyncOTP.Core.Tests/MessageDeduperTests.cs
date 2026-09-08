using SyncOTP.Core;
using Xunit;

namespace SyncOTP.Core.Tests;

public class MessageDeduperTests
{
    private static IncomingMessage Message(string id, string text, DateTimeOffset? sentAt = null) =>
        new(id, text, "Chase", sentAt ?? DateTimeOffset.UtcNow, "test");

    [Fact]
    public void FirstSightOfAMessageIsNew()
    {
        var deduper = new MessageDeduper();

        Assert.True(deduper.IsNew(Message("a1", "Your code is 123456")));
    }

    [Fact]
    public void SameIdIsSuppressed()
    {
        var deduper = new MessageDeduper();
        var message = Message("a1", "Your code is 123456");

        Assert.True(deduper.IsNew(message));
        Assert.False(deduper.IsNew(message));
    }

    [Fact]
    public void SameTextWithADifferentIdIsSuppressedInsideTheWindow()
    {
        // A Shortcut retry or a second delivery path produces a fresh id for identical text.
        var deduper = new MessageDeduper(TimeSpan.FromSeconds(90));
        var now = DateTimeOffset.UtcNow;

        Assert.True(deduper.IsNew(Message("a1", "Your code is 123456"), now));
        Assert.False(deduper.IsNew(Message("b2", "Your code is 123456"), now.AddSeconds(5)));
    }

    [Fact]
    public void SameTextAfterTheWindowIsANewMessage()
    {
        // Re-requesting a code often yields the identical body; after the window it is a real event.
        var deduper = new MessageDeduper(TimeSpan.FromSeconds(90));
        var now = DateTimeOffset.UtcNow;

        Assert.True(deduper.IsNew(Message("a1", "Your code is 123456"), now));
        Assert.True(deduper.IsNew(Message("b2", "Your code is 123456"), now.AddSeconds(120)));
    }

    [Fact]
    public void TextComparisonIgnoresCaseAndSurroundingWhitespace()
    {
        var deduper = new MessageDeduper();
        var now = DateTimeOffset.UtcNow;

        Assert.True(deduper.IsNew(Message("a1", "Your Code Is 123456"), now));
        Assert.False(deduper.IsNew(Message("b2", "  your code is 123456  "), now.AddSeconds(1)));
    }

    [Fact]
    public void DifferentMessagesBothPass()
    {
        var deduper = new MessageDeduper();
        var now = DateTimeOffset.UtcNow;

        Assert.True(deduper.IsNew(Message("a1", "Your code is 111111"), now));
        Assert.True(deduper.IsNew(Message("a2", "Your code is 222222"), now.AddSeconds(1)));
    }

    [Fact]
    public void OldIdsAreStillRemembered()
    {
        // ntfy can replay a cached message long after the fact; re-copying it would be wrong.
        var deduper = new MessageDeduper(TimeSpan.FromSeconds(90));
        var now = DateTimeOffset.UtcNow;

        Assert.True(deduper.IsNew(Message("a1", "Your code is 123456"), now));
        Assert.False(deduper.IsNew(Message("a1", "Your code is 123456"), now.AddHours(1)));
    }
}
