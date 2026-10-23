using System;
using System.Collections.Generic;
using HolyScreen.Capture;
using Xunit;

namespace HolyScreen.Tests;

public class PixelRectTests
{
    [Fact]
    public void FromPoints_NormalizesAnyCornerOrder()
    {
        Assert.Equal(new PixelRect(10, 20, 30, 40), PixelRect.FromPoints(40, 60, 10, 20));
        Assert.Equal(new PixelRect(10, 20, 30, 40), PixelRect.FromPoints(10, 60, 40, 20));
    }

    [Fact]
    public void Contains_IsHalfOpen()
    {
        var r = new PixelRect(0, 0, 10, 10);
        Assert.True(r.Contains(0, 0));
        Assert.True(r.Contains(9, 9));
        Assert.False(r.Contains(10, 9));
        Assert.False(r.Contains(9, 10));
    }

    [Fact]
    public void Intersect_ReturnsOverlapOrEmpty()
    {
        var a = new PixelRect(0, 0, 100, 100);
        Assert.Equal(new PixelRect(50, 60, 50, 40), a.Intersect(new PixelRect(50, 60, 200, 200)));
        Assert.True(a.Intersect(new PixelRect(100, 0, 10, 10)).IsEmpty); // touching edges do not overlap
    }

    [Fact]
    public void Union_CoversBoth_AndIgnoresEmpty()
    {
        var a = new PixelRect(-1920, 0, 1920, 1080);
        var b = new PixelRect(0, 0, 2560, 1440);
        Assert.Equal(new PixelRect(-1920, 0, 4480, 1440), a.Union(b));
        Assert.Equal(b, PixelRect.Empty.Union(b));
    }

    [Fact]
    public void MoveInside_KeepsSizeAndClamps()
    {
        var bounds = new PixelRect(0, 0, 1920, 1080);
        Assert.Equal(new PixelRect(1820, 980, 100, 100), new PixelRect(1900, 1000, 100, 100).MoveInside(bounds));
        Assert.Equal(new PixelRect(0, 0, 1920, 1080), new PixelRect(-50, -50, 5000, 5000).MoveInside(bounds));
    }

    [Fact]
    public void ArrayRoundTrip_AndInvalidArraysAreRejected()
    {
        var r = new PixelRect(-5, 7, 300, 200);
        Assert.Equal(r, PixelRect.FromArray(r.ToArray()));
        Assert.Null(PixelRect.FromArray(new[] { 1, 2, 0, 5 }));
        Assert.Null(PixelRect.FromArray(new[] { 1, 2, 3 }));
        Assert.Null(PixelRect.FromArray(null));
    }

    [Fact]
    public void DisplayLayout_FromPoint_FindsContainingOrNearestMonitor()
    {
        var left = new MonitorInfo(IntPtr.Zero, "L", new PixelRect(-1920, 0, 1920, 1080), new PixelRect(-1920, 0, 1920, 1040), 96, false);
        var right = new MonitorInfo(IntPtr.Zero, "R", new PixelRect(0, 0, 2560, 1440), new PixelRect(0, 0, 2560, 1400), 144, true);
        var monitors = new List<MonitorInfo> { left, right };

        Assert.Same(left, DisplayLayout.FromPoint(monitors, -1, 500));
        Assert.Same(right, DisplayLayout.FromPoint(monitors, 0, 500));
        Assert.Same(right, DisplayLayout.FromPoint(monitors, 100, 1300)); // inside the taller right monitor
        Assert.Same(left, DisplayLayout.FromPoint(monitors, -1500, 1300)); // in the gap below the left one
        Assert.Same(right, DisplayLayout.FromPoint(monitors, -100, 1200)); // gap, but 100 px from the right one
        Assert.Equal(1.5, right.Scale);
    }
}
