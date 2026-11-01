using System.Collections.Immutable;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Runtime.CompilerServices;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Controls;
using YukkuriMovieMaker.Exo;
using YukkuriMovieMaker.ItemEditor;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Player.Video.Effects;
using YukkuriMovieMaker.Plugin.Effects;

namespace YMM4.ExtendedScreenSplit;

public enum SplitLayoutMode
{
    [Display(Name = "円状")] Circular,
    [Display(Name = "線状")] Linear,
    [Display(Name = "グリッド")] Grid,
}

public enum SplitWidthUnit
{
    [Display(Name = "px / °")]
    Absolute,
    [Display(Name = "割合")]
    Proportion,
}

public enum GridHeightUnit
{
    [Display(Name = "ピクセル")]
    Pixel,
    [Display(Name = "割合")]
    Proportion,
}

[VideoEffect("グループ制御中のアイテムで画面分割(拡張)", ["整列"], ["グループ制御中のアイテムを分割し、自動で切り抜きます"], true, true,
    IsEffectItemSupported = false, IsAviUtlSupported = false)]
public sealed class ExtendedScreenSplitEffect : VideoEffectBase
{
    internal const int MaximumItems = 1000;

    private bool useIndividualSettings;
    private bool autoCompress;
    private bool showPlacementNumbers;
    private System.Windows.Media.Color placementNumberColor = System.Windows.Media.Colors.White;
    private SplitLayoutMode layoutMode;
    private int gridRowCount = 2;
    private readonly object settingsLock = new();
    private ImmutableList<SplitItemSettings> itemSettings = CreateDefaultSettings(1);
    private ImmutableList<GridRowSettings> gridRows = [new(0, 2), new(1, 2)];

    public override string Label => "グループ制御中のアイテムで画面分割(拡張)";

    [Display(GroupName = "分割", Name = "分割数")]
    [AnimationSlider("F0", "", 1, 50)]
    [YukkuriMovieMaker.ItemEditor.CustomVisibilityAttributes.ShowPropertyEditorWhen(nameof(IsNonGridMode), true)]
    public Animation SplitCount { get; } = new(4, 1, 50000000);

    [Display(GroupName = "分割", Name = "全体角度")]
    [AnimationSlider("F1", "°", 0, 360)]
    [YukkuriMovieMaker.ItemEditor.CustomVisibilityAttributes.ShowPropertyEditorWhen(nameof(IsCircularMode), true)]
    public Animation CircularTotalAngle { get; } = new(360, 0, 360);

    [Display(GroupName = "分割", Name = "全体幅", Description = "0 px の場合は画面全体の幅を使います")]
    [AnimationSlider("F1", "px", 0, 50)]
    [YukkuriMovieMaker.ItemEditor.CustomVisibilityAttributes.ShowPropertyEditorWhen(nameof(IsNonCircularMode), true)]
    public Animation TotalWidth { get; } = new(0, 0, 100000000);

    [Display(GroupName = "分割", Name = "分割方法")]
    [EnumComboBox]
    public SplitLayoutMode LayoutMode
    {
        get => layoutMode;
        set
        {
            if (Set(ref layoutMode, value, nameof(LayoutMode), nameof(IsGridMode), nameof(IsNonGridMode),
                nameof(IsCircularMode), nameof(IsNonCircularMode), nameof(CanUseAutoCompress), nameof(GridRowCount), nameof(GridRows)))
                foreach (var setting in ItemSettings)
                {
                    setting.LayoutMode = value;
                }
        }
    }

    [Display(GroupName = "分割", Name = "中心X")]
    [AnimationSlider("F1", "px", -50, 50)]
    public Animation CenterX { get; } = new(0, -100000000, 100000000);

    [Display(GroupName = "分割", Name = "中心Y")]
    [AnimationSlider("F1", "px", -50, 50)]
    public Animation CenterY { get; } = new(0, -100000000, 100000000);

    [Display(GroupName = "分割", Name = "回転オフセット")]
    [AnimationSlider("F1", "°", -50, 50)]
    public Animation RotationOffset { get; } = new(0, -36000000, 36000000);

    [Display(GroupName = "分割", Name = "番号表示", Description = "プレビューに配置先番号を透かし表示します。書き出しには表示されません")]
    [ToggleSlider]
    public bool ShowPlacementNumbers { get => showPlacementNumbers; set => Set(ref showPlacementNumbers, value); }

    [Display(GroupName = "分割", Name = "番号の色")]
    [ColorPicker]
    public System.Windows.Media.Color PlacementNumberColor { get => placementNumberColor; set => Set(ref placementNumberColor, value); }

