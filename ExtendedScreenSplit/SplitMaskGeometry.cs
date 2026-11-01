using System.Numerics;
using Vortice;

namespace YMM4.ExtendedScreenSplit;

internal readonly record struct SplitMaskGeometry(IReadOnlyList<Vector2> Points, Vector2 Centroid, float Width, float Height);
internal readonly record struct SplitDividerLine(Vector2 Start, Vector2 End);

internal static class SplitMaskGeometryBuilder
{
    public static IReadOnlyList<SplitDividerLine> CreateDividers(float screenWidth, float screenHeight, int count,
        double centerX, double centerY, double rotationDegrees, SplitLayoutMode mode,
        IReadOnlyList<int>? gridColumns = null, IReadOnlyList<double>? cropWidths = null, double totalWidth = 0,
        IReadOnlyList<double>? gridMaximumWidths = null, IReadOnlyList<double>? gridRowHeights = null, bool autoCompress = false,
        SplitAlignmentReference linearAlignment = SplitAlignmentReference.Center,
        IReadOnlyList<double>? gridXOffsets = null, IReadOnlyList<SplitAlignmentReference>? gridAlignments = null,
        double layoutWidth = 0, double layoutHeight = 0)
    {
        var lines = new List<SplitDividerLine>();
        var cx = (float)centerX;
        var cy = (float)centerY;
        var effectiveWidth = layoutWidth > 0 ? Math.Min(screenWidth, (float)layoutWidth) : screenWidth;
        var effectiveHeight = layoutHeight > 0 ? Math.Min(screenHeight, (float)layoutHeight) : screenHeight;
        var diagonal = MathF.Sqrt(effectiveWidth * effectiveWidth + effectiveHeight * effectiveHeight) * 2f;
        if (mode == SplitLayoutMode.Circular)
        {
            var span = (float)Math.Clamp(totalWidth, 0, 360);
            var widths = ResolveWidths(0, count, span, cropWidths, autoCompress);
            var startOffset = linearAlignment switch
            {
                SplitAlignmentReference.Center => -span / 2f,
                SplitAlignmentReference.Right => -span,
                _ => 0f,
            };
            var consumed = 0f;
            var fullCircle = span >= 360f - 0.0001f && widths.Sum() >= 360f - 0.0001f;
            for (var i = 0; i < count; i++)
            {
                if ((i > 0 || fullCircle) && consumed < 360f - 0.0001f)
                {
                    var angle = (rotationDegrees + startOffset + consumed - 90.0) * Math.PI / 180.0;
                    lines.Add(new(new(cx, cy), new(cx + diagonal * (float)Math.Cos(angle), cy + diagonal * (float)Math.Sin(angle))));
                }
                consumed += Math.Min(widths[i], Math.Max(0, 360f - consumed));
            }
        }
        else if (mode == SplitLayoutMode.Linear)
        {
            var radians = (float)(rotationDegrees * Math.PI / 180.0);
            var normal = new Vector2(MathF.Cos(radians), MathF.Sin(radians));
            var tangent = new Vector2(-normal.Y, normal.X);
            var span = totalWidth > 0
                ? (float)totalWidth
                : effectiveWidth * MathF.Abs(normal.X) + effectiveHeight * MathF.Abs(normal.Y);
            var widths = ResolveWidths(0, count, span, cropWidths, autoCompress);
            var position = GetAlignedStart(widths.Sum(), span, linearAlignment);
            for (var i = 0; i < count - 1; i++)
            {
                position += widths[i];
                var center = normal * position + new Vector2(cx, cy);
                lines.Add(new(center - tangent * diagonal, center + tangent * diagonal));
            }
        }
        else if (gridColumns is not null && gridColumns.Count > 0)
        {
            var rows = Math.Min(gridColumns.Count, ExtendedScreenSplitEffect.MaximumItems);
            var rowHeights = ResolveGridRowHeights(rows, effectiveHeight, gridRowHeights, autoCompress);
            var rowWidths = new float[rows][];
            var rowActualWidths = new float[rows];
            for (var row = 0; row < rows; row++)
            {
                var columns = Math.Clamp(gridColumns[row], 1, ExtendedScreenSplitEffect.MaximumItems);
                var maximum = gridMaximumWidths is not null && row < gridMaximumWidths.Count ? gridMaximumWidths[row] : 0;
                var rowSpan = (float)(maximum > 0 ? Math.Min(totalWidth, maximum) : totalWidth);
                rowWidths[row] = ResolveWidths(gridColumns.Take(row).Sum(), columns, rowSpan, cropWidths, autoCompress);
                var actual = rowWidths[row].Sum();
                if (maximum > 0 && actual > maximum && actual > 0)
                {
                    var scale = (float)(maximum / actual);
                    for (var col = 0; col < rowWidths[row].Length; col++) rowWidths[row][col] *= scale;
                    actual = (float)maximum;
                }
                rowActualWidths[row] = actual;
            }

            var totalHeight = rowHeights.Sum();
            var top = -totalHeight / 2f;
            for (var row = 0; row < rows; row++)
            {
                var widths = rowWidths[row];
                var alignment = gridAlignments is not null && row < gridAlignments.Count ? gridAlignments[row] : SplitAlignmentReference.Center;
                var xOffset = gridXOffsets is not null && row < gridXOffsets.Count ? (float)gridXOffsets[row] : 0;
                var left = GetAlignedStart(rowActualWidths[row], (float)totalWidth, alignment) + xOffset;
                for (var col = 0; col < widths.Length - 1; col++)
                {
                    left += widths[col];
                    AddTransformedLine(lines, new(left, top), new(left, top + rowHeights[row]), rotationDegrees, cx, cy);
                }
                top += rowHeights[row];
                if (row < rows - 1)
                {
                    var nextAlignment = gridAlignments is not null && row + 1 < gridAlignments.Count ? gridAlignments[row + 1] : SplitAlignmentReference.Center;
                    var nextXOffset = gridXOffsets is not null && row + 1 < gridXOffsets.Count ? (float)gridXOffsets[row + 1] : 0;
                    var currentLeft = GetAlignedStart(rowActualWidths[row], (float)totalWidth, alignment) + xOffset;
                    var currentRight = currentLeft + rowActualWidths[row];
                    var nextLeft = GetAlignedStart(rowActualWidths[row + 1], (float)totalWidth, nextAlignment) + nextXOffset;
                    var nextRight = nextLeft + rowActualWidths[row + 1];
                    var seamLeft = Math.Max(currentLeft, nextLeft);
                    var seamRight = Math.Min(currentRight, nextRight);
                    if (seamRight > seamLeft)
                        AddTransformedLine(lines, new(seamLeft, top), new(seamRight, top), rotationDegrees, cx, cy);
                }
            }
        }
        return lines
            .Select(line => TryClipLine(line, -effectiveWidth / 2f, -effectiveHeight / 2f, effectiveWidth / 2f, effectiveHeight / 2f, out var clipped)
                ? clipped
                : (SplitDividerLine?)null)
            .Where(line => line.HasValue)
            .Select(line => line!.Value)
            .ToArray();
    }

