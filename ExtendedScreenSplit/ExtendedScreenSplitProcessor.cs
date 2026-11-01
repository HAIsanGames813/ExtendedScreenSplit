using System.Numerics;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Player.Video.Effects;

namespace YMM4.ExtendedScreenSplit;

internal sealed class ExtendedScreenSplitProcessor : VideoEffectProcessorBase
{
    private readonly record struct MaskCacheKey(
        float ScreenWidth, float ScreenHeight, int Index, int Count, double Width, int WidthsHash, double TotalWidth, double X, double Y, double Angle,
        SplitLayoutMode Mode, float DrawX, float DrawY, float ZoomX, float ZoomY, float RotationZ,
        float CenterPointX, float CenterPointY, bool Invert);
    private readonly record struct OverlayCacheKey(MaskCacheKey Transform, int GroupCount, int SlotCount, int LayoutHash, int ColorArgb);

    private readonly ExtendedScreenSplitEffect item;
    private ID2D1DeviceContext6 context = null!;
    private AlphaMask? alphaMask;
    private Composite? composite;
    private ID2D1CommandList? maskCommandList;
    private ID2D1CommandList? placementOverlay;
    private ID2D1CommandList? emptyOverlay;
    private IDWriteFactory? writeFactory;
    private MaskCacheKey? lastMaskKey;
    private OverlayCacheKey? lastOverlayKey;

