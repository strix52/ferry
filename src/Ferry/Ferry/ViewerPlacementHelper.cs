namespace Ferry;

internal readonly record struct WindowBounds(double Left, double Top, double Width, double Height);

internal static class ViewerPlacementHelper
{
    private static WindowBounds CenterAndClamp(
        double workAreaLeft,
        double workAreaTop,
        double workAreaWidth,
        double workAreaHeight,
        double maxWidthDips,
        double maxHeightDips,
        double targetFraction)
    {
        double width = Math.Round(workAreaWidth * targetFraction);
        double height = Math.Round(workAreaHeight * targetFraction);

        if (!double.IsPositiveInfinity(maxWidthDips))
        {
            width = Math.Min(maxWidthDips, width);
        }
        if (!double.IsPositiveInfinity(maxHeightDips))
        {
            height = Math.Min(maxHeightDips, height);
        }

        // Clamp dimensions so they never exceed the work area, and respect minimum 320x240
        width = Math.Clamp(width, Math.Min(320.0, workAreaWidth), workAreaWidth);
        height = Math.Clamp(height, Math.Min(240.0, workAreaHeight), workAreaHeight);

        // Center within the monitor's DIP work area
        double left = workAreaLeft + (workAreaWidth - width) / 2.0;
        double top = workAreaTop + (workAreaHeight - height) / 2.0;

        // Defensive clamping against edges
        if (left + width > workAreaLeft + workAreaWidth)
        {
            left = workAreaLeft + workAreaWidth - width;
        }
        if (left < workAreaLeft)
        {
            left = workAreaLeft;
        }

        if (top + height > workAreaTop + workAreaHeight)
        {
            top = workAreaTop + workAreaHeight - height;
        }
        if (top < workAreaTop)
        {
            top = workAreaTop;
        }

        return new WindowBounds(left, top, width, height);
    }

    internal static WindowBounds Compute(
        int screenWorkingAreaLeft,
        int screenWorkingAreaTop,
        int screenWorkingAreaWidth,
        int screenWorkingAreaHeight,
        double dpiScaleX,
        double dpiScaleY,
        double targetFraction = 0.8)
    {
        double scaleX = dpiScaleX > 0 ? dpiScaleX : 1.0;
        double scaleY = dpiScaleY > 0 ? dpiScaleY : 1.0;

        // Convert physical screen pixels to WPF DIPs
        double dipLeft = screenWorkingAreaLeft / scaleX;
        double dipTop = screenWorkingAreaTop / scaleY;
        double dipWidth = screenWorkingAreaWidth / scaleX;
        double dipHeight = screenWorkingAreaHeight / scaleY;

        return CenterAndClamp(dipLeft, dipTop, dipWidth, dipHeight, double.PositiveInfinity, double.PositiveInfinity, targetFraction);
    }

    internal static WindowBounds ComputeFallback(
        double workAreaLeft,
        double workAreaTop,
        double workAreaWidth,
        double workAreaHeight,
        double targetFraction = 0.8)
    {
        return CenterAndClamp(workAreaLeft, workAreaTop, workAreaWidth, workAreaHeight, 800.0, 600.0, targetFraction);
    }

    internal static WindowBounds? ResolveBounds(
        Func<(int Left, int Top, int Width, int Height, double DpiX, double DpiY)?>? getScreenMetrics,
        Func<(double Left, double Top, double Width, double Height)?>? getWorkAreaMetrics)
    {
        if (getScreenMetrics != null)
        {
            try
            {
                var screen = getScreenMetrics();
                if (screen.HasValue && screen.Value.Width > 0 && screen.Value.Height > 0)
                {
                    var s = screen.Value;
                    return Compute(s.Left, s.Top, s.Width, s.Height, s.DpiX, s.DpiY);
                }
            }
            catch { }
        }

        if (getWorkAreaMetrics != null)
        {
            try
            {
                var wa = getWorkAreaMetrics();
                if (wa.HasValue && wa.Value.Width > 0 && wa.Value.Height > 0)
                {
                    var w = wa.Value;
                    return ComputeFallback(w.Left, w.Top, w.Width, w.Height);
                }
            }
            catch { }
        }

        return null;
    }
}
