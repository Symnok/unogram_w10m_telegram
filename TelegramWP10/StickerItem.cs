using System.ComponentModel;
using Windows.UI.Xaml.Media.Imaging;

namespace TelegramWP10
{
    public class StickerItem : INotifyPropertyChanged
    {
        public long SetId       { get; set; }
        public long FileId      { get; set; }
        public long ThumbFileId { get; set; }
        public string RemoteFileId { get; set; }

        private BitmapImage _thumb;
        public BitmapImage Thumb {
            get => _thumb;
            set { _thumb = value; OnPropertyChanged("Thumb"); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public void OnPropertyChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
