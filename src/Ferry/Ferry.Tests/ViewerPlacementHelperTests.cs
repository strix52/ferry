using System;
using Ferry;
using Xunit;

namespace Ferry.Tests;

public class ViewerPlacementHelperTests
{
    [Fact]
    public void Compute_100PercentDpi_WithinBoundsAndCentered()
    {
        var b = ViewerPlacementHelper.Compute(0, 0, 1920, 1040, 1.0, 1.0);
        Assert.True(b.Width <= 1920 && b.Width >= 320, "100% DPI Width within bounds");
        Assert.True(b.Height <= 1040 && b.Height >= 240, "100% DPI Height within bounds");
        Assert.Equal((1920 - b.Width) / 2, b.Left);
    }

    [Fact]
    public void Compute_125PercentDpi_WithinWorkArea()
    {
        var b = ViewerPlacementHelper.Compute(0, 0, 1920, 1040, 1.25, 1.25);
        Assert.True(b.Width <= 1536, "125% DPI Width <= workArea");
        Assert.True(b.Height <= 832, "125% DPI Height <= workArea");
    }

    [Fact]
    public void Compute_150PercentDpi_WithinWorkArea()
    {
        var b = ViewerPlacementHelper.Compute(0, 0, 3840, 2120, 1.5, 1.5);
        Assert.True(b.Width <= 2560, "150% DPI Width within workArea");
        Assert.True(b.Height <= 1413, "150% DPI Height within workArea");
    }

    [Fact]
    public void Compute_NegativeOriginMonitor_LeftStaysWithinMonitor()
    {
        var b = ViewerPlacementHelper.Compute(-1920, 0, 1920, 1040, 1.0, 1.0);
        Assert.True(b.Left >= -1920 && b.Left + b.Width <= 0, "Negative origin Left stays within monitor");
    }

    [Fact]
    public void ComputeFallback_ConstrainedWorkArea_BoundsContained()
    {
        var b = ViewerPlacementHelper.ComputeFallback(0, 0, 640, 480);
        Assert.True(b.Width <= 640 && b.Width >= 320, "Constrained 640x480 Width <= 640");
        Assert.True(b.Height <= 480 && b.Height >= 240, "Constrained 640x480 Height <= 480");
        Assert.True(b.Left >= 0 && b.Left + b.Width <= 640, "Constrained 640x480 Left >= 0");
        Assert.True(b.Top >= 0 && b.Top + b.Height <= 480, "Constrained 640x480 Top >= 0");
    }

    [Fact]
    public void ComputeFallback_TinyWorkArea_BoundsContained()
    {
        var b = ViewerPlacementHelper.ComputeFallback(0, 0, 400, 300);
        Assert.True(b.Width <= 400, "Tiny 400x300 Width <= 400");
        Assert.True(b.Height <= 300, "Tiny 400x300 Height <= 300");
    }

    [Fact]
    public void ResolveBounds_ScreenMetricsAvailable_Succeeds()
    {
        var r = ViewerPlacementHelper.ResolveBounds(
            getScreenMetrics: () => (0, 0, 1920, 1040, 1.0, 1.0),
            getWorkAreaMetrics: () => null);
        Assert.True(r.HasValue && r.Value.Width <= 1920, "ResolveBounds Screen path succeeds");
    }

    [Fact]
    public void ResolveBounds_ScreenMetricsFail_WorkAreaFallbackSucceeds()
    {
        var r = ViewerPlacementHelper.ResolveBounds(
            getScreenMetrics: () => throw new InvalidOperationException("Screen query failed"),
            getWorkAreaMetrics: () => (0, 0, 1280, 800));
        Assert.True(r.HasValue && r.Value.Width <= 1280 && r.Value.Height <= 800, "ResolveBounds WorkArea fallback succeeds");
    }

    [Fact]
    public void ResolveBounds_NoMetricsAvailable_FailsClosed()
    {
        var r = ViewerPlacementHelper.ResolveBounds(
            getScreenMetrics: () => null,
            getWorkAreaMetrics: () => null);
        Assert.False(r.HasValue, "ResolveBounds returns null when no metrics are available");
    }

    [Fact]
    public void ResolveBounds_AllMetricsThrowOrZero_FailsClosed()
    {
        var r = ViewerPlacementHelper.ResolveBounds(
            getScreenMetrics: () => throw new Exception("crash"),
            getWorkAreaMetrics: () => (0, 0, 0, 0));
        Assert.False(r.HasValue, "ResolveBounds returns null on terminal failure");
    }
}
