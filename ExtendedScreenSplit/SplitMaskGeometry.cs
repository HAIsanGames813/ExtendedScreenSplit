using System.Numerics;
using Vortice;

namespace YMM4.ExtendedScreenSplit;

internal readonly record struct SplitMaskGeometry(IReadOnlyList<Vector2> Points, Vector2 Centroid, float Width, float Height);

internal static class SplitMaskGeometryBuilder
{
    public static SplitMaskGeometry Create(float screenWidth, float screenHeight, int index, int count, double cropWidth,
        double centerX, double centerY, double rotationDegrees, SplitLayoutMode mode, IReadOnlyList<int>? gridColumns = null,
        IReadOnlyList<double>? cropWidths = null, double totalWidth = 0, IReadOnlyList<double>? gridMaximumWidths = null,
        IReadOnlyList<double>? gridRowHeights = null, bool autoCompress = false)
    {
        // Group controlled inputs may have different visible bounds. Build every mask
        // against the project canvas so transparent margins cannot change the split.
        var halfWidth = screenWidth / 2f;
        var cx = (float)centerX;
        var cy = (float)centerY;
        var points = new List<Vector2>();
        if (mode == SplitLayoutMode.Grid)
        {
            if (gridColumns is null || !TryGetGridCell(index, gridColumns, out var row, out var column, out var columns))
                return new([], Vector2.Zero, 0, 0);
            var rows = gridColumns.Count;
            var rowMaximumWidth = gridMaximumWidths is not null && row < gridMaximumWidths.Count ? gridMaximumWidths[row] : 0;
            var rowTotalWidth = (float)(rowMaximumWidth > 0 ? Math.Min(totalWidth, rowMaximumWidth) : totalWidth);
            var rowStartIndex = gridColumns.Take(row).Sum();
            var rowWidths = ResolveWidths(rowStartIndex, columns, rowTotalWidth, cropWidths, autoCompress);
            var actualWidth = rowWidths.Sum();
            if (rowMaximumWidth > 0 && actualWidth > rowMaximumWidth && actualWidth > 0)
            {
                var scale = (float)(rowMaximumWidth / actualWidth);
                for (var i = 0; i < rowWidths.Length; i++) rowWidths[i] *= scale;
                actualWidth = (float)rowMaximumWidth;
            }
            var cellWidth = rowWidths[column];
            var rowHeights = ResolveWidths(0, rows, screenHeight, gridRowHeights, autoCompress);
            var actualHeight = rowHeights.Sum();
            var cellHeight = rowHeights[row];
            var left = -actualWidth / 2f;
            for (var previousColumn = 0; previousColumn < column; previousColumn++)
                left += rowWidths[previousColumn];
            var top = -actualHeight / 2f;
            for (var previousRow = 0; previousRow < row; previousRow++)
                top += rowHeights[previousRow];
            points.AddRange([new(left, top), new(left + cellWidth, top),
                new(left + cellWidth, top + cellHeight), new(left, top + cellHeight)]);
            TransformPoints(points, rotationDegrees, cx, cy);
        }
        else if (mode == SplitLayoutMode.Circular)
        {
            var totalAngle = Math.Clamp(totalWidth, 0, 360);
            var resolvedWidths = ResolveWidths(0, count, totalAngle, cropWidths, autoCompress);
            var start = rotationDegrees;
            var consumedAngle = 0.0;
            for (var previousIndex = 0; previousIndex < index; previousIndex++)
            {
                var previousSpan = Math.Min(resolvedWidths[previousIndex], Math.Max(0, 360 - consumedAngle));
                start += previousSpan;
                consumedAngle += previousSpan;
            }
            var span = (float)Math.Min(resolvedWidths[index], Math.Max(0, 360 - consumedAngle));
            var radius = MathF.Sqrt(screenWidth * screenWidth + screenHeight * screenHeight) * 2f;
            points.Add(new(cx, cy));
            const int steps = 96;
            for (var i = 0; i <= steps; i++)
            {
                var angle = (start + span * i / steps - 90.0) * Math.PI / 180.0;
                points.Add(new(cx + radius * (float)Math.Cos(angle), cy + radius * (float)Math.Sin(angle)));
            }
        }
        else
        {
            // Project all four screen corners onto the strip normal. Its span is
            // W*|cos(a)| + H*|sin(a)|, so the default width always partitions the
            // whole screen, including at diagonal angles and non-square aspect ratios.
            var angle = (float)(rotationDegrees * Math.PI / 180.0);
            var normal = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            var tangent = new Vector2(-normal.Y, normal.X);
            var projectedSpan = totalWidth > 0 ? (float)totalWidth : screenWidth * MathF.Abs(normal.X) + screenHeight * MathF.Abs(normal.Y);
            var resolvedWidths = ResolveWidths(0, count, projectedSpan, cropWidths, autoCompress);
            var width = resolvedWidths[index];
            var projectionStart = -projectedSpan / 2f;
            for (var previousIndex = 0; previousIndex < index; previousIndex++)
                projectionStart += resolvedWidths[previousIndex];
            var projectionCenter = projectionStart + width / 2f;
            var center = normal * (projectionCenter + cx * normal.X + cy * normal.Y) +
                tangent * (cx * tangent.X + cy * tangent.Y);
            var halfLength = MathF.Sqrt(screenWidth * screenWidth + screenHeight * screenHeight) * 2f;
            points.Add(center - normal * width / 2f - tangent * halfLength);
            points.Add(center + normal * width / 2f - tangent * halfLength);
            points.Add(center + normal * width / 2f + tangent * halfLength);
            points.Add(center - normal * width / 2f + tangent * halfLength);
        }

        points = ClipToCanvas(points, -screenWidth / 2f, -screenHeight / 2f, screenWidth / 2f, screenHeight / 2f);
        if (points.Count < 3) return new(points, Vector2.Zero, 0, 0);
        // Auto-center on the center of the mask's axis-aligned width/height box.
        // This keeps the item centered in the visible rectangular footprint even
        // when the actual mask is triangular or otherwise asymmetric.
        var minX = points.Min(p => p.X);
        var maxX = points.Max(p => p.X);
        var minY = points.Min(p => p.Y);
        var maxY = points.Max(p => p.Y);
        return new(points, new Vector2((minX + maxX) / 2f, (minY + maxY) / 2f), maxX - minX, maxY - minY);
    }