    private static bool TryClipLine(SplitDividerLine line, float left, float top, float right, float bottom, out SplitDividerLine clipped)
    {
        var delta = line.End - line.Start;
        var t0 = 0f;
        var t1 = 1f;
        if (!ClipTest(-delta.X, line.Start.X - left, ref t0, ref t1) ||
            !ClipTest(delta.X, right - line.Start.X, ref t0, ref t1) ||
            !ClipTest(-delta.Y, line.Start.Y - top, ref t0, ref t1) ||
            !ClipTest(delta.Y, bottom - line.Start.Y, ref t0, ref t1))
        {
            clipped = default;
            return false;
        }
        clipped = new(line.Start + delta * t0, line.Start + delta * t1);
        return true;
    }

    private static bool ClipTest(float p, float q, ref float t0, ref float t1)
    {
        if (MathF.Abs(p) < float.Epsilon) return q >= 0;
        var ratio = q / p;
        if (p < 0)
        {
            if (ratio > t1) return false;
            if (ratio > t0) t0 = ratio;
        }
        else
        {
            if (ratio < t0) return false;
            if (ratio < t1) t1 = ratio;
        }
        return true;
    }

    private static void AddTransformedLine(List<SplitDividerLine> lines, Vector2 start, Vector2 end,
        double rotationDegrees, float cx, float cy)
    {
        var transform = Matrix3x2.CreateRotation((float)(rotationDegrees * Math.PI / 180.0));
        var offset = new Vector2(cx, cy);
        lines.Add(new(Vector2.Transform(start, transform) + offset, Vector2.Transform(end, transform) + offset));
    }

    private static float GetAlignedStart(float contentWidth, float referenceWidth, SplitAlignmentReference alignment) =>
        alignment switch
        {
            SplitAlignmentReference.Left => -referenceWidth / 2f,
            SplitAlignmentReference.Right => referenceWidth / 2f - contentWidth,
            _ => -contentWidth / 2f,
        };

