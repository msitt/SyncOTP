using SyncOTP.Core;
using Xunit;

namespace SyncOTP.Core.Tests;

public class CodeExtractorTests
{
    private readonly CodeExtractor _extractor = new(acceptLowConfidence: true);

    [Theory]
    // Real-world shapes, lightly edited. The expected value is the code as it should land on the
    // clipboard, so grouped and prefixed forms are normalised to bare digits.
    [InlineData("G-123456 is your Google verification code.", "123456")]
    [InlineData("Your Apple ID Code is: 907245. Do not share it with anyone.", "907245")]
    [InlineData("482913 is your Microsoft account verification code", "482913")]
    [InlineData("Your Amazon OTP is 471928. Do not share it with anyone.", "471928")]
    [InlineData("Chase: Your code is 738104. Never share it. Call 1-800-935-9935 with concerns.", "738104")]
    [InlineData("Your Uber code is 4821. Never share this code.", "4821")]
    [InlineData("Use 195 620 as your verification code.", "195620")]
    [InlineData("Your verification code is 123-456", "123456")]
    [InlineData("PayPal: Your security code is 284617. Your code expires in 10 minutes.", "284617")]
    [InlineData("Your one-time passcode is 55291.", "55291")]
    [InlineData("【淘宝】验证码123456，请勿泄露", "123456")]
    [InlineData("Your login code for Discord is 913055", "913055")]
    [InlineData("Enter 66218 to confirm your phone number.", "66218")]
    [InlineData("Your Steam code is K4T2M", "K4T2M")]
    [InlineData("Verification code: 2947156", "2947156")]
    [InlineData("Your security code is 8471. It expires in 5 minutes. Reply STOP to 22395 to opt out.", "8471")]
    public void ExtractsTheCode(string message, string expected)
    {
        var result = _extractor.Extract(message);

        Assert.NotNull(result);
        Assert.Equal(expected, result!.Code);
    }

    [Theory]
    // Nothing here is a one-time code, so touching the clipboard would be wrong.
    [InlineData("Your package arrives today between 3:45 PM and 5:15 PM.")]
    [InlineData("Your order #48213 has shipped. Track it at https://example.com/t/99182")]
    [InlineData("Reminder: your appointment is on 03/15 at 2:30 PM. Reply STOP to cancel.")]
    [InlineData("Hey, can you call me back at 555-0142 when you get a chance?")]
    [InlineData("Your payment of $1,284.50 to Acme Corp was processed.")]
    [InlineData("Happy birthday! Hope you have a great day.")]
    [InlineData("Your balance is low. Transfer funds at www.bank.example to avoid a fee.")]
    public void IgnoresMessagesWithoutCodes(string message)
    {
        Assert.Null(_extractor.Extract(message));
    }

    [Fact]
    public void PrefersTheCodeOverASurroundingPhoneNumber()
    {
        var result = _extractor.Extract(
            "Wells Fargo: 620914 is your verification code. Questions? Call 1-800-869-3557.");

        Assert.NotNull(result);
        Assert.Equal("620914", result!.Code);
    }

    [Fact]
    public void PrefersTheCodeOverAnExpiryDuration()
    {
        var result = _extractor.Extract("Your code is 338291 and it expires in 15 minutes.");

        Assert.NotNull(result);
        Assert.Equal("338291", result!.Code);
    }

    [Fact]
    public void GoogleFormatIsHighConfidence()
    {
        var result = _extractor.Extract("G-778341 is your Google verification code.");

        Assert.NotNull(result);
        Assert.Equal(CodeConfidence.High, result!.Confidence);
    }

    [Fact]
    public void AdjacentPhraseIsHighConfidence()
    {
        var result = _extractor.Extract("Your code is 447120.");

        Assert.NotNull(result);
        Assert.Equal(CodeConfidence.High, result!.Confidence);
    }

    [Fact]
    public void LoneNumberInAShortMessageIsLowConfidence()
    {
        var result = _extractor.Extract("884213");

        Assert.NotNull(result);
        Assert.Equal(CodeConfidence.Low, result!.Confidence);
    }

    [Fact]
    public void LoneNumberIsRejectedWhenLowConfidenceIsDisabled()
    {
        var strict = new CodeExtractor(acceptLowConfidence: false);

        Assert.Null(strict.Extract("884213"));
    }

    [Fact]
    public void AlphanumericCandidateNeedsAKeyword()
    {
        // No verification keyword anywhere, so "AB12CD" must not be treated as a code.
        Assert.Null(_extractor.Extract("Meet me at gate AB12CD in the morning"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void HandlesEmptyInput(string? message)
    {
        Assert.Null(_extractor.Extract(message));
    }

    [Fact]
    public void HandlesMultilineMessages()
    {
        var result = _extractor.Extract("Instagram\n\nYour code is 552108.\n\nDo not share it.");

        Assert.NotNull(result);
        Assert.Equal("552108", result!.Code);
    }
}
