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
using YukkuriMovieMaker.Plugin.Brush;
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
    [Display(Name = "ピクセル/度")]
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

public enum SplitAlignmentReference
{
    [Display(Name = "左")] Left,
    [Display(Name = "中央")] Center,
    [Display(Name = "右")] Right,
}

[VideoEffect("グループ制御中のアイテムで画面分割(拡張)", ["描画"], ["グループ制御中のアイテムを分割し、自動で切り抜きます"], true, true,
    IsEffectItemSupported = false, IsAviUtlSupported = false)]
public sealed class ExtendedScreenSplitEffect : VideoEffectBase
{
    internal const int MaximumItems = 1000;
    internal const int MaximumGridRows = 100;

    private bool useIndividualSettings;
    private bool autoCompress;
    private bool showPlacementNumbers;
    private System.Windows.Media.Color placementNumberColor = System.Windows.Media.Colors.White;
    private bool outlineOnly;
    private SplitLayoutMode layoutMode;
    private SplitAlignmentReference linearAlignment = SplitAlignmentReference.Center;
    private int gridRowCount = 2;
    private int previewRevision;
    private readonly object settingsLock = new();
    private readonly HashSet<GridRowSettings> attachedGridRows = [];
    private readonly HashSet<SplitItemSettings> attachedItemSettings = [];
    private int queuedGroupCount;
    private bool groupCountUpdateQueued;
    private ImmutableList<SplitItemSettings> itemSettings = CreateDefaultSettings(1);
    private ImmutableList<GridRowSettings> gridRows = [new(0, 2), new(1, 2)];

    public ExtendedScreenSplitEffect()
    {
        AttachGridRows(gridRows);
        AttachItemSettings(itemSettings);
        EnsureGridRows(gridRowCount);
    }

    public override string Label => "グループ制御中のアイテムで画面分割(拡張)";

    [Browsable(false)]
    [System.Text.Json.Serialization.JsonIgnore]
    public int PreviewRevision => previewRevision;

    [Display(GroupName = "分割", Name = "分割数")]
    [AnimationSlider("F0", "", 1, 50)]
    [YukkuriMovieMaker.ItemEditor.CustomVisibilityAttributes.ShowPropertyEditorWhen(nameof(IsNonGridMode), true)]
    public Animation SplitCount { get; } = new(4, 1, 50000000);

    [Display(GroupName = "分割", Name = "並びオフセット", Description = "アイテム番号に加算して移動先をずらします")]
    [AnimationSlider("F0", "", 0, 50)]
    public Animation ArrangementOffset { get; } = new(0, 0, 100000000);

    [Display(GroupName = "分割", Name = "全体角度")]
    [AnimationSlider("F1", "°", 0, 360)]
    [YukkuriMovieMaker.ItemEditor.CustomVisibilityAttributes.ShowPropertyEditorWhen(nameof(IsCircularMode), true)]
    public Animation CircularTotalAngle { get; } = new(360, 0, 360);