    public static SplitMaskGeometry Create(float screenWidth, float screenHeight, int index, int count, double cropWidth,
        double centerX, double centerY, double rotationDegrees, SplitLayoutMode mode, IReadOnlyList<int>? gridColumns = null,
        IReadOnlyList<double>? cropWidths = null, double totalWidth = 0, IReadOnlyList<double>? gridMaximumWidths = null,
        IReadOnlyList<double>? gridRowHeights = null, bool autoCompress = false,
        SplitAlignmentReference linearAlignment = SplitAlignmentReference.Center,
        IReadOnlyList<double>? gridXOffsets = null, IReadOnlyList<SplitAlignmentReference>? gridAlignments = null,
        double layoutWidth = 0, double layoutHeight = 0)
    {
        // Group controlled inputs may have different visible bounds. Build every mask
        // against the same centered layout rectangle so transparent margins cannot change the split.
        // Items beyond the configured split count reuse slots from the beginning.
        // Normalize here as well as in the processor so every layout mode (including
        // circular and linear) keeps the same cyclic behavior.
        if (count <= 0)
            return new([], Vector2.Zero, 0, 0);
        index = ((index % count) + count) % count;
        var effectiveWidth = layoutWidth > 0 ? Math.Min(screenWidth, (float)layoutWidth) : screenWidth;
        var effectiveHeight = layoutHeight > 0 ? Math.Min(screenHeight, (float)layoutHeight) : screenHeight;
        var cx = (float)centerX;
        var cy = (float)centerY;
        var points = new List<Vector2>();
        if (mode == SplitLayoutMode.Grid)
        {
            if (gridColumns is null || !TryGetGridCell(index, gridColumns, out var row, out var column, out var columns))
                return new([], Vector2.Zero, 0, 0);
            var rows = gridColumns.Count;
            var rowMaximumWidth = gridMaximumWidths is not null && row < gridMaximumWidths.Count ? gridMaximumWidths[row] : 0;
            var rowXOffset = gridXOffsets is not null && row < gridXOffsets.Count ? (float)gridXOffsets[row] : 0;
            var rowAlignment = gridAlignments is not null && row < gridAlignments.Count ? gridAlignments[row] : SplitAlignmentReference.Center;
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
            var rowHeights = ResolveGridRowHeights(rows, effectiveHeight, gridRowHeights, autoCompress);
            var actualHeight = rowHeights.Sum();
            var cellHeight = rowHeights[row];
            var left = GetAlignedStart(actualWidth, (float)totalWidth, rowAlignment) + rowXOffset;
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
            var startOffset = linearAlignment switch
            {
                SplitAlignmentReference.Center => -totalAngle / 2.0,
                SplitAlignmentReference.Right => -totalAngle,
                _ => 0.0,
            };
            var start = rotationDegrees + startOffset;
            var consumedAngle = 0.0;
            for (var previousIndex = 0; previousIndex < index; previousIndex++)
            {
                var previousSpan = Math.Min(resolvedWidths[previousIndex], Math.Max(0, 360 - consumedAngle));
                start += previousSpan;
                consumedAngle += previousSpan;
            }
            var span = (float)Math.Min(resolvedWidths[index], Math.Max(0, 360 - consumedAngle));
            var radius = MathF.Sqrt(effectiveWidth * effectiveWidth + effectiveHeight * effectiveHeight) * 2f;
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
            var projectedSpan = totalWidth > 0 ? (float)totalWidth : effectiveWidth * MathF.Abs(normal.X) + effectiveHeight * MathF.Abs(normal.Y);
            var resolvedWidths = ResolveWidths(0, count, projectedSpan, cropWidths, autoCompress);
            var width = resolvedWidths[index];
            var projectionStart = GetAlignedStart(resolvedWidths.Sum(), projectedSpan, linearAlignment);
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

        points = ClipToCanvas(points, -effectiveWidth / 2f, -effectiveHeight / 2f, effectiveWidth / 2f, effectiveHeight / 2f);
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

    private static float[] ResolveGridRowHeights(int rowCount, float layoutHeight,
        IReadOnlyList<double>? requestedHeights, bool autoCompress)
    {
        var heights = ResolveWidths(0, rowCount, layoutHeight, requestedHeights, autoCompress);
        var totalHeight = heights.Sum();
        if (totalHeight > layoutHeight && totalHeight > 0)
        {
            var scale = layoutHeight / totalHeight;
            for (var row = 0; row < heights.Length; row++)
                heights[row] *= scale;
        }
        return heights;
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
