using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace MinecraftServerManager.App.Infrastructure;

/// <summary>
/// An observable collection whose complete projection can be replaced with one reset event.
/// WPF performs collection-view, layout, virtualization and auto-scroll work for every normal
/// <see cref="ObservableCollection{T}"/> mutation; console bursts therefore must not publish one
/// notification per line.
/// </summary>
internal sealed class BatchObservableCollection<T> : ObservableCollection<T>
{
    public bool ReplaceAll(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var replacement = items as IReadOnlyList<T> ?? items.ToArray();
        if (HasSameItems(replacement))
        {
            return false;
        }

        CheckReentrancy();
        Items.Clear();
        for (var index = 0; index < replacement.Count; index++)
        {
            Items.Add(replacement[index]);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        return true;
    }

    private bool HasSameItems(IReadOnlyList<T> replacement)
    {
        if (Count != replacement.Count)
        {
            return false;
        }

        var comparer = EqualityComparer<T>.Default;
        for (var index = 0; index < replacement.Count; index++)
        {
            if (!comparer.Equals(Items[index], replacement[index]))
            {
                return false;
            }
        }

        return true;
    }
}