    [Display(GroupName = "分割", Name = "全体幅", Description = "0 px の場合は画面全体の幅を使います")]
    [AnimationSlider("F1", "px", 0, 500)]
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
            {
                foreach (var setting in ItemSettings)
                    setting.LayoutMode = value;
                EnsureGridRows(GridRowCount);
            }
        }
    }

    [Display(GroupName = "分割", Name = "基準")]
    [EnumComboBox]
    [YukkuriMovieMaker.ItemEditor.CustomVisibilityAttributes.ShowPropertyEditorWhen(nameof(IsLinearMode), true)]
    public SplitAlignmentReference LinearAlignment
    {
        get => linearAlignment;
        set => Set(ref linearAlignment, value);
    }

    [Display(GroupName = "分割", Name = "中心X")]
    [AnimationSlider("F1", "px", -500, 500)]
    public Animation CenterX { get; } = new(0, -100000000, 100000000);

    [Display(GroupName = "分割", Name = "中心Y")]
    [AnimationSlider("F1", "px", -500, 500)]
    public Animation CenterY { get; } = new(0, -100000000, 100000000);

    [Display(GroupName = "分割", Name = "回転オフセット")]
    [AnimationSlider("F1", "°", -360, 360)]
    public Animation RotationOffset { get; } = new(0, -36000000, 36000000);

    [Display(GroupName = "分割", Name = "番号表示", Description = "プレビューに配置先番号を透かし表示します。書き出しには表示されません")]
    [ToggleSlider]
    public bool ShowPlacementNumbers { get => showPlacementNumbers; set => Set(ref showPlacementNumbers, value); }

    [Display(GroupName = "分割", Name = "番号の色")]
    [ColorPicker]
    public System.Windows.Media.Color PlacementNumberColor { get => placementNumberColor; set => Set(ref placementNumberColor, value); }

    [Display(GroupName = "縁", Name = "太さ")]
    [AnimationSlider("F1", "px", 0, 10)]
    public Animation BorderThickness { get; } = new(3, 0, 500);

    [Display(GroupName = "縁", Name = "ぼかし")]
    [AnimationSlider("F1", "px", 0, 10)]
    public Animation BorderBlur { get; } = new(0, 0, 1000);

    [Display(GroupName = "縁", Name = "縁のみ")]
    [ToggleSlider]
    public bool OutlineOnly { get => outlineOnly; set => Set(ref outlineOnly, value); }

    [Display(GroupName = "縁/描画", Name = "X")]
    [AnimationSlider("F1", "px", -50, 50)]
    public Animation BorderX { get; } = new(0, -100000, 100000);

    [Display(GroupName = "縁/描画", Name = "Y")]
    [AnimationSlider("F1", "px", -50, 50)]
    public Animation BorderY { get; } = new(0, -100000, 100000);

    [Display(GroupName = "縁/描画", Name = "不透明度")]
    [AnimationSlider("F1", "%", 0, 100)]
    public Animation BorderOpacity { get; } = new(100, 0, 100);

    [Display(GroupName = "縁/描画", Name = "拡大率")]
    [AnimationSlider("F1", "%", 0, 100)]
    public Animation BorderZoom { get; } = new(100, 0, 100000);

    [Display(GroupName = "縁/模様", Name = "模様", AutoGenerateField = true)]
    public Brush BorderBrush { get; } = new();

    [Display(GroupName = "グリッド", Name = "段数")]
    [YukkuriMovieMaker.ItemEditor.CustomVisibilityAttributes.ShowPropertyEditorWhen(nameof(IsGridMode), true)]
    [TextBoxSlider("F0", "", 1, 100)]
    [Range(1, MaximumGridRows)]
    public int GridRowCount
    {
        get => gridRowCount;
        set
        {
            var count = Math.Clamp(value, 1, MaximumGridRows);
            if (Set(ref gridRowCount, count)) EnsureGridRows(count);
        }
    }

    [Display(GroupName = "グリッド", Name = "段ごとの設定")]
    [GridRowsEditor]
    [YukkuriMovieMaker.ItemEditor.CustomVisibilityAttributes.ShowPropertyEditorWhen(nameof(IsGridMode), true)]
    public ImmutableList<GridRowSettings> GridRows
    {
        get => gridRows;
        set
        {
            var rows = value ?? [];
            if (!Set(ref gridRows, rows, nameof(GridRows))) return;
            AttachGridRows(gridRows);
            for (var i = 0; i < gridRows.Count; i++) gridRows[i].SetRowNumber(i);
            EnsureGridRows(GridRowCount);
        }
    }

    [Display(GroupName = "アイテム", Name = "個別設定")]
    [ToggleSlider]
    public bool UseIndividualSettings
    {
        get => useIndividualSettings;
        set
        {
            if (!Set(ref useIndividualSettings, value, nameof(UseIndividualSettings), nameof(CanUseAutoCompress), nameof(ItemSettings))) return;
            foreach (var setting in ItemSettings) setting.IsVisible = value;
            OnPropertyChanged(nameof(ItemSettings));
        }
    }

    [Display(GroupName = "アイテム", Name = "自動圧縮", Description = "未指定の幅や段の高さを残り領域へ均等に収めます")]
    [ToggleSlider]
    [YukkuriMovieMaker.ItemEditor.CustomVisibilityAttributes.ShowPropertyEditorWhen(nameof(CanUseAutoCompress), true)]
    public bool AutoCompress { get => autoCompress; set => Set(ref autoCompress, value); }

    [Browsable(false)]
    public bool IsGridMode => LayoutMode == SplitLayoutMode.Grid;

    [Browsable(false)]
    public bool IsNonGridMode => !IsGridMode;

    [Browsable(false)]
    public bool IsLinearMode => LayoutMode == SplitLayoutMode.Linear;

    [Browsable(false)]
    public bool IsCircularMode => LayoutMode == SplitLayoutMode.Circular;

    [Browsable(false)]
    public bool IsNonCircularMode => !IsCircularMode;

    [Browsable(false)]
    public bool CanUseAutoCompress => UseIndividualSettings || IsGridMode;

    [Display(GroupName = "アイテム", Name = "アイテム別設定")]
    [SplitItemsEditor]
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
                    setting.IsVisible = UseIndividualSettings;
                    AttachItemSetting(setting);
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
        yield return ArrangementOffset;
        yield return BorderThickness;
        yield return BorderBlur;
        yield return BorderX;
        yield return BorderY;
        yield return BorderOpacity;
        yield return BorderZoom;
        yield return BorderBrush;
        foreach (var row in gridRows)
            yield return row;
        SplitItemSettings[] snapshot;
        lock (settingsLock) snapshot = itemSettings.ToArray();
        foreach (var setting in snapshot) yield return setting;
    }

    private static ImmutableList<SplitItemSettings> CreateDefaultSettings(int count) =>
        Enumerable.Range(0, Math.Clamp(count, 1, MaximumItems)).Select(i => new SplitItemSettings(i)).ToImmutableList();

    internal void EnsureGroupCount(int groupCount)
    {
        var wanted = Math.Clamp(groupCount, 1, MaximumItems);
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            lock (settingsLock)
            {
                if (wanted == itemSettings.Count && !groupCountUpdateQueued) return;
                queuedGroupCount = wanted;
                if (groupCountUpdateQueued) return;
                groupCountUpdateQueued = true;
            }
            dispatcher.BeginInvoke(new Action(ApplyQueuedGroupCount));
            return;
        }

        EnsureGroupCountCore(wanted);
    }

    private void ApplyQueuedGroupCount()
    {
        int wanted;
        lock (settingsLock)
        {
            wanted = queuedGroupCount;
            queuedGroupCount = 0;
            groupCountUpdateQueued = false;
        }
        EnsureGroupCountCore(wanted);
    }

    private void EnsureGroupCountCore(int wanted)
    {
        lock (settingsLock)
        {
            if (wanted == itemSettings.Count) return;
            var builder = itemSettings.ToBuilder();
            if (wanted < builder.Count)
            {
                builder.RemoveRange(wanted, builder.Count - wanted);
                SetWithoutUndoRedo(ref itemSettings, builder.ToImmutable(), nameof(ItemSettings));
                return;
            }
            while (builder.Count < wanted) builder.Add(new SplitItemSettings(builder.Count)
            {
                LayoutMode = LayoutMode,
                IsVisible = UseIndividualSettings,
            });
            foreach (var setting in builder.Skip(itemSettings.Count)) AttachItemSetting(setting);
            // Group count is discovered during preview rendering, but the item
            // editor list is changed on the UI dispatcher so its controls rebuild safely.
            SetWithoutUndoRedo(ref itemSettings, builder.ToImmutable(), nameof(ItemSettings));
        }
    }

    private void EnsureGridRows(int count)
    {
        count = Math.Clamp(count, 1, MaximumGridRows);
        if (gridRows.Count < count)
        {
            var builder = gridRows.ToBuilder();
            while (builder.Count < count)
            {
                var index = builder.Count;
                var row = new GridRowSettings(index, 1);
                AttachGridRow(row);
                builder.Add(row);
            }
            Set(ref gridRows, builder.ToImmutable(), nameof(GridRows));
        }
        for (var i = 0; i < gridRows.Count; i++)
        {
            gridRows[i].SetRowNumber(i);
            gridRows[i].IsGridMode = IsGridMode;
            gridRows[i].IsActive = i < count && IsGridMode;
        }
        OnPropertyChanged(nameof(GridRows));
    }

    private void AttachGridRows(IEnumerable<GridRowSettings> rows)
    {
        foreach (var row in rows) AttachGridRow(row);
    }

    private void AttachGridRow(GridRowSettings row)
    {
        if (attachedGridRows.Add(row)) row.SettingsChanged += OnGridRowSettingsChanged;
    }

    private void OnGridRowSettingsChanged(object? sender, EventArgs e)
    {
        NotifyNestedParameterChanged();
        // GridRows is a custom editor property rather than a directly bound
        // Animation. Raising the parent property as well ensures YMM4 invalidates
        // the preview immediately for edits made inside the selected row editor.
        OnPropertyChanged(nameof(GridRows));
    }

    private void AttachItemSettings(IEnumerable<SplitItemSettings> settings)
    {
        foreach (var setting in settings) AttachItemSetting(setting);
    }

    private void AttachItemSetting(SplitItemSettings setting)
    {
        if (attachedItemSettings.Add(setting)) setting.SettingsChanged += OnItemSettingsChanged;
    }

    private void OnItemSettingsChanged(object? sender, EventArgs e)
    {
        NotifyNestedParameterChanged();
    }

    private void NotifyNestedParameterChanged() =>
        SetWithoutUndoRedo(ref previewRevision, unchecked(previewRevision + 1), nameof(PreviewRevision));
}

