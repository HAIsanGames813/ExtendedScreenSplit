using System.ComponentModel;
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Controls;

namespace YMM4.ExtendedScreenSplit;

public partial class SplitItemsEditorControl : UserControl, IPropertyEditorControl, INotifyPropertyChanged
{
    private ExtendedScreenSplitEffect? effect;
    private object? ownerItem;
    private object? timeline;
    private SplitItemSettings? selectedItem;
    private readonly List<object> sourceItems = [];
    private readonly List<string> sourceRemarks = [];
    private bool refreshingSources;
    private bool sourceMappingsInitialized;
    private bool refreshQueued;

    public SplitItemsEditorControl()
    {
        InitializeComponent();
        DataContext = this;
    }

    public IEnumerable<SplitItemSettings> Items => effect?.ItemSettings ?? [];
    public string ItemRemarkTooltip => Texts.ItemRemarkTooltip;

    public SplitItemSettings? SelectedItem
    {
        get => selectedItem;
        set
        {
            if (ReferenceEquals(selectedItem, value)) return;
            if (selectedItem is not null) selectedItem.PropertyChanged -= SelectedItem_PropertyChanged;
            selectedItem = value;
            if (selectedItem is not null) selectedItem.PropertyChanged += SelectedItem_PropertyChanged;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedGroupName));
            RebuildSelectedItemEditor();
        }
    }

    public string? SelectedGroupName => SelectedItem is null
        ? null
        : string.IsNullOrWhiteSpace(SelectedItem.Remark)
            ? string.Format(System.Globalization.CultureInfo.CurrentCulture, Texts.ItemGroupFormat, SelectedItem.ItemNumber)
            : string.Format(System.Globalization.CultureInfo.CurrentCulture, Texts.ItemRemarkGroupFormat, SelectedItem.ItemNumber, SelectedItem.Remark);

    public event EventHandler? BeginEdit;
    public event EventHandler? EndEdit;
    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetEffect(ExtendedScreenSplitEffect? value, object? item = null)
    {
        if (effect is not null) effect.PropertyChanged -= Effect_PropertyChanged;
        if (timeline is INotifyPropertyChanged oldTimeline) oldTimeline.PropertyChanged -= Timeline_PropertyChanged;
        UnsubscribeSourceItems();
        effect = value;
        ownerItem = item;
        timeline = GroupItemContextResolver.FindTimeline(ownerItem);
        sourceMappingsInitialized = false;
        if (timeline is INotifyPropertyChanged newTimeline) newTimeline.PropertyChanged += Timeline_PropertyChanged;
        if (effect is not null) effect.PropertyChanged += Effect_PropertyChanged;
        SelectedItem = effect?.ItemSettings.FirstOrDefault(x => x.ItemNumber == 0)
            ?? effect?.ItemSettings.FirstOrDefault();
        OnPropertyChanged(nameof(Items));
        RefreshSourceItems();
    }

    private void Effect_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(ExtendedScreenSplitEffect.ItemSettings))
        {
            OnPropertyChanged(nameof(Items));
            var selectedNumber = selectedItem?.ItemNumber ?? 0;
            SelectedItem = effect?.ItemSettings.FirstOrDefault(x => x.ItemNumber == selectedNumber)
                ?? effect?.ItemSettings.FirstOrDefault();
            RefreshSourceItems();
        }
    }

    private void Timeline_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == "Items")
            QueueSourceRefresh();
    }

    private void QueueSourceRefresh()
    {
        if (refreshQueued || !IsLoaded) return;
        refreshQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            refreshQueued = false;
            RefreshSourceItems();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void RefreshSourceItems()
    {
        if (refreshingSources || effect is null || timeline is null || ownerItem is null) return;
        refreshingSources = true;
        try
        {
            var nextItems = GroupItemContextResolver.GetChildren(timeline, ownerItem);
            var changed = sourceItems.Count != nextItems.Count || sourceItems.Where((item, index) => !ReferenceEquals(item, nextItems[index])).Any();
            var previousRemarks = sourceRemarks.ToArray();
            UnsubscribeSourceItems();
            sourceItems.Clear();
            sourceItems.AddRange(nextItems);
            sourceRemarks.Clear();
            sourceRemarks.AddRange(sourceItems.Select(GroupItemContextResolver.GetRemark));
            SubscribeSourceItems();

            if (sourceItems.Count > 0) effect.EnsureGroupCount(sourceItems.Count);
            var settings = effect.ItemSettings;
            for (var index = 0; index < Math.Min(settings.Count, sourceItems.Count); index++)
            {
                var source = sourceItems[index];
                var sourceRemark = sourceRemarks[index];
                if (!sourceMappingsInitialized)
                {
                    if (!string.IsNullOrWhiteSpace(sourceRemark))
                        settings[index].Remark = sourceRemark;
                    else if (!string.IsNullOrWhiteSpace(settings[index].Remark))
                        GroupItemContextResolver.SetRemark(source, settings[index].Remark);
                }
                else if (changed || (index < previousRemarks.Length && previousRemarks[index] != sourceRemark))
                {
                    settings[index].Remark = sourceRemark;
                }
            }
            sourceMappingsInitialized = true;
            if (selectedItem is not null) OnPropertyChanged(nameof(SelectedGroupName));
        }
        finally
        {
            refreshingSources = false;
        }
    }

    private void SubscribeSourceItems()
    {
        foreach (var source in sourceItems.OfType<INotifyPropertyChanged>())
            source.PropertyChanged += SourceItem_PropertyChanged;
    }

    private void UnsubscribeSourceItems()
    {
        foreach (var source in sourceItems.OfType<INotifyPropertyChanged>())
            source.PropertyChanged -= SourceItem_PropertyChanged;
    }

    private void SourceItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (null or "" or "Remark") || refreshingSources || effect is null || sender is null) return;
        var index = sourceItems.FindIndex(item => ReferenceEquals(item, sender));
        if (index < 0 || index >= effect.ItemSettings.Count) return;
        var remark = GroupItemContextResolver.GetRemark(sender);
        if (index < sourceRemarks.Count) sourceRemarks[index] = remark;
        if (effect.ItemSettings[index].Remark != remark) effect.ItemSettings[index].Remark = remark;
    }

    private void SelectedItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(SplitItemSettings.Remark) ||
            e.PropertyName == nameof(SplitItemSettings.ItemNumber))
        {
            OnPropertyChanged(nameof(SelectedGroupName));
            if (e.PropertyName == nameof(SplitItemSettings.Remark))
            {
                RebuildSelectedItemEditor();
                if (!refreshingSources && sender is SplitItemSettings setting &&
                    setting.ItemNumber >= 0 && setting.ItemNumber < sourceItems.Count)
                    GroupItemContextResolver.SetRemark(sourceItems[setting.ItemNumber], setting.Remark);
            }
        }
    }

    private void RebuildSelectedItemEditor()
    {
        if (SelectedItemEditorHost is null) return;
        if (SelectedItem is null)
        {
            SelectedItemEditorHost.Content = null;
            return;
        }
        var editor = new PropertiesEditor
        {
            Target = SelectedItem,
            ParentGroup = SelectedGroupName,
            IsScrollViewerEnabled = true,
        };
        editor.BeginEdit += ChildEditor_BeginEdit;
        editor.EndEdit += ChildEditor_EndEdit;
        SelectedItemEditorHost.Content = editor;
    }

    private void Remark_GotKeyboardFocus(object sender, RoutedEventArgs e) => BeginEdit?.Invoke(this, EventArgs.Empty);

    private void Remark_LostKeyboardFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: SplitItemSettings setting } && setting.ItemNumber >= 0 && setting.ItemNumber < sourceItems.Count)
            GroupItemContextResolver.SetRemark(sourceItems[setting.ItemNumber], setting.Remark);
        EndEdit?.Invoke(this, EventArgs.Empty);
    }

    private void ChildEditor_BeginEdit(object? sender, EventArgs e) => BeginEdit?.Invoke(this, EventArgs.Empty);

    private void ChildEditor_EndEdit(object? sender, EventArgs e) => EndEdit?.Invoke(this, EventArgs.Empty);

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
