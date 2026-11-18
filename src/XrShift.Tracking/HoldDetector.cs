using System.Numerics;

namespace XrShift.Tracking;

/// <summary>What the sensors read over the most recent stretch of time.</summary>
/// <param name="GyroMean">Average turn rate, rad/s. When truly still, this is the gyro's offset.</param>
/// <param name="AccelMean">Average accelerometer reading, m/s². When still, this points up.</param>
/// <param name="TiltSpreadDeg">How far the gravity direction wandered, degrees.</param>
/// <param name="GyroSpread">Largest difference between 100 ms gyro averages on any axis, rad/s.</param>
/// <param name="GyroPeak">Fastest 100 ms average turn rate, rad/s.</param>
/// <param name="GyroNoise">Sample-to-sample gyro noise per axis, rad/s rms.</param>
public readonly record struct HoldReading(
    Vector3 GyroMean, Vector3 AccelMean, float TiltSpreadDeg, float GyroSpread, float GyroPeak, float GyroNoise);

/// <summary>
/// Spots when the glasses are being held in one position, so a calibration step can wait for
/// the wearer instead of trusting a countdown. Thread-safe.
/// </summary>
public sealed class HoldDetector
{
    const float BlockSeconds = 0.1f;
    const float NominalDt = 0.001f;
    const float MaxDt = 0.05f;

    readonly object _gate = new();
    readonly Vector3[] _gyro, _accel, _gyroSquare;
    int _filled, _index, _samples;
    float _blockTime;
    Vector3 _gyroSum, _accelSum, _gyroSquareSum;
    ulong _lastTimestamp;

    /// <param name="seconds">How long a position must be kept to count as a hold.</param>
    public HoldDetector(float seconds = 1.5f)
    {
        int blocks = Math.Max(3, (int)MathF.Round(seconds / BlockSeconds));
        _gyro = new Vector3[blocks];
        _accel = new Vector3[blocks];
        _gyroSquare = new Vector3[blocks];
    }

    /// <summary>Gravity may wander this far (degrees) during a hold. A worn head is never perfectly still.</summary>
    public float MaxTiltSpreadDeg { get; init; } = 2f;
    /// <summary>Fastest allowed 100 ms average turn rate, rad/s. Catches a steady turn, which gravity cannot see.</summary>
    public float MaxGyroRate { get; init; } = 0.09f;
    /// <summary>Largest allowed difference between 100 ms gyro averages, rad/s. Tighten for a desk reading.</summary>
    public float MaxGyroSpread { get; init; } = float.PositiveInfinity;

    public void Add(ImuSample s)
    {
        lock (_gate)
        {
            float dt = _lastTimestamp != 0 && s.TimestampNs > _lastTimestamp
                ? (float)((s.TimestampNs - _lastTimestamp) * 1e-9)
                : NominalDt;
            if (dt > MaxDt) dt = NominalDt;
            _lastTimestamp = s.TimestampNs;

            _gyroSum += s.Gyro;
            _accelSum += s.Accel;
            _gyroSquareSum += s.Gyro * s.Gyro;
            _samples++;
            _blockTime += dt;
            if (_blockTime < BlockSeconds) return;

            _gyro[_index] = _gyroSum / _samples;
            _accel[_index] = _accelSum / _samples;
            _gyroSquare[_index] = _gyroSquareSum / _samples;
            _index = (_index + 1) % _gyro.Length;
            _filled = Math.Min(_filled + 1, _gyro.Length);
            ClearBlock();
        }
    }

    /// <summary>Forgets everything seen so far. The next reading needs a full stretch of new data.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _filled = _index = 0;
            ClearBlock();
        }
    }

    /// <summary>Reads the most recent stretch. False until a full stretch has arrived since the last reset.</summary>
    public bool TryRead(out HoldReading reading)
    {
        lock (_gate)
        {
            reading = default;
            int n = _gyro.Length;
            if (_filled < n) return false;

            Vector3 gyroMean = Vector3.Zero, accelMean = Vector3.Zero, squareMean = Vector3.Zero;
            Vector3 min = _gyro[0], max = _gyro[0];
            float peak = 0;
            for (int i = 0; i < n; i++)
            {
                gyroMean += _gyro[i];
                accelMean += _accel[i];
                squareMean += _gyroSquare[i];
                min = Vector3.Min(min, _gyro[i]);
                max = Vector3.Max(max, _gyro[i]);
                peak = MathF.Max(peak, _gyro[i].Length());
            }
            gyroMean /= n;
            accelMean /= n;
            squareMean /= n;
            if (accelMean.Length() < 1f) return false;

            Vector3 up = Vector3.Normalize(accelMean);
            float tilt = 0;
            for (int i = 0; i < n; i++)
            {
                float length = _accel[i].Length();
                if (length < 1f) return false;
                float cos = Math.Clamp(Vector3.Dot(_accel[i] / length, up), -1f, 1f);
                tilt = MathF.Max(tilt, MathF.Acos(cos));
            }

            Vector3 range = max - min;
            Vector3 variance = Vector3.Max(squareMean - gyroMean * gyroMean, Vector3.Zero);
            reading = new HoldReading(
                gyroMean, accelMean,
                tilt * (180f / MathF.PI),
                MathF.Max(range.X, MathF.Max(range.Y, range.Z)),
                peak,
                MathF.Sqrt((variance.X + variance.Y + variance.Z) / 3f));
            return true;
        }
    }

    /// <summary>True if a reading is steady enough to count as holding one position.</summary>
    public bool IsHold(in HoldReading r) =>
        r.TiltSpreadDeg <= MaxTiltSpreadDeg && r.GyroPeak <= MaxGyroRate && r.GyroSpread <= MaxGyroSpread;

    void ClearBlock()
    {
        _gyroSum = _accelSum = _gyroSquareSum = Vector3.Zero;
        _samples = 0;
        _blockTime = 0;
    }
}