    [Display(GroupName = "分割", Name = "個別設定")]
    [ToggleSlider]
    public bool UseIndividualSettings
    {
        get => useIndividualSettings;
        set => Set(ref useIndividualSettings, value, nameof(UseIndividualSettings), nameof(CanUseAutoCompress));
    }

    [Display(GroupName = "分割", Name = "自動圧縮", Description = "未指定の幅や段の高さを残り領域へ均等に収めます")]
    [ToggleSlider]
    [YukkuriMovieMaker.ItemEditor.CustomVisibilityAttributes.ShowPropertyEditorWhen(nameof(CanUseAutoCompress), true)]
    public bool AutoCompress { get => autoCompress; set => Set(ref autoCompress, value); }

    [Display(GroupName = "グリッド", Name = "段数")]
    [YukkuriMovieMaker.ItemEditor.CustomVisibilityAttributes.ShowPropertyEditorWhen(nameof(LayoutMode), SplitLayoutMode.Grid)]
    [TextBoxSlider("F0", "", 1, 10)]
    public int GridRowCount
    {
        get => gridRowCount;
        set
        {
            var count = Math.Clamp(value, 1, MaximumItems);
            if (Set(ref gridRowCount, count)) EnsureGridRows(count);
        }
    }

    [Display(GroupName = "グリッド", Name = "段ごとの列数", AutoGenerateField = true)]
    [YukkuriMovieMaker.ItemEditor.CustomVisibilityAttributes.ShowPropertyEditorWhen(nameof(LayoutMode), SplitLayoutMode.Grid)]
    public ImmutableList<GridRowSettings> GridRows
    {
        get => gridRows;
        set
        {
            gridRows = value ?? [];
            for (var i = 0; i < gridRows.Count; i++) gridRows[i].SetRowNumber(i);
            EnsureGridRows(GridRowCount);
            OnPropertyChanged();
        }
    }

    [Browsable(false)]
    public bool IsGridMode => LayoutMode == SplitLayoutMode.Grid;

    [Browsable(false)]
    public bool IsNonGridMode => !IsGridMode;

    [Browsable(false)]
    public bool IsCircularMode => LayoutMode == SplitLayoutMode.Circular;

    [Browsable(false)]
    public bool IsNonCircularMode => !IsCircularMode;

    [Browsable(false)]
    public bool CanUseAutoCompress => UseIndividualSettings || IsGridMode;

    [Display(GroupName = "分割", Name = "アイテム別設定", AutoGenerateField = true)]
    [YukkuriMovieMaker.ItemEditor.CustomVisibilityAttributes.ShowPropertyEditorWhen(nameof(UseIndividualSettings), true)]
    public ImmutableList<SplitItemSettings> ItemSettings
    {
        get => itemSettings;
        set
        {
            var list = value ?? CreateDefaultSettings(1);
            lock (settingsLock)
            {
                foreach (var setting in list)
                {
                    setting.LayoutMode = LayoutMode;
                }
                Set(ref itemSettings, list);
            }
        }
    }

    public override IVideoEffectProcessor CreateVideoEffect(IGraphicsDevicesAndContext devices) => new ExtendedScreenSplitProcessor(devices, this);
    public override IEnumerable<string> CreateExoVideoFilters(int keyFrameIndex, ExoOutputDescription exoOutputDescription) { yield break; }

    protected override IEnumerable<IAnimatable> GetAnimatables()
    {
        yield return SplitCount;
        yield return CircularTotalAngle;
        yield return TotalWidth;
        yield return CenterX;
        yield return CenterY;
        yield return RotationOffset;
        foreach (var row in gridRows) yield return row;
        SplitItemSettings[] snapshot;
        lock (settingsLock) snapshot = itemSettings.ToArray();
        foreach (var setting in snapshot) yield return setting;
    }

    private static ImmutableList<SplitItemSettings> CreateDefaultSettings(int count) =>
        Enumerable.Range(0, Math.Clamp(count, 1, MaximumItems)).Select(i => new SplitItemSettings(i)).ToImmutableList();

    internal void EnsureGroupCount(int groupCount)
    {
        lock (settingsLock)
        {
            var wanted = Math.Clamp(groupCount, 1, MaximumItems);
            if (wanted <= itemSettings.Count) return;
            var builder = itemSettings.ToBuilder();
            while (builder.Count < wanted) builder.Add(new SplitItemSettings(builder.Count)
            {
                LayoutMode = LayoutMode,
            });
            // This is discovered during preview/rendering. Recording it as an
            // undoable edit makes YMM4 replay stale animation keyframe state on undo.
            SetWithoutUndoRedo(ref itemSettings, builder.ToImmutable(), nameof(ItemSettings));
        }
    }

