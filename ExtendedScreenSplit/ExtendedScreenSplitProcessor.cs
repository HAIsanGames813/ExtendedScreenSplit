using System.Numerics;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Player.Video.Effects;
using YukkuriMovieMaker.Plugin.Brush;

namespace YMM4.ExtendedScreenSplit;

internal sealed class ExtendedScreenSplitProcessor : VideoEffectProcessorBase
{
    private readonly record struct MaskCacheKey(
        float ScreenWidth, float ScreenHeight, int Index, int Count, double Width, int WidthsHash, double TotalWidth, double X, double Y, double Angle,
        SplitLayoutMode Mode, float DrawX, float DrawY, float ZoomX, float ZoomY, float RotationZ,
        float CenterPointX, float CenterPointY, bool Invert, bool OutlineOnly);
    private readonly record struct OverlayCacheKey(MaskCacheKey Transform, int GroupCount, int SlotCount, int LayoutHash, int ColorArgb);
    private readonly record struct BorderCacheKey(MaskCacheKey Transform, int LineHash, double Thickness, double X, double Y, double Zoom, int BrushTypeHash);

    private readonly ExtendedScreenSplitEffect item;
    private IGraphicsDevicesAndContext devices = null!;
    private ID2D1DeviceContext6 context = null!;
    private AlphaMask? alphaMask;
    private Composite? composite;
    private Composite? overlayComposite;
    private GaussianBlur? borderBlurEffect;
    private Opacity? borderOpacityEffect;
    private IBrushSource? borderBrushSource;
    private Type? borderBrushType;
    private ID2D1CommandList? maskCommandList;
    private ID2D1CommandList? borderCommandList;
    private ID2D1CommandList? placementOverlay;
    private ID2D1CommandList? emptyOverlay;
    private IDWriteFactory? writeFactory;
    private MaskCacheKey? lastMaskKey;
    private OverlayCacheKey? lastOverlayKey;
    private BorderCacheKey? lastBorderKey;

    public ExtendedScreenSplitProcessor(IGraphicsDevicesAndContext devices, ExtendedScreenSplitEffect item) : base(devices)
    {
        this.item = item;
        context = devices.DeviceContext;
        this.devices = devices;
    }

    protected override ID2D1Image? CreateEffect(IGraphicsDevicesAndContext devices)
    {
        // VideoEffectProcessorBase calls this virtual method from its constructor,
        // before this processor's constructor body assigns the context field.
        context = devices.DeviceContext;
        alphaMask = new AlphaMask(devices.DeviceContext);
        disposer.Collect(alphaMask);
        composite = new Composite(devices.DeviceContext) { Mode = CompositeMode.SourceOver };
        disposer.Collect(composite);
        using var alphaOutput = alphaMask.Output;
        composite.SetInput(0, alphaOutput, true);
        emptyOverlay = CreateTransparentCommandList();
        disposer.Collect(emptyOverlay);
        borderCommandList = CreateTransparentCommandList();
        disposer.Collect(borderCommandList);
        borderBlurEffect = new GaussianBlur(devices.DeviceContext);
        disposer.Collect(borderBlurEffect);
        borderBlurEffect.SetInput(0, borderCommandList, true);
        borderOpacityEffect = new Opacity(devices.DeviceContext);
        disposer.Collect(borderOpacityEffect);
        using (var blurOutput = borderBlurEffect.Output) borderOpacityEffect.SetInput(0, blurOutput, true);
        overlayComposite = new Composite(devices.DeviceContext) { Mode = CompositeMode.SourceOver };
        disposer.Collect(overlayComposite);
        using (var borderOutput = borderOpacityEffect.Output) overlayComposite.SetInput(0, borderOutput, true);
        overlayComposite.SetInput(1, emptyOverlay, true);
        using (var overlayOutput = overlayComposite.Output) composite.SetInput(1, overlayOutput, true);
        var output = composite.Output;
        disposer.Collect(output);
        return output;
    }

