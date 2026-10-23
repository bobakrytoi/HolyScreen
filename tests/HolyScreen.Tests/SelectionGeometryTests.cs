using HolyScreen.Capture;
using HolyScreen.UI.Overlay;
using Xunit;

namespace HolyScreen.Tests;

public class SelectionGeometryTests
{
    private static readonly PixelRect Screen = new(-1920, 0, 3840, 1080);

    [Fact]
    public void FromDrag_IncludesBothEndPixels_InAnyDirection()
    {
        Assert.Equal(new PixelRect(100, 50, 101, 31), SelectionGeometry.FromDrag(100, 50, 200, 80, false, Screen));
        Assert.Equal(new PixelRect(100, 50, 101, 31), SelectionGeometry.FromDrag(200, 80, 100, 50, false, Screen));
        Assert.Equal(new PixelRect(5, 5, 1, 1), SelectionGeometry.FromDrag(5, 5, 5, 5, false, Screen));
    }

    [Fact]
    public void FromDrag_Square_FollowsTheLongerSideAndDirection()
    {
        Assert.Equal(new PixelRect(100, 100, 51, 51), SelectionGeometry.FromDrag(100, 100, 150, 110, true, Screen));
        Assert.Equal(new PixelRect(50, 50, 51, 51), SelectionGeometry.FromDrag(100, 100, 50, 95, true, Screen));
    }

    [Fact]
    public void FromDrag_IsClippedToTheFrame()
    {
        Assert.Equal(PixelRect.FromEdges(1800, 1000, 1920, 1080), SelectionGeometry.FromDrag(1800, 1000, 2500, 1500, false, Screen));
    }

    [Theory]
    [InlineData(100, 100, "TopLeft")]
    [InlineData(300, 100, "TopRight")]
    [InlineData(300, 300, "BottomRight")]
    [InlineData(100, 300, "BottomLeft")]
    [InlineData(200, 98, "Top")]
    [InlineData(200, 303, "Bottom")]
    [InlineData(97, 200, "Left")]
    [InlineData(302, 200, "Right")]
    [InlineData(200, 200, "Move")]
    [InlineData(400, 200, "None")]
    [InlineData(200, 90, "None")]
    public void HitTest_FindsCornersEdgesAndInterior(int x, int y, string expectedName)
    {
        var selection = new PixelRect(100, 100, 200, 200); // edges at x=100/300, y=100/300
        HandleKind expected = System.Enum.Parse<HandleKind>(expectedName);
        Assert.Equal(expected, SelectionGeometry.HitTest(selection, x, y, tolerance: 6));
    }

    [Fact]
    public void HitTest_OnATinySelection_PrefersTheNearerEdge()
    {
        var selection = new PixelRect(100, 100, 4, 4);
        Assert.Equal(HandleKind.BottomRight, SelectionGeometry.HitTest(selection, 104, 104, tolerance: 6));
        Assert.Equal(HandleKind.TopLeft, SelectionGeometry.HitTest(selection, 101, 101, tolerance: 6));
    }

    [Fact]
    public void Resize_MovesOnlyTheGrabbedEdges()
    {
        var start = new PixelRect(100, 100, 200, 200);
        Assert.Equal(PixelRect.FromEdges(100, 100, 350, 300), SelectionGeometry.Resize(start, HandleKind.Right, 50, 999, Screen).Rect);
        Assert.Equal(PixelRect.FromEdges(80, 70, 300, 300), SelectionGeometry.Resize(start, HandleKind.TopLeft, -20, -30, Screen).Rect);
        Assert.Equal(PixelRect.FromEdges(100, 100, 300, 310), SelectionGeometry.Resize(start, HandleKind.Bottom, 77, 10, Screen).Rect);
    }

    [Fact]
    public void Resize_PastTheOppositeEdge_Flips()
    {
        var start = new PixelRect(100, 100, 200, 200);
        (PixelRect rect, HandleKind handle) = SelectionGeometry.Resize(start, HandleKind.Left, 300, 0, Screen);
        Assert.Equal(PixelRect.FromEdges(300, 100, 400, 300), rect);
        Assert.Equal(HandleKind.Right, handle);

        (rect, handle) = SelectionGeometry.Resize(start, HandleKind.BottomRight, -250, -260, Screen);
        Assert.Equal(PixelRect.FromEdges(50, 40, 100, 100), rect);
        Assert.Equal(HandleKind.TopLeft, handle);
    }

    [Fact]
    public void Resize_KeepsAtLeastOnePixel_AndStaysInsideTheFrame()
    {
        var start = new PixelRect(100, 100, 200, 200);
        Assert.Equal(PixelRect.FromEdges(100, 100, 101, 300), SelectionGeometry.Resize(start, HandleKind.Right, -200, 0, Screen).Rect);
        Assert.Equal(PixelRect.FromEdges(100, 100, 1920, 1080), SelectionGeometry.Resize(start, HandleKind.BottomRight, 9999, 9999, Screen).Rect);
    }

    [Fact]
    public void Move_SlidesAlongTheFrameEdges()
    {
        var start = new PixelRect(1800, 1000, 100, 50);
        Assert.Equal(new PixelRect(1820, 1030, 100, 50), SelectionGeometry.Move(start, 500, 500, Screen));
        Assert.Equal(new PixelRect(-1920, 1000, 100, 50), SelectionGeometry.Move(start, -9000, 0, Screen));
    }
}
