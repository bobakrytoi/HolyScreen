using System;
using System.Globalization;

namespace HolyScreen.Capture;

/// <summary>
/// An integer rectangle in physical (device) pixels, half-open: it covers columns [X, Right) and rows [Y, Bottom).
/// All capture and selection math is done in these units, in virtual-screen coordinates.
/// </summary>
internal readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public static readonly PixelRect Empty = new(0, 0, 0, 0);

    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public long Area => IsEmpty ? 0 : (long)Width * Height;

    public static PixelRect FromEdges(int left, int top, int right, int bottom) =>
        new(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));

    /// <summary>The rectangle spanned by two corner points, in any order.</summary>
    public static PixelRect FromPoints(int x0, int y0, int x1, int y1) =>
        FromEdges(Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1));

    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;

    public bool Contains(PixelRect other) =>
        !other.IsEmpty && other.X >= X && other.Y >= Y && other.Right <= Right && other.Bottom <= Bottom;

    public bool IntersectsWith(PixelRect other) =>
        !IsEmpty && !other.IsEmpty && other.X < Right && other.Right > X && other.Y < Bottom && other.Bottom > Y;

    /// <summary>The overlap of both rectangles, or <see cref="Empty"/> when they do not overlap.</summary>
    public PixelRect Intersect(PixelRect other)
    {
        int left = Math.Max(X, other.X);
        int top = Math.Max(Y, other.Y);
        int right = Math.Min(Right, other.Right);
        int bottom = Math.Min(Bottom, other.Bottom);
        return right > left && bottom > top ? new PixelRect(left, top, right - left, bottom - top) : Empty;
    }

    public PixelRect Union(PixelRect other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;
        return FromEdges(Math.Min(X, other.X), Math.Min(Y, other.Y), Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom));
    }

    public PixelRect Offset(int dx, int dy) => new(X + dx, Y + dy, Width, Height);

    public PixelRect Inflate(int amount) => FromEdges(X - amount, Y - amount, Right + amount, Bottom + amount);

    /// <summary>Moves (never resizes, unless it is larger than <paramref name="bounds"/>) so the rectangle lies inside.</summary>
    public PixelRect MoveInside(PixelRect bounds)
    {
        int width = Math.Min(Width, bounds.Width);
        int height = Math.Min(Height, bounds.Height);
        int x = Math.Clamp(X, bounds.X, bounds.Right - width);
        int y = Math.Clamp(Y, bounds.Y, bounds.Bottom - height);
        return new PixelRect(x, y, width, height);
    }

    public int[] ToArray() => new[] { X, Y, Width, Height };

    public static PixelRect? FromArray(int[]? values) =>
        values is { Length: 4 } && values[2] > 0 && values[3] > 0 ? new PixelRect(values[0], values[1], values[2], values[3]) : null;

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{X},{Y} {Width}x{Height}");
}
