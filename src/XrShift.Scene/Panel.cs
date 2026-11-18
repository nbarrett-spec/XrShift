using System.Numerics;
using System.Runtime.InteropServices;

namespace XrShift.Scene;

/// <summary>
/// A virtual screen floating in space around the wearer. It sits at a direction and distance
/// from the head and faces back toward it. Sizes are in metres.
/// </summary>
public sealed record Panel
{
    const float DegToRad = MathF.PI / 180f;

    /// <summary>Direction of the centre: + = to the left of straight ahead (same sign as head yaw).</summary>
    public float YawDeg { get; init; }
    /// <summary>Direction of the centre: + = above the horizon.</summary>
    public float PitchDeg { get; init; }
    /// <summary>Spin about the line of sight: + = top leans to the wearer's right.</summary>
    public float RollDeg { get; init; }
    public float Distance { get; init; } = 2f;
    public float Width { get; init; } = 1.6f;
    public float Height { get; init; } = 0.9f;
    /// <summary>How far the screen wraps around horizontally, in degrees of arc. 0 = flat.</summary>
    public float CurveDeg { get; init; }
    /// <summary>Turn about the screen's own upright middle line: + faces it more to the right. Flat screens only.</summary>
    public float TurnDeg { get; init; }

    /// <summary>A band all the way around the wearer, e.g. a horizon ruler.</summary>
    public static Panel Ring(float radius, float height) => new()
    {
        Distance = radius,
        Width = 2f * MathF.PI * radius,
        Height = height,
        CurveDeg = 360f,
    };

    /// <summary>Panel axes (X right, Y up, Z toward the wearer) into the world.</summary>
    public Quaternion Rotation =>
        Quaternion.CreateFromYawPitchRoll(YawDeg * DegToRad, PitchDeg * DegToRad, -RollDeg * DegToRad);

    public Vector3 Centre => Vector3.Transform(new Vector3(0, 0, -Distance), Rotation);

    /// <summary>How wide the screen looks from the head, edge to edge.</summary>
    public float AngularWidthDeg
    {
        get
        {
            // Walk across in small steps so a screen that wraps most of the way round still adds up.
            const int steps = 72;
            float total = 0;
            Vector3 previous = LocalPoint(-0.5f, 0);
            for (int i = 1; i <= steps; i++)
            {
                Vector3 next = LocalPoint((float)i / steps - 0.5f, 0);
                float cross = previous.Z * next.X - previous.X * next.Z;
                float dot = previous.X * next.X + previous.Z * next.Z;
                total += MathF.Atan2(cross, dot);
                previous = next;
            }
            return MathF.Abs(total) / DegToRad;
        }
    }

    /// <summary>
    /// A point on the screen before it is turned to its place: <paramref name="s"/> runs -0.5 (left edge)
    /// to 0.5 (right edge), <paramref name="t"/> runs -0.5 (bottom) to 0.5 (top).
    /// </summary>
    public Vector3 LocalPoint(float s, float t)
    {
        float curve = CurveDeg * DegToRad;
        float y = t * Height;
        if (curve < 1e-3f)
        {
            if (MathF.Abs(TurnDeg) < 1e-3f) return new Vector3(s * Width, y, -Distance);
            // Turned about the upright line through the middle: the right edge swings away for + turns.
            float turn = TurnDeg * DegToRad;
            return new Vector3(s * Width * MathF.Cos(turn), y, -Distance - s * Width * MathF.Sin(turn));
        }

        // The width is laid along an arc that bulges away from the wearer.
        float radius = Width / curve;
        float angle = s * curve;
        return new Vector3(radius * MathF.Sin(angle), y, -Distance + radius * (1f - MathF.Cos(angle)));
    }

    public Vector3 WorldPoint(float s, float t) => Vector3.Transform(LocalPoint(s, t), Rotation);

