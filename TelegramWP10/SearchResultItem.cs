using System.ComponentModel;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media.Imaging;

namespace TelegramWP10
{
    public class SearchResultItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        private void Notify(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

        public enum ResultType { Chat, Message, Header, Divider }
        public ResultType Type { get; set; }
        public bool IsHeader => Type == ResultType.Header;
        public bool IsDivider => Type == ResultType.Divider;
        public Visibility HeaderVisibility => Type == ResultType.Header ? Visibility.Visible : Visibility.Collapsed;
        public Visibility DividerVisibility => Type == ResultType.Divider ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ItemVisibility => (Type == ResultType.Chat || Type == ResultType.Message) ? Visibility.Visible : Visibility.Collapsed;

        public long ChatId { get; set; }
        public string Title { get; set; } = "";
        public string Subtitle { get; set; } = "";

        // Цвет заголовка следует текущей теме — переиспользуем то же статическое
        // поле, что ChatItem, чтобы не заводить второй источник истины.
        // NotifyTitleColor() дёргается при смене темы для всех элементов списка.
        public string TitleColor => ChatItem.ThemeTitleColor;
        public void NotifyTitleColor() => Notify("TitleColor");

        private BitmapImage _photo;
        public BitmapImage Photo {
            get => _photo;
            set { _photo = value; Notify("Photo"); Notify("NoPhotoVisibility"); }
        }
        public Visibility NoPhotoVisibility => _photo == null ? Visibility.Visible : Visibility.Collapsed;

        // Заглушка при отсутствии фото: цвет из палитры по ChatId + инициалы из Title
        public string AvatarColor => AvatarPlaceholder.GetColor(ChatId);
        public string AvatarInitials => AvatarPlaceholder.GetInitials(Title);

        public long MessageId { get; set; }
        public string DateText { get; set; } = "";
        public Visibility DateVisibility => string.IsNullOrEmpty(DateText) ? Visibility.Collapsed : Visibility.Visible;
    }
}
