namespace XrShift.Tracking;

/// <summary>Turns the raw TCP byte stream into samples. Tolerates split reads, junk and other record types.</summary>
public sealed class ImuFrameParser
{
    byte[] _buf = new byte[16 * 1024];
    int _len;
    long _samples;
    long _otherFrames;

    public long SamplesParsed => Interlocked.Read(ref _samples);
    /// <summary>Records with a valid header that were not gyro + accelerometer data.</summary>
    public long OtherFrames => Interlocked.Read(ref _otherFrames);

    public void Reset() => _len = 0;

    public void Feed(ReadOnlySpan<byte> data, Action<ImuSample> onSample)
    {
        if (_len + data.Length > _buf.Length)
            Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _len + data.Length));
        data.CopyTo(_buf.AsSpan(_len));
        _len += data.Length;

        Span<byte> span = _buf.AsSpan(0, _len);
        int pos = 0;
        while (true)
        {
            int header = FindHeader(span[pos..]);
            if (header < 0)
            {
                // Nothing usable left; keep a possible partial header at the end.
                pos = span.Length - Math.Min(ImuFrame.HeaderLength - 1, span.Length - pos);
                break;
            }

            pos += header;
            if (span.Length - pos < ImuFrame.ParseLength)
                break; // wait for the rest of this record

            ReadOnlySpan<byte> frame = span.Slice(pos, ImuFrame.ParseLength);
            if (frame.Slice(ImuFrame.MarkerOffset, ImuFrame.ImuMarker.Length).SequenceEqual(ImuFrame.ImuMarker)
                && ImuFrame.TryRead(frame, out ImuSample sample))
            {
                Interlocked.Increment(ref _samples);
                onSample(sample);
                pos += ImuFrame.ParseLength;
            }
            else
            {
                Interlocked.Increment(ref _otherFrames);
                pos += ImuFrame.HeaderLength;
            }
        }

        span[pos..].CopyTo(_buf);
        _len -= pos;
    }

    static int FindHeader(ReadOnlySpan<byte> s)
    {
        int from = 0;
        while (true)
        {
            int i = s[from..].IndexOf(ImuFrame.HeaderTail);
            if (i < 0) return -1;
            int tail = from + i;
            if (tail >= 1 && ImuFrame.IsHeaderStart(s[tail - 1])) return tail - 1;
            from = tail + 1;
        }
    }
}
