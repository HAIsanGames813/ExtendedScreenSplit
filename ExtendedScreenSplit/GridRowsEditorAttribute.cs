using System.Windows;
using YukkuriMovieMaker.Commons;

namespace YMM4.ExtendedScreenSplit;

[AttributeUsage(AttributeTargets.Property)]
internal sealed class GridRowsEditorAttribute : PropertyEditorAttribute2
{
    public GridRowsEditorAttribute()
    {
        PropertyEditorSize = PropertyEditorSize.FullWidth;
        IsMultiEditing = false;
    }

    public override FrameworkElement Create() => new GridRowsEditorControl();

    public override void SetBindings(FrameworkElement control, ItemProperty[] itemProperties)
    {
        if (control is GridRowsEditorControl editor && itemProperties.FirstOrDefault()?.PropertyOwner is ExtendedScreenSplitEffect effect)
            editor.SetEffect(effect);
    }

    public override void ClearBindings(FrameworkElement control)
    {
        if (control is GridRowsEditorControl editor) editor.SetEffect(null);
    }
}
