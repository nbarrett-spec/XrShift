using System.Numerics;

namespace XrShift.Scene;

/// <summary>
/// How a rendered frame maps onto the wearer's view. The glasses show the PC's picture
/// head-locked, filling a fixed angle; drawing with the same angle is what makes a
/// virtual screen hold still in the room when the head turns.
/// </summary>
public sealed record DisplayOptics(int Width, int Height, float HorizontalFovDeg)
{
    const float DegToRad = MathF.PI / 180f;

    /// <summary>
    /// One Pro class: 1920x1080, 57° corner to corner, which is 50.7° wide. A starting value:
    /// if world-fixed things slide when the head turns, this is the number to trim.
    /// </summary>
    public static DisplayOptics OnePro { get; } = new(1920, 1080, 50.7f);

    public float Aspect => (float)Width / Height;

    public float VerticalFovDeg =>
        2f * MathF.Atan(MathF.Tan(HorizontalFovDeg * DegToRad / 2f) / Aspect) / DegToRad;

    /// <summary>Pixels per degree in the middle of the view (it rises toward the edges).</summary>
    public float PixelsPerDegree =>
        Width / 2f / MathF.Tan(HorizontalFovDeg * DegToRad / 2f) * DegToRad;

    /// <summary>View space (X right, Y up, looking down -Z) to clip space, depth 0..1.</summary>
    public Matrix4x4 Projection(float near = 0.05f, float far = 200f) =>
        Matrix4x4.CreatePerspectiveFieldOfView(VerticalFovDeg * DegToRad, Aspect, near, far);
}

/// <summary>Turns a head pose into what the glasses should show.</summary>
public static class Camera
{
    /// <summary>World to view. <paramref name="head"/> rotates head axes (X right, Y up, Z back) into the world.</summary>
    public static Matrix4x4 View(Quaternion head) =>
        Matrix4x4.CreateFromQuaternion(Quaternion.Conjugate(head));

    /// <summary>The distance between a typical adult's eyes, in metres.</summary>
    public const float DefaultEyeSeparation = 0.063f;

    /// <summary>
    /// World to the view of one eye, for 3D: the eye sits <paramref name="eyeX"/> metres to the right of the
    /// middle of the head (negative = the left eye). Both eyes look the same way, so something far away lands
    /// in the same place for each and something near lands apart, which is what gives it depth.
    /// </summary>
    public static Matrix4x4 EyeView(Quaternion head, float eyeX) =>
        View(head) * Matrix4x4.CreateTranslation(-eyeX, 0, 0);

    /// <summary>
    /// Where one eye's picture goes in a side-by-side frame: left eye in the left half. A frame twice as wide
    /// as the display (3840x1080) carries two full pictures; a normal one carries two squeezed to half width,
    /// which the glasses stretch back out.
    /// </summary>
    public static (float Left, float Top, float Width, float Height) EyeViewport(int frameWidth, int frameHeight, DisplayOptics optics, bool rightEye)
    {
        float half = frameWidth / 2f, x = rightEye ? half : 0;
        bool fullPictures = half / frameHeight >= optics.Aspect * 0.9f;
        if (!fullPictures) return (x, 0, half, frameHeight);
        float scale = MathF.Min(half / optics.Width, (float)frameHeight / optics.Height);
        float w = optics.Width * scale, h = optics.Height * scale;
        return (x + (half - w) / 2f, (frameHeight - h) / 2f, w, h);
    }

    public static Matrix4x4 ViewProjection(Quaternion head, DisplayOptics optics) =>
        View(head) * optics.Projection();

    /// <summary>Where a world point lands on the display, in pixels from the top-left. False if it is behind the wearer.</summary>
    public static bool TryProject(Vector3 world, Quaternion head, DisplayOptics optics, out Vector2 pixel)
    {
        Vector4 clip = Vector4.Transform(new Vector4(world, 1f), ViewProjection(head, optics));
        if (clip.W <= 1e-6f)
        {
            pixel = default;
            return false;
        }
        pixel = new Vector2(
            (clip.X / clip.W * 0.5f + 0.5f) * optics.Width,
            (0.5f - clip.Y / clip.W * 0.5f) * optics.Height);
        return true;
    }
}
