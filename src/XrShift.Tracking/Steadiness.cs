using System.Numerics;

namespace XrShift.Tracking;

/// <summary>One moment of a replay: the pose drawn with, and the unpredicted pose the tracker had right then.</summary>
public readonly record struct PoseFrame(double Time, Quaternion Reported, Quaternion Actual);

/// <summary>
/// How steady a picture is. Wander: how much the drawn pose moves while the head is still.
/// Overshoot: how far it runs past where the head stops. Lag: how far it trails during a steady turn.
/// Overshoot and lag compare the drawn pose with where the head actually was when the frame was seen,
/// which a replay knows because it has the future.
/// </summary>
public readonly record struct SteadyScore(float WanderRmsDeg, float WanderPeakDeg, float OvershootDeg, float LagMs)
{
    /// <summary>One Pro: 1920 pixels over 50.7°.</summary>
    public const float PixelsPerDegree = 37.9f;

    public float WanderRmsArcmin => WanderRmsDeg * 60;
    public float WanderPeakArcmin => WanderPeakDeg * 60;
    public float WanderRmsPx => WanderRmsDeg * PixelsPerDegree;
    public float WanderPeakPx => WanderPeakDeg * PixelsPerDegree;

    const float StillDegPerSec = 3f;   // under this the head counts as still
    const float MovingDegPerSec = 10f; // a still stretch starts SettleSeconds after the last speed above this
    const float SettleSeconds = 0.5f;
    const float TurnDegPerSec = 20f;   // a turn or stop is judged above this
    const float SpeedWindow = 0.02f;   // speed is measured over this many seconds of the actual pose

    /// <summary>Feeds samples through <paramref name="tracker"/>, reading the drawn pose with <paramref name="read"/> after each one.</summary>
    public static List<PoseFrame> Replay(IEnumerable<ImuSample> samples, HeadTracker tracker, Func<HeadTracker, HeadPose> read)
    {
        var frames = new List<PoseFrame>();
        ulong first = 0;
        foreach (ImuSample s in samples)
        {
            tracker.Update(s);
            if (!tracker.HasPose) continue;
            if (first == 0) first = s.TimestampNs;
            Quaternion actual = tracker.GetPose().Orientation; // first, so it sets straight-ahead
            frames.Add(new PoseFrame((s.TimestampNs - first) * 1e-9, read(tracker).Orientation, actual));
        }
        return frames;
    }

    /// <param name="lookAheadSeconds">How long after each frame's time it is actually seen.</param>
    public static SteadyScore Of(IReadOnlyList<PoseFrame> frames, float lookAheadSeconds)
    {
        int n = frames.Count;
        var reported = new Vector3[n];
        var actual = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            reported[i] = Forward(frames[i].Reported);
            actual[i] = Forward(frames[i].Actual);
        }

        // Per frame: actual speed (deg/s), direction of motion, and how far the drawn pose is ahead (+)
        // of where the head is when the frame is seen, along that direction (radians).
        var speed = new float[n];
        var dir = new Vector3[n];
        var ahead = new float[n];
        int last = -1;
        for (int i = 0, back = 0, seen = 0; i < n; i++)
        {
            while (back < i && frames[i].Time - frames[back + 1].Time >= SpeedWindow) back++;
            while (seen < n - 1 && frames[seen].Time < frames[i].Time + lookAheadSeconds) seen++;
            if (frames[seen].Time < frames[i].Time + lookAheadSeconds) break; // ran out of future
            last = i;

            Vector3 move = actual[i] - actual[back];
            double span = frames[i].Time - frames[back].Time;
            speed[i] = span > 0 ? (float)(move.Length() / span) * (180f / MathF.PI) : 0f;
            dir[i] = move.Length() > 1e-9f ? Vector3.Normalize(move) : Vector3.Zero;
            ahead[i] = Vector3.Dot(reported[i] - actual[seen], dir[i]);
        }

        // When the head next moves: a frame only counts as still if the head is still when it is seen, too.
        var nextMove = new double[n];
        double next = double.PositiveInfinity;
        for (int i = last; i >= 0; i--)
        {
            if (speed[i] >= StillDegPerSec) next = frames[i].Time;
            nextMove[i] = next;
        }

        // Wander: drawn pose's spread around its own average over each settled still stretch.
        double sumSq = 0;
        float peak = 0;
        int count = 0;
        double lastMoving = 1.0 - SettleSeconds; // the filter is still settling onto gravity in its first second
        var stretch = new List<Vector3>();
        void EndStretch()
        {
            if (stretch.Count == 0) return;
            Vector3 mean = Vector3.Zero;
            foreach (Vector3 f in stretch) mean += f;
            mean = Vector3.Normalize(mean);
            foreach (Vector3 f in stretch)
            {
                float a = AngleDeg(f, mean);
                sumSq += a * a;
                peak = MathF.Max(peak, a);
                count++;
            }
            stretch.Clear();
        }

        // Lag: during steady turns (speed within 10% of what it was SpeedWindow earlier).
        double lagSum = 0;
        int lagCount = 0;

        // Overshoot: how far the drawn pose ever gets past where the head comes to rest, along the turn,
        // from the start of each turn until SettleSeconds after it stops.
        float overshoot = float.NaN;
        int turnStart = -1, fastFrame = -1;
        double stopTime = double.NaN;

        for (int i = 0, back = 0; i <= last; i++)
        {
            while (back < i && frames[i].Time - frames[back + 1].Time >= SpeedWindow) back++;
            double t = frames[i].Time;

            if (speed[i] >= MovingDegPerSec) lastMoving = t;
            if (speed[i] < StillDegPerSec && t - lastMoving >= SettleSeconds && nextMove[i] > t + lookAheadSeconds + SpeedWindow)
                stretch.Add(reported[i]);
            else EndStretch();

            if (speed[i] > TurnDegPerSec && MathF.Abs(speed[i] - speed[back]) < 0.1f * speed[i])
            {
                lagSum += -ahead[i] / (speed[i] * MathF.PI / 180f) * 1000;
                lagCount++;
            }

            if (speed[i] > TurnDegPerSec)
            {
                if (fastFrame < 0) turnStart = i;
                fastFrame = i;
                stopTime = double.NaN;
            }
            else if (fastFrame >= 0)
            {
                if (double.IsNaN(stopTime) && speed[i] < MovingDegPerSec) stopTime = t;
                if (!double.IsNaN(stopTime) && t - stopTime > SettleSeconds)
                {
                    Vector3 rest = actual[i];
                    float worst = 0;
                    for (int j = turnStart; j <= i; j++)
                        worst = MathF.Max(worst, Vector3.Dot(reported[j] - rest, dir[fastFrame]));
                    overshoot = float.IsNaN(overshoot) ? worst : MathF.Max(overshoot, worst);
                    fastFrame = -1;
                }
            }
        }
        EndStretch();

        return new SteadyScore(
            count > 0 ? (float)Math.Sqrt(sumSq / count) : float.NaN,
            count > 0 ? peak : float.NaN,
            overshoot * (180f / MathF.PI),
            lagCount > 0 ? (float)(lagSum / lagCount) : float.NaN);
    }

    static Vector3 Forward(Quaternion q) => Vector3.Transform(-Vector3.UnitZ, q);

    static float AngleDeg(Vector3 a, Vector3 b) =>
        2 * MathF.Asin(MathF.Min(1f, (a - b).Length() / 2)) * (180f / MathF.PI);
}