    protected override void setInput(ID2D1Image? input) => alphaMask?.SetInput(0, input, true);

    protected override void ClearEffectChain()
    {
        alphaMask?.SetInput(0, null, true);
        alphaMask?.SetInput(1, null, true);
        ClearPlacementOverlay();
        borderBlurEffect?.SetInput(0, emptyOverlay, true);
        if (borderBrushSource is not null) disposer.RemoveAndDispose(ref borderBrushSource);
        borderBrushType = null;
        lastMaskKey = null;
        lastBorderKey = null;
        lastOverlayKey = null;
    }

    public override DrawDescription Update(EffectDescription description)
    {
        var frame = description.ItemPosition.Frame;
        var duration = description.ItemDuration.Frame;
        var fps = description.FPS;
        var layoutMode = item.LayoutMode;
        var draw = description.DrawDescription.SetCustomValue(layoutMode, "ExtendedScreenSplit.LayoutMode");
        if (description.GroupCount > 0) item.EnsureGroupCount(description.GroupCount);
        var sourceIndex = description.GroupIndex;
        var screenWidth = (float)description.ScreenSize.Width;
        var screenHeight = (float)description.ScreenSize.Height;
        var rotationOffset = item.RotationOffset.GetValue(frame, duration, fps);
        var autoTotalWidth = layoutMode == SplitLayoutMode.Circular
            ? item.CircularTotalAngle.GetValue(frame, duration, fps)
            : layoutMode == SplitLayoutMode.Grid || screenWidth <= 0 || screenHeight <= 0
                ? screenWidth
                : screenWidth * Math.Abs(Math.Cos(rotationOffset * Math.PI / 180.0)) + screenHeight * Math.Abs(Math.Sin(rotationOffset * Math.PI / 180.0));
        var configuredTotalWidth = layoutMode == SplitLayoutMode.Circular
            ? autoTotalWidth
            : item.TotalWidth.GetValue(frame, duration, fps);
        var totalWidth = layoutMode == SplitLayoutMode.Circular
            ? Math.Clamp(configuredTotalWidth, 0, 360)
            : configuredTotalWidth > 0 ? configuredTotalWidth : autoTotalWidth;
        var gridColumns = layoutMode == SplitLayoutMode.Grid
            ? item.GridRows.Take(item.GridRowCount).Select(row => row.Columns).ToArray()
            : [];
        var gridMaximumWidths = layoutMode == SplitLayoutMode.Grid
            ? item.GridRows.Take(item.GridRowCount).Select(row => row.MaximumWidth.GetValue(frame, duration, fps)).ToArray()
            : [];
        var gridXOffsets = layoutMode == SplitLayoutMode.Grid
            ? item.GridRows.Take(item.GridRowCount).Select(row => row.XOffset.GetValue(frame, duration, fps)).ToArray()
            : [];
        var gridAlignments = layoutMode == SplitLayoutMode.Grid
            ? item.GridRows.Take(item.GridRowCount).Select(row => row.Alignment).ToArray()
            : [];
        var gridRowHeights = layoutMode == SplitLayoutMode.Grid
            ? item.GridRows.Take(item.GridRowCount).Select(row => row.HeightUnit == GridHeightUnit.Proportion
                ? row.HeightProportion.GetValue(frame, duration, fps) * screenHeight / 100.0
                : row.HeightPixels.GetValue(frame, duration, fps)).ToArray()
            : [];
        var count = layoutMode == SplitLayoutMode.Grid
            ? Math.Clamp(gridColumns.Sum(), 1, ExtendedScreenSplitEffect.MaximumItems)
            : Math.Clamp((int)Math.Round(item.SplitCount.GetValue(frame, duration, fps)), 1, ExtendedScreenSplitEffect.MaximumItems);
        var slotCount = layoutMode == SplitLayoutMode.Grid
            ? Math.Min(count, gridColumns.Sum())
            : count;
        var groupItemCount = Math.Clamp(Math.Max(description.GroupCount, slotCount), slotCount, ExtendedScreenSplitEffect.MaximumItems);
        var hasMoreItemsThanSlots = groupItemCount > slotCount;
        var arrangementOffsetValue = Math.Round(item.ArrangementOffset.GetValue(frame, duration, fps));
        var offsetRange = hasMoreItemsThanSlots ? groupItemCount : slotCount;
        var arrangementOffset = offsetRange > 0 ? (int)((arrangementOffsetValue % offsetRange + offsetRange) % offsetRange) : 0;
        var placementIndex = GetCyclicWindowSlotIndex(sourceIndex, arrangementOffset, groupItemCount, slotCount);
        var autoCompress = item.AutoCompress;
        var itemSettingsSnapshot = item.ItemSettings;
        var cropWidths = new double[slotCount];
        if (item.UseIndividualSettings)
        {
            for (var destinationSlot = 0; destinationSlot < slotCount; destinationSlot++)
            {
                var sourceItemIndex = (destinationSlot + arrangementOffset) % groupItemCount;
                var setting = GetItemSetting(itemSettingsSnapshot, sourceItemIndex);
                cropWidths[destinationSlot] = GetIndividualCropWidth(setting, totalWidth, frame, duration, fps);
            }
        }
        var widthsHasher = new HashCode();
        widthsHasher.Add(item.LinearAlignment);
        foreach (var columns in gridColumns) widthsHasher.Add(columns);
        foreach (var maximumWidth in gridMaximumWidths) widthsHasher.Add(maximumWidth);
        foreach (var xOffset in gridXOffsets) widthsHasher.Add(xOffset);
        foreach (var alignment in gridAlignments) widthsHasher.Add(alignment);
        foreach (var height in gridRowHeights) widthsHasher.Add(height);
        foreach (var width in cropWidths) widthsHasher.Add(width);
        widthsHasher.Add(autoCompress);
        var widthsHash = widthsHasher.ToHashCode();
        var settings = item.UseIndividualSettings ? GetItemSetting(itemSettingsSnapshot, sourceIndex) : null;
        var zoomPercent = settings?.ZoomPercent.GetValue(frame, duration, fps) ?? 100;
        var offsetX = settings?.OffsetX.GetValue(frame, duration, fps) ?? 0;
        var offsetY = settings?.OffsetY.GetValue(frame, duration, fps) ?? 0;
        var rotation = settings?.Rotation.GetValue(frame, duration, fps) ?? 0;
        var centerX = item.CenterX.GetValue(frame, duration, fps);
        var centerY = item.CenterY.GetValue(frame, duration, fps);
        var cropWidth = GetIndividualCropWidth(settings, totalWidth, frame, duration, fps);
        var maskStateHasher = new HashCode();
        maskStateHasher.Add(item.PreviewRevision);
        maskStateHasher.Add(layoutMode);
        maskStateHasher.Add(item.SplitCount.GetValue(frame, duration, fps));
        maskStateHasher.Add(arrangementOffset);
        maskStateHasher.Add(count);
        maskStateHasher.Add(item.CircularTotalAngle.GetValue(frame, duration, fps));
        maskStateHasher.Add(item.TotalWidth.GetValue(frame, duration, fps));
        maskStateHasher.Add(totalWidth);
        maskStateHasher.Add(widthsHash);
        maskStateHasher.Add(centerX);
        maskStateHasher.Add(centerY);
        maskStateHasher.Add(rotationOffset);
        maskStateHasher.Add(item.LinearAlignment);
        maskStateHasher.Add(item.UseIndividualSettings);
        maskStateHasher.Add(item.AutoCompress);
        maskStateHasher.Add(item.ShowPlacementNumbers);
        maskStateHasher.Add(item.PlacementNumberColor);
        maskStateHasher.Add(item.BorderThickness.GetValue(frame, duration, fps));
        maskStateHasher.Add(item.BorderBlur.GetValue(frame, duration, fps));
        maskStateHasher.Add(item.BorderX.GetValue(frame, duration, fps));
        maskStateHasher.Add(item.BorderY.GetValue(frame, duration, fps));
        maskStateHasher.Add(item.BorderOpacity.GetValue(frame, duration, fps));
        maskStateHasher.Add(item.BorderZoom.GetValue(frame, duration, fps));
        maskStateHasher.Add(item.OutlineOnly);
        maskStateHasher.Add(item.BorderBrush.Type);
        maskStateHasher.Add(item.GridRowCount);
        foreach (var row in item.GridRows.Take(item.GridRowCount))
        {
            maskStateHasher.Add(row.Columns);
            maskStateHasher.Add(row.MaximumWidth.GetValue(frame, duration, fps));
            maskStateHasher.Add(row.XOffset.GetValue(frame, duration, fps));
            maskStateHasher.Add(row.Alignment);
            maskStateHasher.Add(row.HeightUnit);
            maskStateHasher.Add(row.HeightPixels.GetValue(frame, duration, fps));
            maskStateHasher.Add(row.HeightProportion.GetValue(frame, duration, fps));
        }
        if (settings is not null)
        {
            maskStateHasher.Add(settings.ItemNumber);
            maskStateHasher.Add(offsetX);
            maskStateHasher.Add(offsetY);
            maskStateHasher.Add(zoomPercent);
            maskStateHasher.Add(rotation);
            maskStateHasher.Add(cropWidth);
            maskStateHasher.Add(settings.WidthUnit);
        }
        draw = draw.SetCustomValue(maskStateHasher.ToHashCode(), "ExtendedScreenSplit.MaskState");
        var autoOffset = Vector2.Zero;

        var percentScale = (float)(zoomPercent / 100.0);
        var updatedDraw = draw with
        {
            Draw = draw.Draw + new Vector3((float)(offsetX + autoOffset.X), (float)(offsetY + autoOffset.Y), 0),
            Zoom = draw.Zoom * new Vector2(percentScale, percentScale),
            Rotation = draw.Rotation + new Vector3(0, 0, (float)rotation),
        };

        if (input is not null && description.ScreenSize.Width > 0 && description.ScreenSize.Height > 0)
        {
            // Each source item keeps its own override even when multiple items
            // cycle into the same destination slot. A zero override restores the
            // automatic width for that item's slot; percentage values replace it.
            IReadOnlyList<double> itemCropWidths = cropWidths;
            if (placementIndex >= 0 && placementIndex < cropWidths.Length && cropWidths[placementIndex] != cropWidth)
            {
                var perItemWidths = cropWidths.ToArray();
                perItemWidths[placementIndex] = cropWidth;
                itemCropWidths = perItemWidths;
            }
            var mask = placementIndex >= 0 && slotCount > 0
                ? SplitMaskGeometryBuilder.Create(screenWidth, screenHeight, placementIndex, count, cropWidth, centerX, centerY, rotationOffset, layoutMode, gridColumns, itemCropWidths, totalWidth, gridMaximumWidths, gridRowHeights, autoCompress, item.LinearAlignment, gridXOffsets, gridAlignments)
                : new SplitMaskGeometry([], Vector2.Zero, 0, 0);
            // YMM4 applies Draw after the effect image. Always place the item at
            // the mask center, then inverse-map the screen mask to keep it fixed.
            autoOffset = mask.Centroid;
            updatedDraw = updatedDraw with { Draw = draw.Draw + new Vector3((float)(offsetX + autoOffset.X), (float)(offsetY + autoOffset.Y), 0) };

            if (layoutMode == SplitLayoutMode.Grid && mask.Width > 0 && mask.Height > 0)
            {
                var bounds = context.GetImageLocalBounds(input);
                var baseZoom = draw.Zoom * percentScale;
                var sourceWidth = (bounds.Right - bounds.Left) * MathF.Abs(baseZoom.X);
                var sourceHeight = (bounds.Bottom - bounds.Top) * MathF.Abs(baseZoom.Y);
                var itemRadians = (float)(draw.Rotation.Z + rotation) * MathF.PI / 180f;
                var cos = MathF.Abs(MathF.Cos(itemRadians));
                var sin = MathF.Abs(MathF.Sin(itemRadians));
                var rotatedWidth = sourceWidth * cos + sourceHeight * sin;
                var rotatedHeight = sourceWidth * sin + sourceHeight * cos;
                if (rotatedWidth > 0 && rotatedHeight > 0)
                {
                    var fit = Math.Max(mask.Width / rotatedWidth, mask.Height / rotatedHeight);
                    percentScale *= fit;
                    updatedDraw = updatedDraw with { Zoom = draw.Zoom * new Vector2(percentScale, percentScale) };
                }
            }

            var key = new MaskCacheKey(screenWidth, screenHeight, placementIndex, count, cropWidth, widthsHash, totalWidth, centerX, centerY, rotationOffset,
                layoutMode, updatedDraw.Draw.X, updatedDraw.Draw.Y, updatedDraw.Zoom.X, updatedDraw.Zoom.Y,
                updatedDraw.Rotation.Z, updatedDraw.CenterPoint.X, updatedDraw.CenterPoint.Y, updatedDraw.Invert, item.OutlineOnly);
            if (lastMaskKey != key)
            {
                if (TryCreateScreenToSourceTransform(updatedDraw, out var screenToSource))
                {
                    var localPoints = item.OutlineOnly ? [] : mask.Points.Select(point => Vector2.Transform(point, screenToSource)).ToArray();
                    ReplaceMask(localPoints);
                }
                else
                {
                    ReplaceMask([]);
                }
                lastMaskKey = key;
            }
            UpdateBorderOverlay(description, updatedDraw, screenWidth, screenHeight, count, slotCount, gridColumns, cropWidths,
                gridMaximumWidths, gridRowHeights, totalWidth, layoutMode, autoCompress, item.LinearAlignment, gridXOffsets, gridAlignments, frame, duration, fps,
                centerX, centerY, rotationOffset, key);
            UpdatePlacementOverlay(description, updatedDraw, screenWidth, screenHeight, count, slotCount, gridColumns, cropWidths,
                gridMaximumWidths, gridRowHeights, totalWidth, layoutMode, autoCompress, item.LinearAlignment, gridXOffsets, gridAlignments, frame, duration, fps, centerX, centerY, rotationOffset, key);
        }

        return updatedDraw;
    }