    /// <summary>
    /// Where a line of sight from the head meets the screen: <paramref name="s"/> and <paramref name="t"/> as in
    /// <see cref="LocalPoint"/> (0,0 is the middle; beyond +-0.5 is off the screen). False if the screen is
    /// behind that line of sight altogether.
    /// </summary>
    public bool TryHit(Vector3 direction, out float s, out float t)
    {
        s = t = 0;
        Vector3 d = Vector3.Transform(direction, Quaternion.Conjugate(Rotation));
        float curve = CurveDeg * DegToRad;

        if (curve < 1e-3f)
        {
            // The flat screen's plane passes through (0, 0, -D); its across direction is (cos, 0, -sin) of the turn.
            float turn = TurnDeg * DegToRad;
            var across = new Vector3(MathF.Cos(turn), 0, -MathF.Sin(turn));
            var normal = new Vector3(MathF.Sin(turn), 0, MathF.Cos(turn));
            float facing = Vector3.Dot(d, normal);
            if (facing > -1e-4f) return false;
            float reach = Vector3.Dot(new Vector3(0, 0, -Distance), normal) / facing;
            if (reach <= 0) return false;
            Vector3 onPlane = d * reach - new Vector3(0, 0, -Distance);
            s = Vector3.Dot(onPlane, across) / Width;
            t = onPlane.Y / Height;
            return true;
        }

        // Curved: the screen is part of an upright cylinder of this radius whose axis passes through (0, -D + R).
        float radius = Width / curve, centreZ = -Distance + radius;
        float a = d.X * d.X + d.Z * d.Z;
        if (a < 1e-8f) return false;
        float b = -2f * d.Z * centreZ, c = centreZ * centreZ - radius * radius;
        float disc = b * b - 4f * a * c;
        if (disc < 0) return false;
        float root = MathF.Sqrt(disc);
        // The screen is the side of the cylinder away from the wearer: the farther crossing.
        float along = MathF.Max((-b + root) / (2f * a), (-b - root) / (2f * a));
        if (along <= 0) return false;
        float x = d.X * along, z = d.Z * along - centreZ;
        s = MathF.Atan2(x, -z) / curve;
        t = d.Y * along / Height;
        return true;
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct MeshVertex(Vector3 Position, Vector2 Uv);

/// <summary>Triangles ready for the GPU. Texture U runs left to right, V top to bottom.</summary>
public sealed record Mesh(MeshVertex[] Vertices, ushort[] Indices);

public static class PanelMesh
{
    /// <summary>Largest slice of arc drawn as one flat strip; small enough that a curve looks smooth.</summary>
    const float DegreesPerStrip = 2f;

    public static Mesh Build(Panel panel)
    {
        int columns = panel.CurveDeg < 0.06f
            ? 1
            : Math.Clamp((int)MathF.Ceiling(panel.CurveDeg / DegreesPerStrip), 2, 360);

        var vertices = new MeshVertex[(columns + 1) * 2];
        var indices = new ushort[columns * 6];
        for (int c = 0; c <= columns; c++)
        {
            float u = (float)c / columns;
            vertices[c * 2] = new MeshVertex(panel.WorldPoint(u - 0.5f, 0.5f), new Vector2(u, 0));
            vertices[c * 2 + 1] = new MeshVertex(panel.WorldPoint(u - 0.5f, -0.5f), new Vector2(u, 1));
        }
        for (int c = 0; c < columns; c++)
        {
            int topLeft = c * 2, bottomLeft = topLeft + 1, topRight = topLeft + 2, bottomRight = topLeft + 3;
            int i = c * 6;
            indices[i] = (ushort)topLeft;
            indices[i + 1] = (ushort)bottomLeft;
            indices[i + 2] = (ushort)topRight;
            indices[i + 3] = (ushort)topRight;
            indices[i + 4] = (ushort)bottomLeft;
            indices[i + 5] = (ushort)bottomRight;
        }
        return new Mesh(vertices, indices);
    }

    /// <summary>A flat rectangle fixed to the view (for a crosshair or status text): centre and size in degrees.</summary>
    public static Mesh HeadLocked(float centreXDeg, float centreYDeg, float widthDeg, float heightDeg, float distance = 1f)
    {
        const float degToRad = MathF.PI / 180f;
        float x0 = distance * MathF.Tan((centreXDeg - widthDeg / 2f) * degToRad);
        float x1 = distance * MathF.Tan((centreXDeg + widthDeg / 2f) * degToRad);
        float y0 = distance * MathF.Tan((centreYDeg - heightDeg / 2f) * degToRad);
        float y1 = distance * MathF.Tan((centreYDeg + heightDeg / 2f) * degToRad);
        return new Mesh(
        [
            new MeshVertex(new Vector3(x0, y1, -distance), new Vector2(0, 0)),
            new MeshVertex(new Vector3(x0, y0, -distance), new Vector2(0, 1)),
            new MeshVertex(new Vector3(x1, y1, -distance), new Vector2(1, 0)),
            new MeshVertex(new Vector3(x1, y0, -distance), new Vector2(1, 1)),
        ],
        [0, 1, 2, 2, 1, 3]);
    }
}