    private static float[] ResolveWidths(int startIndex, int count, double totalWidth,
        IReadOnlyList<double>? overrides, bool autoCompress)
    {
        var autoCount = 0;
        var fixedWidth = 0.0;
        for (var i = 0; i < count; i++)
        {
            var width = overrides is not null && startIndex + i < overrides.Count ? overrides[startIndex + i] : 0;
            if (width > 0) fixedWidth += width;
            else autoCount++;
        }
        var autoWidth = autoCompress && autoCount > 0
            ? Math.Max(0, (totalWidth - fixedWidth) / autoCount)
            : totalWidth / count;
        return Enumerable.Range(0, count)
            .Select(i =>
            {
                var width = overrides is not null && startIndex + i < overrides.Count ? overrides[startIndex + i] : 0;
                return (float)(width > 0 ? width : autoWidth);
            })
            .ToArray();
    }

    private static bool TryGetGridCell(int index, IReadOnlyList<int> gridColumns, out int row, out int column, out int columns)
    {
        row = 0;
        column = 0;
        columns = 1;
        foreach (var rowColumns in gridColumns)
        {
            var safeColumns = Math.Clamp(rowColumns, 1, ExtendedScreenSplitEffect.MaximumItems);
            if (index < safeColumns)
            {
                column = index;
                columns = safeColumns;
                return true;
            }
            index -= safeColumns;
            row++;
        }
        return false;
    }

    private static void TransformPoints(List<Vector2> points, double rotationDegrees, float cx, float cy)
    {
        var transform = Matrix3x2.CreateRotation((float)(rotationDegrees * Math.PI / 180.0));
        for (var i = 0; i < points.Count; i++)
            points[i] = Vector2.Transform(points[i], transform) + new Vector2(cx, cy);
    }

    private static List<Vector2> ClipToCanvas(List<Vector2> polygon, float left, float top, float right, float bottom)
    {
        polygon = Clip(polygon, p => p.X >= left, (a, b) => IntersectX(a, b, left));
        polygon = Clip(polygon, p => p.X <= right, (a, b) => IntersectX(a, b, right));
        polygon = Clip(polygon, p => p.Y >= top, (a, b) => IntersectY(a, b, top));
        return Clip(polygon, p => p.Y <= bottom, (a, b) => IntersectY(a, b, bottom));
    }

    private static List<Vector2> Clip(List<Vector2> polygon, Func<Vector2, bool> inside, Func<Vector2, Vector2, Vector2> intersect)
    {
        if (polygon.Count == 0) return polygon;
        var output = new List<Vector2>();
        var previous = polygon[^1];
        var previousInside = inside(previous);
        foreach (var current in polygon)
        {
            var currentInside = inside(current);
            if (currentInside != previousInside) output.Add(intersect(previous, current));
            if (currentInside) output.Add(current);
            previous = current;
            previousInside = currentInside;
        }
        return output;
    }

    private static Vector2 IntersectX(Vector2 a, Vector2 b, float x)
    {
        var t = (x - a.X) / (b.X - a.X);
        return new(x, a.Y + (b.Y - a.Y) * t);
    }

    private static Vector2 IntersectY(Vector2 a, Vector2 b, float y)
    {
        var t = (y - a.Y) / (b.Y - a.Y);
        return new(a.X + (b.X - a.X) * t, y);
    }

}
