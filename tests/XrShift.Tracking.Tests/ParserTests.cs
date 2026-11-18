using System.Numerics;
using XrShift.Tracking;

namespace XrShift.Tracking.Tests;

public class ParserTests
{
    static ImuSample Sample(int i) => new(
        1_000_000_000UL + (ulong)i * 1_000_000UL,
        new Vector3(0.01f * i, -0.02f * i, 0.5f),
        new Vector3(0.1f, 9.7f, -0.3f * i));

    static byte[] Frame(ImuSample s, byte headerStart = 0x28)
    {
        byte[] frame = new byte[ImuFrame.WireLength];
        ImuFrame.Write(frame, s, headerStart);
        return frame;
    }

    static List<ImuSample> Parse(ImuFrameParser parser, byte[] stream, int chunk)
    {
        var result = new List<ImuSample>();
        for (int i = 0; i < stream.Length; i += chunk)
            parser.Feed(stream.AsSpan(i, Math.Min(chunk, stream.Length - i)), result.Add);
        return result;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(133)]
    [InlineData(4096)]
    public void ParsesEverySampleHoweverTheStreamIsSplit(int chunk)
    {
        var stream = new List<byte> { 0x01, 0x28, 0x36, 0x00, 0x99, 0xFF, 0x80 }; // junk that looks a bit like a header
        var expected = new List<ImuSample>();
        for (int i = 0; i < 50; i++)
        {
            if (i == 20)
            {
                byte[] other = Frame(Sample(999)); // a different record type between samples
                other[ImuFrame.MarkerOffset + 1] = 0x77;
                stream.AddRange(other);
            }
            expected.Add(Sample(i));
            stream.AddRange(Frame(Sample(i)));
        }

        var parser = new ImuFrameParser();
        List<ImuSample> parsed = Parse(parser, stream.ToArray(), chunk);

        Assert.Equal(expected, parsed);
        Assert.Equal(50, parser.SamplesParsed);
        Assert.Equal(1, parser.OtherFrames);
    }

    [Fact]
    public void AcceptsTheAlternateHeaderByte()
    {
        var parser = new ImuFrameParser();
        List<ImuSample> parsed = Parse(parser, Frame(Sample(3), headerStart: 0x27), 64);
        Assert.Equal([Sample(3)], parsed);
    }

    [Fact]
    public void DropsSamplesWithImpossibleValues()
    {
        var bad = new ImuSample(5, new Vector3(float.NaN, 0, 0), new Vector3(0, 0, 9.8f));
        var huge = new ImuSample(6, new Vector3(5000, 0, 0), new Vector3(0, 0, 9.8f));
        byte[] stream = [.. Frame(bad), .. Frame(huge), .. Frame(Sample(1))];

        var parser = new ImuFrameParser();
        List<ImuSample> parsed = Parse(parser, stream, 4096);

        Assert.Equal([Sample(1)], parsed);
    }

    [Fact]
    public void RecoversAfterAResetMidRecord()
    {
        var parser = new ImuFrameParser();
        byte[] frame = Frame(Sample(1));
        parser.Feed(frame.AsSpan(0, 60), _ => Assert.Fail("incomplete record"));
        parser.Reset();
        List<ImuSample> parsed = Parse(parser, Frame(Sample(2)), 4096);
        Assert.Equal([Sample(2)], parsed);
    }
}