    private static int GetCyclicSlotIndex(int sourceIndex, int offset, int slotCount) =>
        slotCount <= 0 ? sourceIndex : (int)(((long)sourceIndex - offset % slotCount + slotCount) % slotCount);

    private static SplitItemSettings? GetItemSetting(IReadOnlyList<SplitItemSettings> settings, int itemNumber) =>
        itemNumber >= 0 && itemNumber < settings.Count && settings[itemNumber].ItemNumber == itemNumber
            ? settings[itemNumber]
            : null;

    private static int GetCyclicWindowSlotIndex(int sourceIndex, int offset, int groupItemCount, int slotCount)
    {
        if (groupItemCount <= slotCount)
            return GetCyclicSlotIndex(sourceIndex, offset, slotCount);
        if (groupItemCount <= 0 || slotCount <= 0)
            return -1;

        var relativeIndex = (int)(((long)sourceIndex - offset % groupItemCount + groupItemCount) % groupItemCount);
        return relativeIndex < slotCount ? relativeIndex : -1;
    }

    private static double GetIndividualCropWidth(SplitItemSettings? settings, double totalWidth, int frame, int duration, int fps)
    {
        if (settings is null) return 0;
        if (settings.WidthUnit == SplitWidthUnit.Proportion)
        {
            // The percentage is the item's replacement width as a fraction of
            // the total layout width, not an addition to the automatic width.
            var percentage = Math.Clamp(settings.ProportionalCropWidth.GetValue(frame, duration, fps), 0, 100);
            return percentage * totalWidth / 100.0;
        }
        return settings.CropWidth.GetValue(frame, duration, fps);
    }

