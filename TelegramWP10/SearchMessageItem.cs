using Windows.UI.Xaml.Media.Imaging;

namespace TelegramWP10
{
    public class SearchMessageItem
    {
        public long ChatId     { get; set; }
        public long MessageId  { get; set; }
        public string ChatTitle   { get; set; }
        public string MessageText { get; set; }
        public string DateText    { get; set; }
        public BitmapImage ChatPhoto { get; set; }
    }
}
