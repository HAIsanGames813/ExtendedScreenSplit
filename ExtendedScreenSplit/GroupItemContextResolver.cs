using System.Collections;
using System.Reflection;
using System.Windows;

namespace YMM4.ExtendedScreenSplit;

internal static class GroupItemContextResolver
{
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static object? FindTimeline(object? ownerItem)
    {
        if (ownerItem is null || Application.Current is null) return null;
        foreach (Window window in Application.Current.Windows)
        {
            var mainViewModel = window.DataContext;
            if (mainViewModel is null) continue;
            var active = GetProperty(mainViewModel, "ActiveTimelineViewModel");
            var activeTimeline = FindTimelineForItem(active, ownerItem);
            if (activeTimeline is not null) return activeTimeline;

            if (GetProperty(mainViewModel, "DocumentViewModels") is not IEnumerable documents) continue;
            foreach (var document in documents)
            {
                var timelineViewModel = GetProperty(document, "TimelineViewModel");
                var timeline = FindTimelineForItem(timelineViewModel, ownerItem);
                if (timeline is not null) return timeline;
            }
        }
        return null;
    }

    public static IReadOnlyList<object> GetChildren(object? timeline, object? groupItem)
    {
        if (timeline is null || groupItem is null || GetProperty(timeline, "Items") is not IEnumerable timelineItems)
            return [];

        var frame = ReadInt(timeline, "CurrentFrame");
        var items = timelineItems.Cast<object>()
            .Where(item => ReadInt(item, "Frame") <= frame && frame < ReadInt(item, "Frame") + Math.Max(1, ReadInt(item, "Length")))
            .Where(item => !ReadBool(item, "IsHidden"))
            .ToArray();

        try
        {
            var resolverType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("YukkuriMovieMaker.Player.Video.TimelineGroupResolver", false))
                .FirstOrDefault(type => type is not null);
            if (resolverType is not null && timelineItems.GetType().GetGenericArguments().FirstOrDefault() is { } itemType)
            {
                var typedItems = Array.CreateInstance(itemType, items.Length);
                for (var i = 0; i < items.Length; i++) typedItems.SetValue(items[i], i);
                var constructor = resolverType.GetConstructors(InstanceFlags)
                    .FirstOrDefault(candidate => candidate.GetParameters().Length == 2);
                var resolver = constructor?.Invoke([typedItems, new Func<int, bool>(_ => true)]);
                var getChildren = resolverType.GetMethod("GetChildren", InstanceFlags);
                if (resolver is not null && getChildren?.Invoke(resolver, [groupItem]) is IEnumerable children)
                    return children.Cast<object>().ToArray();
            }
        }
        catch
        {
            // Fall back to layer-range matching if the installed YMM4 version changes its internal resolver API.
        }

        var groupLayer = ReadInt(groupItem, "Layer");
        var maxLayer = groupLayer + ReadInt(groupItem, "GroupRange");
        var isGroupOnly = ReadBool(groupItem, "IsGroupOnly");
        var group = ReadInt(groupItem, "Group");
        return items.Where(item => item.GetType() != groupItem.GetType())
            .Where(item => ReadInt(item, "Layer") > groupLayer && ReadInt(item, "Layer") <= maxLayer)
            .Where(item => !isGroupOnly || group != 0 && ReadInt(item, "Group") == group)
            .OrderBy(item => ReadInt(item, "Layer"))
            .ToArray();
    }

    public static string GetRemark(object item) => GetProperty(item, "Remark") as string ?? string.Empty;

    public static void SetRemark(object item, string remark)
    {
        var property = item.GetType().GetProperty("Remark", InstanceFlags);
        if (property?.CanWrite == true && property.PropertyType == typeof(string) && GetRemark(item) != remark)
            property.SetValue(item, remark);
    }

    private static object? FindTimelineForItem(object? timelineViewModel, object ownerItem)
    {
        if (timelineViewModel is null || GetProperty(timelineViewModel, "Items") is not IEnumerable viewModels)
            return null;
        if (!viewModels.Cast<object>().Any(viewModel => ReferenceEquals(GetProperty(viewModel, "Item"), ownerItem)))
            return null;

        for (var type = timelineViewModel.GetType(); type is not null; type = type.BaseType)
        {
            var field = type.GetField("timeline", InstanceFlags);
            if (field?.GetValue(timelineViewModel) is { } timeline) return timeline;
        }
        return null;
    }

    private static object? GetProperty(object? instance, string name) =>
        instance?.GetType().GetProperty(name, InstanceFlags)?.GetValue(instance);

    private static int ReadInt(object instance, string name)
    {
        var value = GetProperty(instance, name);
        return value is IConvertible convertible ? convertible.ToInt32(System.Globalization.CultureInfo.InvariantCulture) : 0;
    }

    private static bool ReadBool(object instance, string name) => GetProperty(instance, name) is true;
}