    private void EnsureGridRows(int count)
    {
        count = Math.Clamp(count, 1, MaximumItems);
        if (gridRows.Count < count)
        {
            var builder = gridRows.ToBuilder();
            while (builder.Count < count)
            {
                var index = builder.Count;
                builder.Add(new GridRowSettings(index, 1));
            }
            Set(ref gridRows, builder.ToImmutable(), nameof(GridRows));
        }
        for (var i = 0; i < gridRows.Count; i++)
        {
            gridRows[i].SetRowNumber(i);
            gridRows[i].IsActive = i < count;
        }
        OnPropertyChanged(nameof(GridRows));
    }
}

public sealed class SplitItemSettings : Animatable
{
    private int itemNumber;
    private SplitLayoutMode layoutMode;
    private SplitWidthUnit widthUnit;

    public SplitItemSettings() { }
    public SplitItemSettings(int itemNumber) => this.itemNumber = itemNumber;

    [Browsable(false)]
    public int ItemNumber { get => itemNumber; set => Set(ref itemNumber, value); }

    [ItemCategory("オフセットX")]
    [AnimationSlider("F1", "px", -50, 50)]
    public Animation OffsetX { get; } = new(0, -100000000, 100000000);

    [ItemCategory("オフセットY")]
    [AnimationSlider("F1", "px", -50, 50)]
    public Animation OffsetY { get; } = new(0, -100000000, 100000000);

    [ItemCategory("拡大率", "100%で元の拡大率を維持します")]
    [AnimationSlider("F1", "%", 0, 100)]
    public Animation ZoomPercent { get; } = new(100, 0, 10000000);

    [ItemCategory("回転角")]
    [AnimationSlider("F1", "°", -50, 50)]
    public Animation Rotation { get; } = new(0, -36000000, 36000000);
    [ItemCategory("単位")]
    [EnumComboBox]
    public SplitWidthUnit WidthUnit
    {
        get => widthUnit;
        set => Set(ref widthUnit, value, nameof(WidthUnit), nameof(IsAbsoluteWidth), nameof(IsProportionWidth));
    }

    [Browsable(false)]
    public bool IsAbsoluteWidth => WidthUnit == SplitWidthUnit.Absolute;

    [Browsable(false)]
    public bool IsProportionWidth => WidthUnit == SplitWidthUnit.Proportion;

    [ItemCategory("分割幅")]
    [ConditionalUnitAnimationSlider("F1", "px", "°", nameof(LayoutMode), SplitLayoutMode.Circular, 0, 50)]
    [YukkuriMovieMaker.ItemEditor.CustomVisibilityAttributes.ShowPropertyEditorWhen(nameof(IsAbsoluteWidth), true)]
    public Animation CropWidth { get; } = new(0, 0, 100000000);

    [ItemCategory("分割幅")]
    [AnimationSlider("F1", "%", 0, 50)]
    [YukkuriMovieMaker.ItemEditor.CustomVisibilityAttributes.ShowPropertyEditorWhen(nameof(IsProportionWidth), true)]
    public Animation ProportionalCropWidth { get; } = new(0, 0, 100);

    public SplitLayoutMode LayoutMode { get => layoutMode; set => SetWithoutUndoRedo(ref layoutMode, value); }
    protected override IEnumerable<IAnimatable> GetAnimatables() => [OffsetX, OffsetY, ZoomPercent, Rotation, CropWidth, ProportionalCropWidth];
}

public sealed class GridRowSettings : Animatable
{
    private int columns = 1;
    private GridHeightUnit heightUnit;
    public GridRowSettings() { }
    internal GridRowSettings(int rowNumber, int columns)
    {
        RowNumber = rowNumber;
        this.columns = Math.Clamp(columns, 1, ExtendedScreenSplitEffect.MaximumItems);
    }

    [Browsable(false)]
    [System.Text.Json.Serialization.JsonIgnore]
    public int RowNumber { get; private set; }

    [GridRowColumnDisplay]
    [TextBoxSlider("F0", "列", 1, 10)]
    [Range(1, ExtendedScreenSplitEffect.MaximumItems)]
    public int Columns { get => columns; set => Set(ref columns, Math.Clamp(value, 1, ExtendedScreenSplitEffect.MaximumItems)); }

    [GridRowWidthDisplay]
    [AnimationSlider("F1", "px", 0, 50)]
    public Animation MaximumWidth { get; } = new(0, 0, 100000000);

