using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Controls;

namespace YMM4.ExtendedScreenSplit;

public partial class GridRowsEditorControl : UserControl, IPropertyEditorControl, INotifyPropertyChanged
{
    private ExtendedScreenSplitEffect? effect;
    private GridRowSettings? selectedRow;

    public GridRowsEditorControl()
    {
        InitializeComponent();
        DataContext = this;
    }

    public IEnumerable<GridRowSettings> Rows => effect?.GridRows.Take(effect.GridRowCount) ?? [];
    public string RowRemarkTooltip => Texts.RowRemarkTooltip;

    public GridRowSettings? SelectedRow
    {
        get => selectedRow;
        set
        {
            if (ReferenceEquals(selectedRow, value)) return;
            if (selectedRow is not null) selectedRow.PropertyChanged -= SelectedRow_PropertyChanged;
            selectedRow = value;
            if (selectedRow is not null) selectedRow.PropertyChanged += SelectedRow_PropertyChanged;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedGroupName));
            RebuildSelectedRowEditor();
        }
    }

    public string? SelectedGroupName => SelectedRow is null
        ? null
        : string.IsNullOrWhiteSpace(SelectedRow.Remark)
            ? string.Format(System.Globalization.CultureInfo.CurrentCulture, Texts.RowGroupFormat, SelectedRow.RowNumber + 1)
            : string.Format(System.Globalization.CultureInfo.CurrentCulture, Texts.RowRemarkGroupFormat, SelectedRow.RowNumber + 1, SelectedRow.Remark);

    public event EventHandler? BeginEdit;
    public event EventHandler? EndEdit;
    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetEffect(ExtendedScreenSplitEffect? value)
    {
        if (effect is not null) effect.PropertyChanged -= Effect_PropertyChanged;
        effect = value;
        if (effect is not null) effect.PropertyChanged += Effect_PropertyChanged;
        var selectedNumber = selectedRow?.RowNumber ?? 0;
        OnPropertyChanged(nameof(Rows));
        SelectedRow = effect?.GridRows.Take(effect.GridRowCount).FirstOrDefault(row => row.RowNumber == selectedNumber)
            ?? effect?.GridRows.Take(effect.GridRowCount).FirstOrDefault();
    }

    private void Effect_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName is nameof(ExtendedScreenSplitEffect.GridRows) or nameof(ExtendedScreenSplitEffect.GridRowCount))
        {
            var selectedNumber = selectedRow?.RowNumber ?? 0;
            OnPropertyChanged(nameof(Rows));
            SelectedRow = effect?.GridRows.Take(effect.GridRowCount).FirstOrDefault(row => row.RowNumber == selectedNumber)
                ?? effect?.GridRows.Take(effect.GridRowCount).FirstOrDefault();
        }
    }

    private void SelectedRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(GridRowSettings.Remark))
        {
            OnPropertyChanged(nameof(SelectedGroupName));
            if (e.PropertyName == nameof(GridRowSettings.Remark)) RebuildSelectedRowEditor();
        }
    }

    private void RebuildSelectedRowEditor()
    {
        if (SelectedRowEditorHost is null) return;
        if (SelectedRow is null)
        {
            SelectedRowEditorHost.Content = null;
            return;
        }
        var editor = new PropertiesEditor
        {
            Target = SelectedRow,
            ParentGroup = SelectedGroupName,
            IsScrollViewerEnabled = true,
        };
        editor.BeginEdit += ChildEditor_BeginEdit;
        editor.EndEdit += ChildEditor_EndEdit;
        SelectedRowEditorHost.Content = editor;
    }

    private void Remark_GotKeyboardFocus(object sender, RoutedEventArgs e) => BeginEdit?.Invoke(this, EventArgs.Empty);
    private void Remark_LostKeyboardFocus(object sender, RoutedEventArgs e) => EndEdit?.Invoke(this, EventArgs.Empty);
    private void ChildEditor_BeginEdit(object? sender, EventArgs e) => BeginEdit?.Invoke(this, EventArgs.Empty);
    private void ChildEditor_EndEdit(object? sender, EventArgs e) => EndEdit?.Invoke(this, EventArgs.Empty);

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
