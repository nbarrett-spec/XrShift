using System.Buffers.Binary;
using System.Numerics;

namespace XrShift.Tracking;

/// <summary>One gyro + accelerometer reading in the glasses' own sensor axes (rad/s, m/s²).</summary>
public readonly record struct ImuSample(ulong TimestampNs, Vector3 Gyro, Vector3 Accel);

/// <summary>
/// Layout of the records the glasses stream on TCP 52998. Taken from community reverse
/// engineering of the One / One Pro / 1S; not yet confirmed on the Pro+ (USB PID 0444).
/// </summary>
public static class ImuFrame
{
    public const int HeaderLength = 6;
    /// <summary>Bytes needed from the header start to read one sample.</summary>
    public const int ParseLength = 84;
    /// <summary>Full record length as sent by the glasses.</summary>
    public const int WireLength = 134;

    public const int TimestampOffset = 14;
    public const int GyroOffset = 34;
    public const int AccelOffset = 46;
    public const int MarkerOffset = 78;

    /// <summary>Header bytes 1..5. Byte 0 is 0x28 on most firmware, 0x27 on some.</summary>
    public static ReadOnlySpan<byte> HeaderTail => [0x36, 0x00, 0x00, 0x00, 0x80];
    /// <summary>Marks a record as gyro + accelerometer data.</summary>
    public static ReadOnlySpan<byte> ImuMarker => [0x00, 0x40, 0x1F, 0x00, 0x00, 0x40];

    public static bool IsHeaderStart(byte b) => b is 0x27 or 0x28;

    public static bool TryRead(ReadOnlySpan<byte> frame, out ImuSample sample)
    {
        ulong ts = BinaryPrimitives.ReadUInt64LittleEndian(frame[TimestampOffset..]);
        Vector3 gyro = ReadVector(frame[GyroOffset..]);
        Vector3 accel = ReadVector(frame[AccelOffset..]);
        sample = new ImuSample(ts, gyro, accel);
        return IsSane(gyro, 100f) && IsSane(accel, 400f);
    }

    /// <summary>Encodes a sample the way the glasses do. Used by the simulator and tests.</summary>
    public static void Write(Span<byte> dest, in ImuSample sample, byte headerStart = 0x28)
    {
        dest = dest[..WireLength];
        dest.Clear();
        dest[0] = headerStart;
        HeaderTail.CopyTo(dest[1..]);
        BinaryPrimitives.WriteUInt64LittleEndian(dest[TimestampOffset..], sample.TimestampNs);
        WriteVector(dest[GyroOffset..], sample.Gyro);
        WriteVector(dest[AccelOffset..], sample.Accel);
        ImuMarker.CopyTo(dest[MarkerOffset..]);
    }

    static Vector3 ReadVector(ReadOnlySpan<byte> b) => new(
        BinaryPrimitives.ReadSingleLittleEndian(b),
        BinaryPrimitives.ReadSingleLittleEndian(b[4..]),
        BinaryPrimitives.ReadSingleLittleEndian(b[8..]));

    static void WriteVector(Span<byte> b, Vector3 v)
    {
        BinaryPrimitives.WriteSingleLittleEndian(b, v.X);
        BinaryPrimitives.WriteSingleLittleEndian(b[4..], v.Y);
        BinaryPrimitives.WriteSingleLittleEndian(b[8..], v.Z);
    }

    static bool IsSane(Vector3 v, float limit) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) &&
        MathF.Abs(v.X) <= limit && MathF.Abs(v.Y) <= limit && MathF.Abs(v.Z) <= limit;
}