    private void UpdateBorderOverlay(EffectDescription description, DrawDescription draw, float screenWidth, float screenHeight,
        int count, int slotCount, IReadOnlyList<int> gridColumns, IReadOnlyList<double> cropWidths,
        IReadOnlyList<double> gridMaximumWidths, IReadOnlyList<double> gridRowHeights, double totalWidth, SplitLayoutMode layoutMode,
        bool autoCompress, SplitAlignmentReference linearAlignment, IReadOnlyList<double> gridXOffsets,
        IReadOnlyList<SplitAlignmentReference> gridAlignments, int frame, int duration, int fps, double centerX, double centerY, double rotationOffset,
        MaskCacheKey transformKey)
    {
        var overlaySourceIndex = description.GroupCount == 0 ? 0 : description.GroupCount - 1;
        var thickness = item.BorderThickness.GetValue(frame, duration, fps);
        var blur = item.BorderBlur.GetValue(frame, duration, fps);
        var opacity = item.BorderOpacity.GetValue(frame, duration, fps);
        borderBlurEffect!.StandardDeviation = (float)blur;
        borderOpacityEffect!.Value = (float)(opacity / 100.0);
        if (thickness <= 0 || opacity <= 0 || slotCount <= 0 || description.GroupIndex != overlaySourceIndex ||
            !TryCreateScreenToSourceTransform(draw, out var screenToSource))
        {
            borderBlurEffect.SetInput(0, emptyOverlay, true);
            lastBorderKey = null;
            return;
        }

        var lines = SplitMaskGeometryBuilder.CreateDividers(screenWidth, screenHeight, count, centerX, centerY,
            rotationOffset, layoutMode, gridColumns, cropWidths, totalWidth, gridMaximumWidths, gridRowHeights, autoCompress,
            linearAlignment, gridXOffsets, gridAlignments);
        var lineHasher = new HashCode();
        foreach (var line in lines) { lineHasher.Add(line.Start); lineHasher.Add(line.End); }
        var brushType = item.BorderBrush.Type;
        if (borderBrushSource is null || borderBrushType != brushType)
        {
            if (borderBrushSource is not null) disposer.RemoveAndDispose(ref borderBrushSource);
            borderBrushSource = item.BorderBrush.CreateBrush(devices);
            borderBrushType = brushType;
            if (borderBrushSource is not null) disposer.Collect(borderBrushSource);
        }
        if (borderBrushSource is null) return;

        var sourceBounds = context.GetImageLocalBounds(input!);
        var brushUpdated = borderBrushSource.Update(new BrushSourceDescription(description, sourceBounds));
        var x = item.BorderX.GetValue(frame, duration, fps);
        var y = item.BorderY.GetValue(frame, duration, fps);
        var zoom = item.BorderZoom.GetValue(frame, duration, fps) / 100.0;
        var key = new BorderCacheKey(transformKey, lineHasher.ToHashCode(), thickness, x, y, zoom, brushType.GetHashCode());
        if (!brushUpdated && lastBorderKey == key) return;

        var next = context.CreateCommandList();
        disposer.Collect(next);
        using var oldTarget = context.Target;
        var oldTransform = context.Transform;
        context.Target = next;
        context.BeginDraw();
        context.Clear(new Color4(0, 0, 0, 0));
        var borderTransform = Matrix3x2.CreateScale((float)zoom) * Matrix3x2.CreateTranslation((float)x, (float)y) * screenToSource;
        context.Transform = borderTransform;
        foreach (var line in lines)
            context.DrawLine(line.Start, line.End, borderBrushSource.Brush, (float)thickness);
        context.EndDraw();
        context.Transform = oldTransform;
        context.Target = oldTarget;
        next.Close();
        borderBlurEffect.SetInput(0, next, true);
        var previous = borderCommandList;
        borderCommandList = next;
        if (previous is not null && !ReferenceEquals(previous, emptyOverlay)) disposer.RemoveAndDispose(ref previous);
        lastBorderKey = key;
    }