public sealed class SplitItemSettings : Animatable
{
    private int itemNumber;
    private SplitLayoutMode layoutMode;
    private SplitWidthUnit widthUnit;
    private bool isVisible;
    private string remark = string.Empty;

    public event EventHandler? SettingsChanged;

    public SplitItemSettings()
    {
        PropertyChanged += (_, _) => SettingsChanged?.Invoke(this, EventArgs.Empty);
        OffsetX.PropertyChanged += (_, _) => SettingsChanged?.Invoke(this, EventArgs.Empty);
        OffsetY.PropertyChanged += (_, _) => SettingsChanged?.Invoke(this, EventArgs.Empty);
        ZoomPercent.PropertyChanged += (_, _) => SettingsChanged?.Invoke(this, EventArgs.Empty);
        Rotation.PropertyChanged += (_, _) => SettingsChanged?.Invoke(this, EventArgs.Empty);
        CropWidth.PropertyChanged += (_, _) => SettingsChanged?.Invoke(this, EventArgs.Empty);
        ProportionalCropWidth.PropertyChanged += (_, _) => SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public SplitItemSettings(int itemNumber) : this() => this.itemNumber = itemNumber;

    [Browsable(false)]
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsVisible
    {
        get => isVisible;
        internal set => SetWithoutUndoRedo(ref isVisible, value);
    }

    [Browsable(false)]
    public int ItemNumber { get => itemNumber; set => Set(ref itemNumber, value); }

    [Browsable(false)]
    public string Remark { get => remark; set => Set(ref remark, value ?? string.Empty); }

    [ItemCategory("X")]
    [AnimationSlider("F1", "px", -500, 500)]
    public Animation OffsetX { get; } = new(0, -100000, 100000);

    [ItemCategory("Y")]
    [AnimationSlider("F1", "px", -500, 500)]
    public Animation OffsetY { get; } = new(0, -100000, 100000);

    [ItemCategory("拡大率", "100%で元の拡大率を維持します")]
    [AnimationSlider("F1", "%", 0, 100)]
    public Animation ZoomPercent { get; } = new(100, 0, 10000000);

    [ItemCategory("回転角")]
    [AnimationSlider("F1", "°", -360, 360)]
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
    [ConditionalUnitAnimationSlider("F1", "px", "°", nameof(LayoutMode), SplitLayoutMode.Circular, 0, 500)]
    [YukkuriMovieMaker.ItemEditor.CustomVisibilityAttributes.ShowPropertyEditorWhen(nameof(IsAbsoluteWidth), true)]
    public Animation CropWidth { get; } = new(0, 0, 100000000);

    [ItemCategory("分割幅")]
    [AnimationSlider("F1", "%", 0, 100)]
    [YukkuriMovieMaker.ItemEditor.CustomVisibilityAttributes.ShowPropertyEditorWhen(nameof(IsProportionWidth), true)]
    public Animation ProportionalCropWidth { get; } = new(0, 0, 100);

    public SplitLayoutMode LayoutMode { get => layoutMode; set => SetWithoutUndoRedo(ref layoutMode, value); }
    protected override IEnumerable<IAnimatable> GetAnimatables() => [OffsetX, OffsetY, ZoomPercent, Rotation, CropWidth, ProportionalCropWidth];
}

public sealed class GridRowSettings : Animatable
{
    private int columns = 1;
    private GridHeightUnit heightUnit;
    private SplitAlignmentReference alignment = SplitAlignmentReference.Center;
    private bool isGridMode;
    private string remark = string.Empty;
    public event EventHandler? SettingsChanged;

