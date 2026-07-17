namespace ComicChat.Core.Geometry;

/// <summary>
/// The comic engine's rectangle. Port of SRECT (bbox.h:4).
/// </summary>
/// <remarks>
/// Coordinates are MM_TWIPS with <b>y negative-down</b>: a panel's origin is its
/// top-left, so <c>Bottom == -UnitHeight</c> and <c>Top &gt; Bottom</c> always.
/// The original stored these as 16-bit to save memory; we widen to int (the
/// arithmetic is identical over the ranges in play) but keep the y convention,
/// because every layout constant in the engine is tuned to it.
/// </remarks>
public struct SRect : IEquatable<SRect>
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public SRect(int left, int top, int right, int bottom)
    {
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }

    public static readonly SRect Empty = new(0, 0, 0, 0);

    public readonly int Width => Right - Left;

    /// <summary>Positive height, given y-negative-down storage.</summary>
    public readonly int Height => Top - Bottom;

    public readonly bool IsEmpty => Width <= 0 || Height <= 0;

    public readonly int CenterX => Left + Width / 2;
    public readonly int CenterY => Bottom + Height / 2;

    public void Offset(int dx, int dy)
    {
        Left += dx;
        Right += dx;
        Top += dy;
        Bottom += dy;
    }

    public readonly SRect Offsetted(int dx, int dy) =>
        new(Left + dx, Top + dy, Right + dx, Bottom + dy);

    /// <summary>Shrink by <paramref name="dx"/>/<paramref name="dy"/> on every side.</summary>
    public void Inflate(int dx, int dy)
    {
        Left -= dx;
        Right += dx;
        Top += dy;
        Bottom -= dy;
    }

    public readonly bool Contains(int x, int y) =>
        x >= Left && x <= Right && y <= Top && y >= Bottom;

    public readonly bool Contains(SRect other) =>
        other.Left >= Left && other.Right <= Right &&
        other.Top <= Top && other.Bottom >= Bottom;

    /// <summary>Port of BBoxOverlap (bbox.cpp). Touching edges do not count as overlap.</summary>
    public readonly bool Overlaps(SRect other) =>
        Left < other.Right && Right > other.Left &&
        Bottom < other.Top && Top > other.Bottom;

    public readonly SRect Union(SRect other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;
        return new SRect(
            Math.Min(Left, other.Left),
            Math.Max(Top, other.Top),
            Math.Max(Right, other.Right),
            Math.Min(Bottom, other.Bottom));
    }

    public readonly SRect Intersect(SRect other) =>
        new(Math.Max(Left, other.Left),
            Math.Min(Top, other.Top),
            Math.Min(Right, other.Right),
            Math.Max(Bottom, other.Bottom));

    public readonly bool Equals(SRect other) =>
        Left == other.Left && Top == other.Top &&
        Right == other.Right && Bottom == other.Bottom;

    public override readonly bool Equals(object? obj) => obj is SRect r && Equals(r);

    public override readonly int GetHashCode() => HashCode.Combine(Left, Top, Right, Bottom);

    public static bool operator ==(SRect a, SRect b) => a.Equals(b);
    public static bool operator !=(SRect a, SRect b) => !a.Equals(b);

    public override readonly string ToString() =>
        $"SRect(L={Left}, T={Top}, R={Right}, B={Bottom}, {Width}x{Height})";
}
