using Windows.UI.Xaml;
using Windows.UI.Xaml.Media.Imaging;

namespace TelegramWP10
{
    public class SearchResultItem
    {
        public enum ResultType { Chat, Message, Header }

        public ResultType Type { get; set; }

        // Для чатов
        public long ChatId { get; set; }
        public string Title { get; set; } = "";
        public string Subtitle { get; set; } = "";
        public BitmapImage Photo { get; set; }

        // Для сообщений
        public long MessageId { get; set; }
        public string DateText { get; set; } = "";

        // Заголовок секции
        public bool IsHeader => Type == ResultType.Header;
        public Visibility DateVisibility => string.IsNullOrEmpty(DateText) ? Visibility.Collapsed : Visibility.Visible;
    }
}
