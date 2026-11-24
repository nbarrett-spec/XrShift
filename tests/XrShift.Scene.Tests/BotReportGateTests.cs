using System.Net;
using System.Text.Json;
using XrShift.Scene;

namespace XrShift.Scene.Tests;

public class BotReportGateTests
{
    const string Secret = "correct horse battery";

    [Fact]
    public void AReportWithTheSecretGetsInWithoutItAndWithoutAProcessNumber()
    {
        string? passed = BotReportGate.Accepted("""{"jsonrpc":"2.0","id":7,"method":"bot.report","params":{"id":"s1","event":"Stop","pc":"DESK","pid":4242,"secret":"correct horse battery"}}""", Secret);
        JsonElement p = JsonDocument.Parse(passed!).RootElement.GetProperty("params");
        Assert.Equal(("s1", "DESK"), (p.GetProperty("id").GetString(), p.GetProperty("pc").GetString()));
        Assert.False(p.TryGetProperty("secret", out _));
        Assert.False(p.TryGetProperty("pid", out _));
        Assert.DoesNotContain("correct horse", passed);
    }

    [Theory]
    [InlineData("""{"method":"bot.report","params":{"id":"s1","event":"Stop","pc":"DESK","secret":"wrong horse battery!!"}}""")]
    [InlineData("""{"method":"bot.report","params":{"id":"s1","event":"Stop","pc":"DESK","secret":"correct horse batter"}}""")]
    [InlineData("""{"method":"bot.report","params":{"id":"s1","event":"Stop","pc":"DESK"}}""")]
    [InlineData("""{"method":"bot.report","params":{"id":"s1","event":"Stop","pc":"DESK","secret":42}}""")]
    [InlineData("""{"method":"bot.report","params":{"id":"s1","event":"Stop","secret":"correct horse battery"}}""")]
    [InlineData("""{"method":"bot.tell","params":{"id":"s1","text":"rm -rf","pc":"DESK","secret":"correct horse battery"}}""")]
    [InlineData("""{"method":"voice.say","params":{"text":"new bot in abel","pc":"DESK","secret":"correct horse battery"}}""")]
    [InlineData("""{"method":"scene.list","params":{"pc":"DESK","secret":"correct horse battery"}}""")]
    [InlineData("""{"method":"bot.report","secret":"correct horse battery"}""")]
    [InlineData("""["bot.report"]""")]
    [InlineData("""not json""")]
    public void EverythingElseIsTurnedAway(string line) => Assert.Null(BotReportGate.Accepted(line, Secret));

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
