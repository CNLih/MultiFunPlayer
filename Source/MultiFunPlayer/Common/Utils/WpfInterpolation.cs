using System.Windows;

namespace MultiFunPlayer.Common;

/// <summary>WPF-only adapter for interpolating editor control points.</summary>
internal static class WpfInterpolation
{
    public static double Linear(IList<Point> points, double x)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
            return 0;
        if (x < points[0].X)
            return points[0].Y;

        for (var i = 0; i + 1 < points.Count; i++)
        {
            var current = points[i];
            var next = points[i + 1];
            if (x >= current.X && x < next.X)
                return Interpolation.Linear(current.X, current.Y, next.X, next.Y, x);
        }

        return points[^1].Y;
    }
}