    [GridRowHeightUnitDisplay]
    [EnumComboBox]
    public GridHeightUnit HeightUnit
    {
        get => heightUnit;
        set => Set(ref heightUnit, value, nameof(HeightUnit), nameof(IsHeightPixel), nameof(IsHeightProportion));
    }

    [Browsable(false)]
    public bool IsHeightPixel => HeightUnit == GridHeightUnit.Pixel;

    [Browsable(false)]
    public bool IsHeightProportion => HeightUnit == GridHeightUnit.Proportion;

    [GridRowHeightDisplay]
    [AnimationSlider("F1", "px", 0, 50)]
    [YukkuriMovieMaker.ItemEditor.CustomVisibilityAttributes.ShowPropertyEditorWhen(nameof(IsHeightPixel), true)]
    public Animation HeightPixels { get; } = new(0, 0, 100000000);

    [GridRowHeightDisplay]
    [AnimationSlider("F1", "%", 0, 50)]
    [YukkuriMovieMaker.ItemEditor.CustomVisibilityAttributes.ShowPropertyEditorWhen(nameof(IsHeightProportion), true)]
    public Animation HeightProportion { get; } = new(0, 0, 100);

    [Browsable(false)]
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsActive { get; internal set; } = true;

    internal void SetRowNumber(int rowNumber) => RowNumber = rowNumber;
    protected override IEnumerable<IAnimatable> GetAnimatables() => [MaximumWidth, HeightPixels, HeightProportion];
}

[AttributeUsage(AttributeTargets.Property)]
internal sealed class ItemCategoryAttribute(string name, string? description = null) : CustomDisplayAttributeBase
{
    public override string? GetGroupName(object instance) => $"アイテム{(instance as SplitItemSettings)?.ItemNumber ?? 0}";
    public override string? GetName(object instance) => name;
    public override string? GetDescription(object instance) => description;
    public override int? GetOrder(object instance) => null;
    public override bool? GetAutoGenerateField(object instance) => null;
    public override bool? GetAutoGenerateFilter(object instance) => null;
}

[AttributeUsage(AttributeTargets.Property)]
internal sealed class GridRowColumnDisplayAttribute : CustomDisplayAttributeBase
{
    public override string? GetGroupName(object instance) => null;
    public override string? GetName(object instance) => $"{((instance as GridRowSettings)?.RowNumber ?? 0) + 1}段目";
    public override string? GetDescription(object instance) => "この段に配置する列数";
    public override int? GetOrder(object instance) => null;
    public override bool? GetAutoGenerateField(object instance) => null;
    public override bool? GetAutoGenerateFilter(object instance) =>
        (instance as GridRowSettings) is { IsActive: true };
}

[AttributeUsage(AttributeTargets.Property)]
internal sealed class GridRowWidthDisplayAttribute : CustomDisplayAttributeBase
{
    public override string? GetGroupName(object instance) => null;
    public override string? GetName(object instance) => $"{((instance as GridRowSettings)?.RowNumber ?? 0) + 1}段目の全体幅上限";
    public override string? GetDescription(object instance) => "0 px の場合は全体幅を使います";
    public override int? GetOrder(object instance) => null;
    public override bool? GetAutoGenerateField(object instance) => null;
    public override bool? GetAutoGenerateFilter(object instance) =>
        (instance as GridRowSettings) is { IsActive: true };
}

[AttributeUsage(AttributeTargets.Property)]
internal sealed class GridRowHeightUnitDisplayAttribute : CustomDisplayAttributeBase
{
    public override string? GetGroupName(object instance) => null;
    public override string? GetName(object instance) => $"{((instance as GridRowSettings)?.RowNumber ?? 0) + 1}段目の縦幅単位";
    public override string? GetDescription(object instance) => null;
    public override int? GetOrder(object instance) => null;
    public override bool? GetAutoGenerateField(object instance) => null;
    public override bool? GetAutoGenerateFilter(object instance) =>
        (instance as GridRowSettings) is { IsActive: true };
}

[AttributeUsage(AttributeTargets.Property)]
internal sealed class GridRowHeightDisplayAttribute : CustomDisplayAttributeBase
{
    public override string? GetGroupName(object instance) => null;
    public override string? GetName(object instance) => $"{((instance as GridRowSettings)?.RowNumber ?? 0) + 1}段目の縦幅";
    public override string? GetDescription(object instance) => "0 の場合は自動計算します";
    public override int? GetOrder(object instance) => null;
    public override bool? GetAutoGenerateField(object instance) => null;
    public override bool? GetAutoGenerateFilter(object instance) =>
        (instance as GridRowSettings) is { IsActive: true };
}