    private void UpdatePlacementOverlay(EffectDescription description, DrawDescription draw, float screenWidth, float screenHeight,
        int count, int slotCount, IReadOnlyList<int> gridColumns, IReadOnlyList<double> cropWidths,
        IReadOnlyList<double> gridMaximumWidths, IReadOnlyList<double> gridRowHeights, double totalWidth, SplitLayoutMode layoutMode,
        bool autoCompress, SplitAlignmentReference linearAlignment, IReadOnlyList<double> gridXOffsets,
        IReadOnlyList<SplitAlignmentReference> gridAlignments,
        int frame, int duration, int fps,
        double centerX, double centerY, double rotationOffset, MaskCacheKey transformKey)
    {
        var overlaySourceIndex = description.GroupCount == 0 ? 0 : description.GroupCount - 1;
        if (!item.ShowPlacementNumbers || description.Usage == TimelineSourceUsage.Exporting ||
            description.GroupIndex != overlaySourceIndex || slotCount <= 0 ||
            !TryCreateScreenToSourceTransform(draw, out var screenToSource))
        {
            ClearPlacementOverlay();
            return;
        }

        var layoutHash = new HashCode();
        foreach (var columns in gridColumns) layoutHash.Add(columns);
        for (var i = 0; i < slotCount; i++)
        {
            layoutHash.Add(cropWidths[i]);
        }
        foreach (var maximumWidth in gridMaximumWidths) layoutHash.Add(maximumWidth);
        foreach (var xOffset in gridXOffsets) layoutHash.Add(xOffset);
        foreach (var alignment in gridAlignments) layoutHash.Add(alignment);
        var color = item.PlacementNumberColor;
        var colorArgb = color.A << 24 | color.R << 16 | color.G << 8 | color.B;
        var key = new OverlayCacheKey(transformKey, description.GroupCount, slotCount, layoutHash.ToHashCode(), colorArgb);
        if (lastOverlayKey == key) return;

        ClearPlacementOverlay();
        writeFactory ??= DWrite.DWriteCreateFactory<IDWriteFactory>();
        disposer.Collect(writeFactory);
        placementOverlay = context.CreateCommandList();
        disposer.Collect(placementOverlay);

        using var textFormat = writeFactory.CreateTextFormat("Segoe UI", FontWeight.Bold, FontStyle.Normal, FontStretch.Normal, 48);
        textFormat.TextAlignment = TextAlignment.Center;
        textFormat.ParagraphAlignment = ParagraphAlignment.Center;
        using var shadowBrush = context.CreateSolidColorBrush(new Color4(0, 0, 0, 0.72f));
        using var textBrush = context.CreateSolidColorBrush(new Color4(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f * 0.86f));

        using var oldTarget = context.Target;
        var oldTransform = context.Transform;
        context.Target = placementOverlay;
        context.BeginDraw();
        context.Clear(new Color4(0, 0, 0, 0));
        context.Transform = screenToSource;
        for (var i = 0; i < slotCount; i++)
        {
            var settings = item.UseIndividualSettings ? item.ItemSettings.FirstOrDefault(x => x.ItemNumber == i) : null;
            var width = GetIndividualCropWidth(settings, totalWidth, frame, duration, fps);
            var mask = SplitMaskGeometryBuilder.Create(screenWidth, screenHeight, i, count, width,
                centerX, centerY, rotationOffset, layoutMode, gridColumns, cropWidths, totalWidth, gridMaximumWidths, gridRowHeights,
                autoCompress, linearAlignment, gridXOffsets, gridAlignments);
            if (mask.Points.Count < 3) continue;
            var fontSize = Math.Clamp(Math.Min(mask.Width, mask.Height) * 0.24f, 22f, 72f);
            var rect = new Rect(mask.Centroid.X - fontSize, mask.Centroid.Y - fontSize * 0.7f,
                fontSize * 2, fontSize * 1.4f);
            var shadowRect = new Rect(rect.Left - 2, rect.Top - 2, rect.Width + 4, rect.Height + 4);
            context.DrawText((i + 1).ToString(), textFormat, shadowRect, shadowBrush);
            context.DrawText((i + 1).ToString(), textFormat, rect, textBrush);
        }
        context.EndDraw();
        context.Transform = oldTransform;
        context.Target = oldTarget;
        placementOverlay.Close();
        overlayComposite?.SetInput(1, placementOverlay, true);
        lastOverlayKey = key;
    }

