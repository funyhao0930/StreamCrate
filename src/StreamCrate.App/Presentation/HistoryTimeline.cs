using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using StreamCrate.Core.Models;

namespace StreamCrate.App.Presentation;

/// <summary>
/// One day of history entries, rendered as a labelled marker plus the rows filed under it.
/// </summary>
internal sealed class HistoryDayGroup
{
    public HistoryDayGroup(DateTime day, bool isToday, IEnumerable<HistoryItem> items)
    {
        Day = day;
        IsToday = isToday;
        Label = isToday ? "今天" : day.ToString("yyyy/MM/dd");
        Items = new ObservableCollection<HistoryItem>(items);
    }

    public DateTime Day { get; }
    public bool IsToday { get; }
    public string Label { get; }
    public Visibility TodayDotVisibility => IsToday ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PastDotVisibility => IsToday ? Visibility.Collapsed : Visibility.Visible;
    public ObservableCollection<HistoryItem> Items { get; }

    /// <summary>
    /// Groups entries newest day first. Rows all share one left edge so the timeline reads as a
    /// single column against the rule.
    /// </summary>
    public static IReadOnlyList<HistoryDayGroup> Build(IEnumerable<HistoryEntry> entries, DateTime today)
    {
        var groups = new List<HistoryDayGroup>();
        var ordered = entries
            .OrderByDescending(entry => entry.CreatedAt)
            .GroupBy(entry => entry.CreatedAt.LocalDateTime.Date);

        foreach (var group in ordered)
        {
            var items = new List<HistoryItem>();
            foreach (var entry in group)
            {
                items.Add(new HistoryItem(entry));
            }

            groups.Add(new HistoryDayGroup(group.Key, group.Key == today, items));
        }

        return groups;
    }

}

/// <summary>
/// Picks the plain or the failure-tinted row template for a history entry.
/// </summary>
public sealed partial class HistoryEntryTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Normal { get; set; }
    public DataTemplate? Failed { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) =>
        item is HistoryItem { IsFailed: true } ? Failed : Normal;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        SelectTemplateCore(item);
}
