using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Artemis.App.ViewModels;

/// <summary>An ObservableCollection that can swap its whole content with one notification, so big lists don't re-layout per row.</summary>
public sealed class BulkCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
