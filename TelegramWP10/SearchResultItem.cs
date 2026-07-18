using System.ComponentModel;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media.Imaging;

namespace TelegramWP10
{
    public class SearchResultItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        private void Notify(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

        public enum ResultType { Chat, Message, Header }
        public ResultType Type { get; set; }
        public bool IsHeader => Type == ResultType.Header;

        public long ChatId { get; set; }
        public string Title { get; set; } = "";
        public string Subtitle { get; set; } = "";

        private BitmapImage _photo;
        public BitmapImage Photo {
            get => _photo;
            set { _photo = value; Notify("Photo"); }
        }

        public long MessageId { get; set; }
        public string DateText { get; set; } = "";
        public Visibility DateVisibility => string.IsNullOrEmpty(DateText) ? Visibility.Collapsed : Visibility.Visible;
    }
}
