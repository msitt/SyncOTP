using SyncOTP.Core;
using Xunit;

namespace SyncOTP.Core.Tests;

public class IncomingMessageTests
{
    [Fact]
    public void Summarise_collapses_whitespace_and_newlines()
    {
        Assert.Equal("Your Acme code is 123456",
            IncomingMessage.Summarise("Your Acme\r\n  code is\t123456 ", 120));
    }

    [Fact]
    public void Summarise_keeps_short_text_intact()
    {
        Assert.Equal("G-123456", IncomingMessage.Summarise("G-123456", 120));
    }

    [Fact]
    public void Summarise_truncates_on_a_word_boundary()
    {
        Assert.Equal("Your Acme verification…",
            IncomingMessage.Summarise("Your Acme verification code is 123456", 25));
    }

    [Fact]
    public void Summarise_still_truncates_when_there_is_no_word_boundary()
    {
        Assert.Equal("aaaaa…", IncomingMessage.Summarise(new string('a', 50), 5));
    }

    [Fact]
    public void Summarise_returns_empty_when_disabled_or_blank()
    {
        Assert.Equal("", IncomingMessage.Summarise("Your code is 123456", 0));
        Assert.Equal("", IncomingMessage.Summarise("   ", 120));
        Assert.Equal("", IncomingMessage.Summarise(null, 120));
    }

    [Fact]
    public void Snippet_uses_the_message_body()
    {
        var message = new IncomingMessage("1", "  Acme:   123456  ", "12345", DateTimeOffset.UtcNow, "ntfy");

        Assert.Equal("Acme: 123456", message.Snippet(120));
    }
}
