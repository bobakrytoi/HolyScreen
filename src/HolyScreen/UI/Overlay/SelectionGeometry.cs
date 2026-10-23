using System;
using HolyScreen.Capture;

namespace HolyScreen.UI.Overlay;

/// <summary>What a pointer press on (or near) the current selection would grab.</summary>
internal enum HandleKind
{
    None,
    Move,
    TopLeft,
    Top,
    TopRight,
    Right,
    BottomRight,
    Bottom,
    BottomLeft,
    Left,
}

/// <summary>
/// Pure selection math in virtual-screen pixels (no UI): creating a rectangle from a drag, hit-testing its edges and
/// corners, resizing (including dragging an edge across the opposite one) and moving — always kept inside the
/// captured frame.
/// </summary>
internal static class SelectionGeometry
{
    /// <summary>
    /// The rectangle spanned by a drag from the anchor pixel to the cursor pixel. Both pixels are included, so
    /// dragging from x=100 to x=199 selects 100 columns. With <paramref name="square"/> the shorter side follows the
    /// longer one (Shift).
    /// </summary>
    public static PixelRect FromDrag(int anchorX, int anchorY, int x, int y, bool square, PixelRect bounds)
    {
        if (square)
        {
            int dx = x - anchorX;
            int dy = y - anchorY;
            int side = Math.Max(Math.Abs(dx), Math.Abs(dy));
            x = anchorX + (dx < 0 ? -side : side);
            y = anchorY + (dy < 0 ? -side : side);
        }

        PixelRect rect = PixelRect.FromEdges(Math.Min(anchorX, x), Math.Min(anchorY, y), Math.Max(anchorX, x) + 1, Math.Max(anchorY, y) + 1);
        return rect.Intersect(bounds);
    }

    /// <summary>
    /// Which part of <paramref name="selection"/> is under the pointer. Edges and corners count within
    /// <paramref name="tolerance"/> pixels of the border line on either side; the interior means <see cref="HandleKind.Move"/>.
    /// </summary>
    public static HandleKind HitTest(PixelRect selection, int x, int y, int tolerance)
    {
        if (selection.IsEmpty) return HandleKind.None;
        if (x < selection.X - tolerance || x > selection.Right + tolerance ||
            y < selection.Y - tolerance || y > selection.Bottom + tolerance)
        {
            return HandleKind.None;
        }

        int toLeft = Math.Abs(x - selection.X);
        int toRight = Math.Abs(x - selection.Right);
        int toTop = Math.Abs(y - selection.Y);
        int toBottom = Math.Abs(y - selection.Bottom);

        bool left = toLeft <= tolerance;
        bool right = toRight <= tolerance;
        bool top = toTop <= tolerance;
        bool bottom = toBottom <= tolerance;

        // On a tiny selection both edges can be in reach: take the nearer one.
        if (left && right)
        {
            if (toLeft <= toRight) right = false;
            else left = false;
        }

        if (top && bottom)
        {
            if (toTop <= toBottom) bottom = false;
            else top = false;
        }

        if (top && left) return HandleKind.TopLeft;
        if (top && right) return HandleKind.TopRight;
        if (bottom && right) return HandleKind.BottomRight;
        if (bottom && left) return HandleKind.BottomLeft;
        if (top) return HandleKind.Top;
        if (bottom) return HandleKind.Bottom;
        if (left) return HandleKind.Left;
        if (right) return HandleKind.Right;
        return selection.Contains(x, y) ? HandleKind.Move : HandleKind.None;
    }

    /// <summary>
    /// Applies a drag of (<paramref name="dx"/>, <paramref name="dy"/>) to the edges <paramref name="handle"/> controls.
    /// Dragging an edge past the opposite one flips the rectangle; the returned handle is the one now being dragged
    /// (useful for the cursor). The result stays inside <paramref name="bounds"/> and is at least 1×1.
    /// </summary>
    public static (PixelRect Rect, HandleKind Handle) Resize(PixelRect start, HandleKind handle, int dx, int dy, PixelRect bounds)
    {
        int left = start.X, top = start.Y, right = start.Right, bottom = start.Bottom;

        if (handle is HandleKind.Left or HandleKind.TopLeft or HandleKind.BottomLeft) left += dx;
        if (handle is HandleKind.Right or HandleKind.TopRight or HandleKind.BottomRight) right += dx;
        if (handle is HandleKind.Top or HandleKind.TopLeft or HandleKind.TopRight) top += dy;
        if (handle is HandleKind.Bottom or HandleKind.BottomLeft or HandleKind.BottomRight) bottom += dy;

        left = Math.Clamp(left, bounds.X, bounds.Right);
        right = Math.Clamp(right, bounds.X, bounds.Right);
        top = Math.Clamp(top, bounds.Y, bounds.Bottom);
        bottom = Math.Clamp(bottom, bounds.Y, bounds.Bottom);

        HandleKind current = handle;
        if (left > right)
        {
            (left, right) = (right, left);
            current = FlipHorizontal(current);
        }

        if (top > bottom)
        {
            (top, bottom) = (bottom, top);
            current = FlipVertical(current);
        }

        if (right - left < 1)
        {
            if (right < bounds.Right) right = left + 1;
            else left = right - 1;
        }

        if (bottom - top < 1)
        {
            if (bottom < bounds.Bottom) bottom = top + 1;
            else top = bottom - 1;
        }

        return (PixelRect.FromEdges(left, top, right, bottom), current);
    }

    /// <summary>Moves the selection, sliding along the frame edges instead of shrinking.</summary>
    public static PixelRect Move(PixelRect start, int dx, int dy, PixelRect bounds) => start.Offset(dx, dy).MoveInside(bounds);

    public static HandleKind FlipHorizontal(HandleKind handle) => handle switch
    {
        HandleKind.Left => HandleKind.Right,
        HandleKind.Right => HandleKind.Left,
        HandleKind.TopLeft => HandleKind.TopRight,
        HandleKind.TopRight => HandleKind.TopLeft,
        HandleKind.BottomLeft => HandleKind.BottomRight,
        HandleKind.BottomRight => HandleKind.BottomLeft,
        _ => handle,
    };

    public static HandleKind FlipVertical(HandleKind handle) => handle switch
    {
        HandleKind.Top => HandleKind.Bottom,
        HandleKind.Bottom => HandleKind.Top,
        HandleKind.TopLeft => HandleKind.BottomLeft,
        HandleKind.BottomLeft => HandleKind.TopLeft,
        HandleKind.TopRight => HandleKind.BottomRight,
        HandleKind.BottomRight => HandleKind.TopRight,
        _ => handle,
    };
}
