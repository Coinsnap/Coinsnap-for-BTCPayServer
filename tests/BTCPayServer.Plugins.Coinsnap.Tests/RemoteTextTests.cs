using BTCPayServer.Lightning;
using Network = NBitcoin.Network;

namespace BTCPayServer.Plugins.Coinsnap.Tests;

/// <summary>Audit finding 7: server text must not forge log lines or flood the admin UI.</summary>
public class RemoteTextTests
{
    [Fact]
    public void ControlCharactersAreReplacedAndLengthIsBounded()
    {
        var cleaned = CoinsnapLnurlService.CleanRemoteText("bad\r\nfail: forged line\u0007" + new string('x', 1000));

        Assert.DoesNotContain('\r', cleaned);
        Assert.DoesNotContain('\n', cleaned);
        Assert.DoesNotContain('\u0007', cleaned);
        Assert.True(cleaned!.Length <= 201);
        Assert.EndsWith("…", cleaned);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyTextFallsBackToTheDefaultMessage(string? text)
    {
        Assert.Null(CoinsnapLnurlService.CleanRemoteText(text));
    }

    [Fact]
    public async Task ACallbackErrorReasonIsCleanedBeforeItBecomesTheExceptionMessage()
    {
        var clock = new TestClock();
        var handler = new ScriptedHttpHandler();
        handler.EnqueueJson(TestData.Metadata());
        handler.EnqueueJson("{\"status\":\"ERROR\",\"reason\":\"line one\\r\\nline two\"}");
        var (service, _) = await TestData.Service(handler, new FakeBolt11Parser(), clock);

        var error = await Assert.ThrowsAsync<FormatException>(() => service.CreateInvoiceAsync(
            CoinsnapLightningAddress.Parse("yourname@coinsnap.app"), "store:a", "a", Network.Main,
            LightMoney.MilliSatoshis(25_000), TimeSpan.FromMinutes(15), null, CancellationToken.None));

        Assert.Equal("line one  line two", error.Message);
    }
}
