using System.Net;
using System.Text.Json;
using XrShift.Scene;

namespace XrShift.Scene.Tests;

public class BotReportGateTests
{
    const string Secret = "correct horse battery";
    const string Report = """{"jsonrpc":"2.0","id":7,"method":"bot.report","params":{"id":"s1","event":"UserPromptSubmit","note":"fix the login page","pc":"DESK","pid":4242}}""";
    static readonly DateTimeOffset Now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ASealedReportGetsInWithoutAProcessNumberOrRequestId()
    {
        string? passed = new BotReportGate(Secret).Accepted(BotReportGate.Seal(Report, Secret, Now), Now);
        JsonElement request = JsonDocument.Parse(passed!).RootElement;
        JsonElement p = request.GetProperty("params");
        Assert.Equal(("s1", "DESK", "fix the login page"), (p.GetProperty("id").GetString(), p.GetProperty("pc").GetString(), p.GetProperty("note").GetString()));
        Assert.False(p.TryGetProperty("pid", out _));
        Assert.False(request.TryGetProperty("id", out _));
    }

    [Fact]
    public void TheSealedLineShowsNeitherTheSecretNorTheNote()
    {
        string line = BotReportGate.Seal(Report, Secret, Now);
        Assert.StartsWith(BotReportGate.Version, line);
        Assert.DoesNotContain("correct horse", line);
        Assert.DoesNotContain("login", line);
        Assert.DoesNotContain('\n', line);
        Assert.NotEqual(line, BotReportGate.Seal(Report, Secret, Now)); // a fresh nonce each time
    }

    [Fact]
    public void AReportSealedWithAnotherSecretIsTurnedAway() =>
        Assert.Null(new BotReportGate(Secret).Accepted(BotReportGate.Seal(Report, "correct horse batterz", Now), Now));

    [Fact]
    public void AnyChangedByteIsTurnedAway()
    {
        string line = BotReportGate.Seal(Report, Secret, Now);
        byte[] blob = Convert.FromBase64String(line[BotReportGate.Version.Length..]);
        for (int i = 0; i < blob.Length; i++)
        {
            byte[] changed = (byte[])blob.Clone();
            changed[i] ^= 1;
            Assert.Null(new BotReportGate(Secret).Accepted(BotReportGate.Version + Convert.ToBase64String(changed), Now));
        }
    }

    [Fact]
    public void ALineSentAgainIsTurnedAway()
    {
        var gate = new BotReportGate(Secret);
        string line = BotReportGate.Seal(Report, Secret, Now);
        Assert.NotNull(gate.Accepted(line, Now));
        Assert.Null(gate.Accepted(line, Now.AddSeconds(5)));
        Assert.NotNull(gate.Accepted(BotReportGate.Seal(Report, Secret, Now), Now.AddSeconds(5)));
    }

    [Theory]
    [InlineData(-121, false)]
    [InlineData(-119, true)]
    [InlineData(119, true)]
    [InlineData(121, false)]
    public void OnlyALineSentWithinTwoMinutesOfNowGetsIn(int secondsFromNow, bool allowed) =>
        Assert.Equal(allowed, new BotReportGate(Secret).Accepted(BotReportGate.Seal(Report, Secret, Now.AddSeconds(secondsFromNow)), Now) is not null);

    [Theory]
    [InlineData("""{"method":"bot.report","params":{"id":"s1","event":"Stop"}}""")]
    [InlineData("""{"method":"bot.report","params":{"id":"s1","event":"Stop","pc":""}}""")]
    [InlineData("""{"method":"bot.report","params":{"id":"s1","event":"Stop","pc":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"}}""")]
    [InlineData("""{"method":"bot.tell","params":{"id":"s1","text":"rm -rf","pc":"DESK"}}""")]
    [InlineData("""{"method":"voice.say","params":{"text":"new bot in abel","pc":"DESK"}}""")]
    [InlineData("""{"method":"scene.list","params":{"pc":"DESK"}}""")]
    [InlineData("""{"method":"bot.report","pc":"DESK"}""")]
    [InlineData("""["bot.report"]""")]
    [InlineData("""not json""")]
    public void ASealedRequestThatIsNotABotReportWithAPcNameIsStillTurnedAway(string request) =>
        Assert.Null(new BotReportGate(Secret).Accepted(BotReportGate.Seal(request, Secret, Now), Now));

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","method":"bot.report","params":{"id":"s1","event":"Stop","pc":"DESK","secret":"correct horse battery"}}""")]
    [InlineData("xr1:")]
    [InlineData("xr1:not base64!")]
    [InlineData("xr2:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("")]
    public void OldPlainLinesAndOtherVersionsAreTurnedAway(string line) => Assert.Null(new BotReportGate(Secret).Accepted(line, Now));

    [Fact]
    public void AnotherVersionPrefixOnAGoodSealIsTurnedAway() =>
        Assert.Null(new BotReportGate(Secret).Accepted("xr2:" + BotReportGate.Seal(Report, Secret, Now)[BotReportGate.Version.Length..], Now));

    [Theory]
    [InlineData("192.168.1.20", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.16.4.4", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("169.254.2.1", true)]
    [InlineData("100.84.113.47", true)]
    [InlineData("100.128.0.1", false)]
    [InlineData("127.0.0.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("::ffff:192.168.1.20", true)]
    [InlineData("::ffff:8.8.8.8", false)]
    [InlineData("fe80::1", true)]
    [InlineData("2001:4860:4860::8888", false)]
    public void OnlyPrivateAddressesMayConnect(string address, bool allowed) =>
        Assert.Equal(allowed, BotReportGate.IsPrivate(IPAddress.Parse(address)));
}