    public GridRowSettings()
    {
        PropertyChanged += (_, _) => SettingsChanged?.Invoke(this, EventArgs.Empty);
        MaximumWidth.PropertyChanged += (_, _) => SettingsChanged?.Invoke(this, EventArgs.Empty);
        XOffset.PropertyChanged += (_, _) => SettingsChanged?.Invoke(this, EventArgs.Empty);
        HeightPixels.PropertyChanged += (_, _) => SettingsChanged?.Invoke(this, EventArgs.Empty);
        HeightProportion.PropertyChanged += (_, _) => SettingsChanged?.Invoke(this, EventArgs.Empty);
        MaximumWidth.UndoRedoCommandCreated += (_, _) => SettingsChanged?.Invoke(this, EventArgs.Empty);
        XOffset.UndoRedoCommandCreated += (_, _) => SettingsChanged?.Invoke(this, EventArgs.Empty);
        HeightPixels.UndoRedoCommandCreated += (_, _) => SettingsChanged?.Invoke(this, EventArgs.Empty);
        HeightProportion.UndoRedoCommandCreated += (_, _) => SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    internal GridRowSettings(int rowNumber, int columns)
        : this()
    {
        RowNumber = rowNumber;
        this.columns = Math.Clamp(columns, 1, ExtendedScreenSplitEffect.MaximumItems);
    }

    [Browsable(false)]
    [System.Text.Json.Serialization.JsonIgnore]
    public int RowNumber { get; private set; }

    [Browsable(false)]
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayName => $"{RowNumber + 1}段目";

    [Browsable(false)]
    public string Remark { get => remark; set => Set(ref remark, value ?? string.Empty); }

    [GridRowColumnDisplay]
    [YukkuriMovieMaker.ItemEditor.CustomVisibilityAttributes.ShowPropertyEditorWhen(nameof(IsGridMode), true)]
    [TextBoxSlider("F0", "列", 1, 10)]
    [Range(1, ExtendedScreenSplitEffect.MaximumItems)]
    public int Columns { get => columns; set => Set(ref columns, Math.Clamp(value, 1, ExtendedScreenSplitEffect.MaximumItems)); }

    [GridRowWidthDisplay]
    [YukkuriMovieMaker.ItemEditor.CustomVisibilityAttributes.ShowPropertyEditorWhen(nameof(IsGridMode), true)]
    [AnimationSlider("F1", "px", 0, 500)]
    public Animation MaximumWidth { get; } = new(0, 0, 100000000);

    [GridRowXOffsetDisplay]
    [YukkuriMovieMaker.ItemEditor.CustomVisibilityAttributes.ShowPropertyEditorWhen(nameof(IsGridMode), true)]
    [AnimationSlider("F1", "px", -500, 500)]
    public Animation XOffset { get; } = new(0, -100000, 100000);

    [GridRowAlignmentDisplay]
    [YukkuriMovieMaker.ItemEditor.CustomVisibilityAttributes.ShowPropertyEditorWhen(nameof(IsGridMode), true)]
    [EnumComboBox]
    public SplitAlignmentReference Alignment { get => alignment; set => Set(ref alignment, value); }

    [GridRowHeightUnitDisplay]
    [YukkuriMovieMaker.ItemEditor.CustomVisibilityAttributes.ShowPropertyEditorWhen(nameof(IsGridMode), true)]
    [EnumComboBox]
    public GridHeightUnit HeightUnit
    {
        get => heightUnit;
        set => Set(ref heightUnit, value, nameof(HeightUnit), nameof(IsHeightPixel), nameof(IsHeightProportion),
            nameof(IsPixelHeightVisible), nameof(IsProportionHeightVisible));
    }

    [Browsable(false)]
    public bool IsHeightPixel => HeightUnit == GridHeightUnit.Pixel;

    [Browsable(false)]
    public bool IsHeightProportion => HeightUnit == GridHeightUnit.Proportion;

    [Browsable(false)]
    public bool IsPixelHeightVisible => IsGridMode && IsHeightPixel;

    [Browsable(false)]
    public bool IsProportionHeightVisible => IsGridMode && IsHeightProportion;

    [GridRowHeightDisplay]
    [AnimationSlider("F1", "px", 0, 50)]
    [YukkuriMovieMaker.ItemEditor.CustomVisibilityAttributes.ShowPropertyEditorWhen(nameof(IsPixelHeightVisible), true)]
    public Animation HeightPixels { get; } = new(0, 0, 100000000);

    [GridRowHeightDisplay]
    [AnimationSlider("F1", "%", 0, 50)]
    [YukkuriMovieMaker.ItemEditor.CustomVisibilityAttributes.ShowPropertyEditorWhen(nameof(IsProportionHeightVisible), true)]
    public Animation HeightProportion { get; } = new(0, 0, 100);

    [Browsable(false)]
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsActive { get; internal set; } = true;

    [Browsable(false)]
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsGridMode
    {
        get => isGridMode;
        internal set => SetWithoutUndoRedo(ref isGridMode, value);
    }

    internal void SetRowNumber(int rowNumber)
    {
        if (RowNumber == rowNumber) return;
        RowNumber = rowNumber;
        OnPropertyChanged(nameof(DisplayName));
    }
    protected override IEnumerable<IAnimatable> GetAnimatables() => [MaximumWidth, XOffset, HeightPixels, HeightProportion];
}

[AttributeUsage(AttributeTargets.Property)]
internal sealed class ItemCategoryAttribute(string name, string? description = null) : CustomDisplayAttributeBase
{
    public override string? GetGroupName(object instance) => null;
    public override string? GetName(object instance) => name;
    public override string? GetDescription(object instance) => description;
    public override int? GetOrder(object instance) => null;
    public override bool? GetAutoGenerateField(object instance) => null;
    public override bool? GetAutoGenerateFilter(object instance) =>
        (instance as SplitItemSettings) is { IsVisible: true };
}

[AttributeUsage(AttributeTargets.Property)]
internal sealed class GridRowColumnDisplayAttribute : CustomDisplayAttributeBase
{
    public override string? GetGroupName(object instance) => null;
    public override string? GetName(object instance) => "列数";
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
    public override string? GetName(object instance) => "全体幅上限";
    public override string? GetDescription(object instance) => "0 px の場合は全体幅を使います";
    public override int? GetOrder(object instance) => null;
    public override bool? GetAutoGenerateField(object instance) => null;
    public override bool? GetAutoGenerateFilter(object instance) =>
        (instance as GridRowSettings) is { IsActive: true };
}

[AttributeUsage(AttributeTargets.Property)]
internal sealed class GridRowXOffsetDisplayAttribute : CustomDisplayAttributeBase
{
    public override string? GetGroupName(object instance) => null;
    public override string? GetName(object instance) => "Xオフセット";
    public override string? GetDescription(object instance) => null;
    public override int? GetOrder(object instance) => null;
    public override bool? GetAutoGenerateField(object instance) => null;
    public override bool? GetAutoGenerateFilter(object instance) =>
        (instance as GridRowSettings) is { IsActive: true };
}

[AttributeUsage(AttributeTargets.Property)]
internal sealed class GridRowAlignmentDisplayAttribute : CustomDisplayAttributeBase
{
    public override string? GetGroupName(object instance) => null;
    public override string? GetName(object instance) => "基準";
    public override string? GetDescription(object instance) => "この段を全体幅の左・中央・右のどこにそろえるか";
    public override int? GetOrder(object instance) => null;
    public override bool? GetAutoGenerateField(object instance) => null;
    public override bool? GetAutoGenerateFilter(object instance) =>
        (instance as GridRowSettings) is { IsActive: true };
}

[AttributeUsage(AttributeTargets.Property)]
internal sealed class GridRowHeightUnitDisplayAttribute : CustomDisplayAttributeBase
{
    public override string? GetGroupName(object instance) => null;
    public override string? GetName(object instance) => "縦幅単位";
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
    public override string? GetName(object instance) => "縦幅";
    public override string? GetDescription(object instance) => "0 の場合は自動計算します";
    public override int? GetOrder(object instance) => null;
    public override bool? GetAutoGenerateField(object instance) => null;
    public override bool? GetAutoGenerateFilter(object instance) =>
        (instance as GridRowSettings) is { IsActive: true };
}

