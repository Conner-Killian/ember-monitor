using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Ember.ViewModels;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public static class CollectionSync
{
    /// <summary>
    /// Synchronise an ObservableCollection with a keyed source list, updating existing items in place
    /// (so bound visuals and animations are preserved) and keeping source order.
    /// </summary>
    public static void Sync<TItem, TSrc>(ObservableCollection<TItem> target, IReadOnlyList<TSrc> source,
        Func<TSrc, string> key, Func<TItem, string> itemKey, Func<TSrc, TItem> create, Action<TItem, TSrc> update)
    {
        var wanted = new HashSet<string>();
        foreach (var s in source) wanted.Add(key(s));
        for (int i = target.Count - 1; i >= 0; i--)
            if (!wanted.Contains(itemKey(target[i]))) target.RemoveAt(i);

        for (int i = 0; i < source.Count; i++)
        {
            var k = key(source[i]);
            int idx = -1;
            for (int j = i; j < target.Count; j++) if (itemKey(target[j]) == k) { idx = j; break; }
            if (idx == -1)
            {
                var item = create(source[i]);
                update(item, source[i]);
                target.Insert(i, item);
            }
            else
            {
                if (idx != i) target.Move(idx, i);
                update(target[i], source[i]);
            }
        }
    }
}
