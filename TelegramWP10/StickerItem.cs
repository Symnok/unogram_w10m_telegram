using Windows.UI.Xaml.Media.Imaging;

namespace TelegramWP10
{
    public class StickerItem
    {
        public long SetId       { get; set; }
        public long FileId      { get; set; }
        public long ThumbFileId { get; set; }
        public string RemoteFileId { get; set; }
        public BitmapImage Thumb { get; set; }
    }
}
