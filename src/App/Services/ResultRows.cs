using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace DocumentTextRecognition.Services;

/// <summary>
/// Строки для виртуальной таблицы. Новая порция вызывает одно уведомление, а не сотни;
/// смена фильтра заменяет набор одним сбросом. Объекты строк сохраняются для выбора и карточки.
/// Используется только из потока интерфейса.
/// </summary>
internal sealed class ResultRows : ObservableCollection<ResultRow>
{
    public void AddRange(IReadOnlyList<ResultRow> rows)
    {
        if (rows.Count == 0) return;
        CheckReentrancy();
        var start = Count;
        var added = rows.ToArray();
        foreach (var row in added) Items.Add(row);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, (IList)added, start));
    }

    public void ReplaceAll(IReadOnlyList<ResultRow> rows)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var row in rows) Items.Add(row);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
