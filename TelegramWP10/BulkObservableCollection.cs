using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace TelegramWP10
{
    public class BulkObservableCollection<T> : ObservableCollection<T>
    {
        private bool _suppressNotifications = false;

        protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e) {
            if (!_suppressNotifications)
                base.OnCollectionChanged(e);
        }

        protected override void OnPropertyChanged(PropertyChangedEventArgs e) {
            if (!_suppressNotifications)
                base.OnPropertyChanged(e);
        }

        // Вставляем диапазон в начало — один Reset вместо N нотификаций
        public void InsertRangeAt(int index, IList<T> items) {
            _suppressNotifications = true;
            for (int i = 0; i < items.Count; i++)
                Insert(index + i, items[i]);
            _suppressNotifications = false;
            // Один Reset на весь батч
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
            OnPropertyChanged(new PropertyChangedEventArgs("Count"));
        }

        // Добавляем диапазон в конец — один Reset
        public void AddRange(IList<T> items) {
            _suppressNotifications = true;
            foreach (var item in items)
                Add(item);
            _suppressNotifications = false;
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
            OnPropertyChanged(new PropertyChangedEventArgs("Count"));
        }

        // Удаляем диапазон — один Reset
        public void RemoveRange(int index, int count) {
            _suppressNotifications = true;
            for (int i = 0; i < count && index < Count; i++)
                RemoveAt(index);
            _suppressNotifications = false;
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
            OnPropertyChanged(new PropertyChangedEventArgs("Count"));
        }
    }
}