    public ExtendedScreenSplitProcessor(IGraphicsDevicesAndContext devices, ExtendedScreenSplitEffect item) : base(devices)
    {
        this.item = item;
        context = devices.DeviceContext;
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
        composite.SetInput(1, emptyOverlay, true);
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
        lastMaskKey = null;
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
        var index = description.GroupIndex;
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
        var autoCompress = item.AutoCompress;
        var cropWidths = Enumerable.Range(0, slotCount)
            .Select(i =>
            {
                if (!item.UseIndividualSettings) return 0;
                var setting = item.ItemSettings.FirstOrDefault(x => x.ItemNumber == i);
                if (setting is null) return 0;
                return setting.WidthUnit == SplitWidthUnit.Proportion
                    ? setting.ProportionalCropWidth.GetValue(frame, duration, fps) * totalWidth / 100.0
                    : setting.CropWidth.GetValue(frame, duration, fps);
            })
            .ToArray();
        var widthsHasher = new HashCode();
        foreach (var columns in gridColumns) widthsHasher.Add(columns);
        foreach (var maximumWidth in gridMaximumWidths) widthsHasher.Add(maximumWidth);
        foreach (var height in gridRowHeights) widthsHasher.Add(height);
        foreach (var width in cropWidths) widthsHasher.Add(width);
        widthsHasher.Add(autoCompress);
        var widthsHash = widthsHasher.ToHashCode();
        var settings = item.UseIndividualSettings ? item.ItemSettings.FirstOrDefault(x => x.ItemNumber == index) : null;
        var zoomPercent = settings?.ZoomPercent.GetValue(frame, duration, fps) ?? 100;
        var offsetX = settings?.OffsetX.GetValue(frame, duration, fps) ?? 0;
        var offsetY = settings?.OffsetY.GetValue(frame, duration, fps) ?? 0;
        var rotation = settings?.Rotation.GetValue(frame, duration, fps) ?? 0;
        var centerX = item.CenterX.GetValue(frame, duration, fps);
        var centerY = item.CenterY.GetValue(frame, duration, fps);
        var cropWidth = settings is null ? 0 : settings.WidthUnit == SplitWidthUnit.Proportion
            ? settings.ProportionalCropWidth.GetValue(frame, duration, fps) * totalWidth / 100.0
            : settings.CropWidth.GetValue(frame, duration, fps);
        var maskStateHasher = new HashCode();
        maskStateHasher.Add(layoutMode);
        maskStateHasher.Add(item.SplitCount.GetValue(frame, duration, fps));
        maskStateHasher.Add(count);
        maskStateHasher.Add(item.CircularTotalAngle.GetValue(frame, duration, fps));
        maskStateHasher.Add(item.TotalWidth.GetValue(frame, duration, fps));
        maskStateHasher.Add(totalWidth);
        maskStateHasher.Add(widthsHash);
        maskStateHasher.Add(centerX);
        maskStateHasher.Add(centerY);
        maskStateHasher.Add(rotationOffset);
        maskStateHasher.Add(item.UseIndividualSettings);
        maskStateHasher.Add(item.AutoCompress);
        maskStateHasher.Add(item.ShowPlacementNumbers);
        maskStateHasher.Add(item.PlacementNumberColor);
        maskStateHasher.Add(item.GridRowCount);
        foreach (var row in item.GridRows)
        {
            maskStateHasher.Add(row.Columns);
            maskStateHasher.Add(row.MaximumWidth.GetValue(frame, duration, fps));
            maskStateHasher.Add(row.HeightUnit);
            maskStateHasher.Add(row.HeightPixels.GetValue(frame, duration, fps));
            maskStateHasher.Add(row.HeightProportion.GetValue(frame, duration, fps));
        }
        foreach (var setting in item.ItemSettings)
        {
            maskStateHasher.Add(setting.ItemNumber);
            maskStateHasher.Add(setting.OffsetX.GetValue(frame, duration, fps));
            maskStateHasher.Add(setting.OffsetY.GetValue(frame, duration, fps));
            maskStateHasher.Add(setting.ZoomPercent.GetValue(frame, duration, fps));
            maskStateHasher.Add(setting.Rotation.GetValue(frame, duration, fps));
            maskStateHasher.Add(setting.CropWidth.GetValue(frame, duration, fps));
            maskStateHasher.Add(setting.ProportionalCropWidth.GetValue(frame, duration, fps));
            maskStateHasher.Add(setting.WidthUnit);
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
            var mask = index < slotCount
                ? SplitMaskGeometryBuilder.Create(screenWidth, screenHeight, index, count, cropWidth, centerX, centerY, rotationOffset, layoutMode, gridColumns, cropWidths, totalWidth, gridMaximumWidths, gridRowHeights, autoCompress)
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

            var key = new MaskCacheKey(screenWidth, screenHeight, index, count, cropWidth, widthsHash, totalWidth, centerX, centerY, rotationOffset,
                layoutMode, updatedDraw.Draw.X, updatedDraw.Draw.Y, updatedDraw.Zoom.X, updatedDraw.Zoom.Y,
                updatedDraw.Rotation.Z, updatedDraw.CenterPoint.X, updatedDraw.CenterPoint.Y, updatedDraw.Invert);
            if (lastMaskKey != key)
            {
                if (TryCreateScreenToSourceTransform(updatedDraw, out var screenToSource))
                {
                    var localPoints = mask.Points.Select(point => Vector2.Transform(point, screenToSource)).ToArray();
                    ReplaceMask(localPoints);
                }
                else
                {
                    ReplaceMask([]);
                }
                lastMaskKey = key;
            }
            UpdatePlacementOverlay(description, updatedDraw, screenWidth, screenHeight, count, slotCount, gridColumns, cropWidths,
                gridMaximumWidths, gridRowHeights, totalWidth, layoutMode, autoCompress, frame, duration, fps, centerX, centerY, rotationOffset, key);
        }

        return updatedDraw;
    }

    private void UpdatePlacementOverlay(EffectDescription description, DrawDescription draw, float screenWidth, float screenHeight,
        int count, int slotCount, IReadOnlyList<int> gridColumns, IReadOnlyList<double> cropWidths,
        IReadOnlyList<double> gridMaximumWidths, IReadOnlyList<double> gridRowHeights, double totalWidth, SplitLayoutMode layoutMode,
        bool autoCompress,
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
            var width = settings is null ? 0 : settings.WidthUnit == SplitWidthUnit.Proportion
                ? settings.ProportionalCropWidth.GetValue(frame, duration, fps) * totalWidth / 100.0
                : settings.CropWidth.GetValue(frame, duration, fps);
            var mask = SplitMaskGeometryBuilder.Create(screenWidth, screenHeight, i, count, width,
                centerX, centerY, rotationOffset, layoutMode, gridColumns, cropWidths, totalWidth, gridMaximumWidths, gridRowHeights, autoCompress);
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
        composite?.SetInput(1, placementOverlay, true);
        lastOverlayKey = key;
    }

    private void ClearPlacementOverlay()
    {
        composite?.SetInput(1, emptyOverlay, true);
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