    private void ClearPlacementOverlay()
    {
        overlayComposite?.SetInput(1, emptyOverlay, true);
        if (placementOverlay is not null)
            disposer.RemoveAndDispose(ref placementOverlay);
        lastOverlayKey = null;
    }

    private ID2D1CommandList CreateTransparentCommandList()
    {
        var commandList = context.CreateCommandList();
        using var oldTarget = context.Target;
        var oldTransform = context.Transform;
        context.Target = commandList;
        context.Transform = Matrix3x2.Identity;
        context.BeginDraw();
        context.Clear(new Color4(0, 0, 0, 0));
        using (var transparentBrush = context.CreateSolidColorBrush(new Color4(0, 0, 0, 0)))
            context.FillRectangle(new RawRectF(0, 0, 1, 1), transparentBrush);
        context.EndDraw();
        context.Transform = oldTransform;
        context.Target = oldTarget;
        commandList.Close();
        return commandList;
    }

    private static bool TryCreateScreenToSourceTransform(DrawDescription draw, out Matrix3x2 screenToSource)
    {
        var transform = draw.Invert
            ? Matrix3x2.CreateScale(-1, 1, draw.CenterPoint)
            : Matrix3x2.Identity;
        transform *= Matrix3x2.CreateScale(draw.Zoom);
        transform *= Matrix3x2.CreateRotation(draw.Rotation.Z * MathF.PI / 180f);
        transform *= Matrix3x2.CreateTranslation(draw.Draw.X, draw.Draw.Y);
        return Matrix3x2.Invert(transform, out screenToSource);
    }

