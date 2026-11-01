using System.Reflection;
using System.Windows;
using YukkuriMovieMaker.Commons;

namespace YMM4.ExtendedScreenSplit;

[AttributeUsage(AttributeTargets.Property)]
internal sealed class SplitItemsEditorAttribute : PropertyEditorAttribute2
{
    public SplitItemsEditorAttribute()
    {
        PropertyEditorSize = PropertyEditorSize.FullWidth;
        IsMultiEditing = false;
    }

    public override FrameworkElement Create() => new SplitItemsEditorControl();

    public override void SetBindings(FrameworkElement control, ItemProperty[] itemProperties)
    {
        if (control is SplitItemsEditorControl editor && itemProperties.FirstOrDefault() is { PropertyOwner: ExtendedScreenSplitEffect effect } property)
            editor.SetEffect(effect, property.Item);
    }

    public override void ClearBindings(FrameworkElement control)
    {
        if (control is SplitItemsEditorControl editor) editor.SetEffect(null);
    }
}