    private void ReplaceMask(IReadOnlyList<Vector2> points)
    {
        var oldMaskCommandList = maskCommandList;

        using var geometry = points.Count >= 3 ? context.Factory.CreatePathGeometry() : null;
        if (geometry is not null)
        {
            using var sink = geometry.Open();
            sink.BeginFigure(points[0], FigureBegin.Filled);
            for (var i = 1; i < points.Count; i++) sink.AddLine(points[i]);
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }

        // Keep AlphaMask's second input connected even for items with no matching
        // slot. A null input leaves the Direct2D graph incomplete during preview.
        var nextMaskCommandList = context.CreateCommandList();
        disposer.Collect(nextMaskCommandList);
        using var oldTarget = context.Target;
        var oldTransform = context.Transform;
        context.Target = nextMaskCommandList;
        context.Transform = Matrix3x2.Identity;
        context.BeginDraw();
        context.Clear(new Color4(0, 0, 0, 0));
        if (geometry is not null)
        {
            using var brush = context.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
            context.FillGeometry(geometry, brush);
        }
        context.EndDraw();
        context.Transform = oldTransform;
        context.Target = oldTarget;
        nextMaskCommandList.Close();
        alphaMask?.SetInput(1, nextMaskCommandList, true);
        maskCommandList = nextMaskCommandList;
        if (oldMaskCommandList is not null)
            disposer.RemoveAndDispose(ref oldMaskCommandList);
    }
}
