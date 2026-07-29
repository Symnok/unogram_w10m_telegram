using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media.Imaging;

namespace TelegramWP10
{
    public class InlineButton
    {
        public string Text { get; set; }
        public string CallbackData { get; set; } // null если не callback
        public string Url { get; set; }          // null если не url
    }

    public class InlineButtonRow
    {
        public List<InlineButton> Buttons { get; set; } = new List<InlineButton>();
    }

    public class MessageEntity {
        public int Offset { get; set; }
        public int Length { get; set; }
        public string Url { get; set; }
        public string Mention { get; set; } // @username или @idXXX для mentionName
    }

    public class MessageItem : INotifyPropertyChanged
    {
        private long _id;
        // Уведомляем об изменении — Id меняется в рантайме при подтверждении
        // отправки (временный id → постоянный), и всё, что на него забинжено
        // (например Tag="{Binding Id}" у кнопки play), должно перечитаться.
        public long Id { get => _id; set { _id = value; OnPropertyChanged("Id"); } }
        private string _text;
        public string Text { get => _text; set { _text = value; OnPropertyChanged("Text"); } }
        public string Date { get; set; }
        public HorizontalAlignment Alignment { get; set; }
        private string _background = "#333333";
        public string Background {
            get => _background;
            set { _background = value; OnPropertyChanged("Background"); OnPropertyChanged("TextColor"); OnPropertyChanged("TimeColor"); }
        }
        // Цвет текста — тёмный для светлых пузырей
        public string TextColor => (_background == "#FFFFFF" || _background == "#EFFDDE") ? "#000000" : "#FFFFFF";
        public string TimeColor => (_background == "#FFFFFF" || _background == "#EFFDDE") ? "#70B15C" : "#CCFFFFFF";
        // Цитата: светло-розовый в светлой теме, полупрозрачный тёмный в тёмной
        public string ReplyBackground   => (_background == "#FFFFFF" || _background == "#EFFDDE") ? "#FADADD" : "#44000000";
        public string ReplyBorderBrush  => (_background == "#FFFFFF" || _background == "#EFFDDE") ? "#E07090" : "#88FFFFFF";
        public string ReplyAuthorColor  => (_background == "#FFFFFF" || _background == "#EFFDDE") ? "#C0306A" : "#7EC8E3";
        public string ReplyTextColor    => (_background == "#FFFFFF" || _background == "#EFFDDE") ? "#000000" : "#CCCCCC";
        public string FilePath { get; set; } // путь к файлу видео для открытия

        // Группировка фото-альбомов (media_album_id) — "Вариант А": визуально
        // слепленные подряд идущие пузыри одного отправителя без реальной
        // мозаики. AlbumId = "" или "0" — сообщение не часть альбома.
        public string AlbumId { get; set; } = "";

        private bool _isFirstInGroup = true;
        public bool IsFirstInGroup {
            get => _isFirstInGroup;
            set { _isFirstInGroup = value; OnPropertyChanged("IsFirstInGroup"); OnPropertyChanged("BubbleCornerRadius"); OnPropertyChanged("GroupMargin"); }
        }
        private bool _isLastInGroup = true;
        public bool IsLastInGroup {
            get => _isLastInGroup;
            set {
                _isLastInGroup = value;
                OnPropertyChanged("IsLastInGroup");
                OnPropertyChanged("BubbleCornerRadius");
                OnPropertyChanged("SenderNameVisibility");
                OnPropertyChanged("SenderAvatarVisibility");
                OnPropertyChanged("TimeVisibility");
            }
        }
        // Скругления: полные у самостоятельного сообщения, "срезанные" на стыке
        // со следующим/предыдущим фото того же альбома — единый визуальный блок.
        public string BubbleCornerRadius {
            get {
                string top = _isFirstInGroup ? "10" : "3";
                string bottom = _isLastInGroup ? "10" : "3";
                return top + "," + top + "," + bottom + "," + bottom;
            }
        }
        // Отступ сверху — только у первого в группе обычный, у слепленных — минимальный
        public string GroupMargin => "0," + (_isFirstInGroup ? "4" : "1") + ",0,0";

        private string _replyToText;
        public string ReplyToText { get => _replyToText; set { _replyToText = value; OnPropertyChanged("ReplyToText"); OnPropertyChanged("ReplyVisibility"); } }
        public Visibility ReplyVisibility => !string.IsNullOrEmpty(ReplyToText) ? Visibility.Visible : Visibility.Collapsed;

        private Windows.UI.Xaml.Media.ImageSource _attachedPhoto;
        public Windows.UI.Xaml.Media.ImageSource AttachedPhoto { get => _attachedPhoto; set { _attachedPhoto = value; OnPropertyChanged("AttachedPhoto"); OnPropertyChanged("PhotoVisibility"); } }
        // PhotoVisibility: показываем если есть превью ИЛИ это обычное видео (не GIF) — и не видео-стикер (тот рисуется отдельным MediaElement)
        public Visibility PhotoVisibility => (!IsStickerVideo && (AttachedPhoto != null || (IsVideo && !IsGif))) ? Visibility.Visible : Visibility.Collapsed;

        // Видео-стикер (WEBM) — своей миниатюрой TDLib присылает не картинку, а
        // короткий mp4-клип (thumbnailFormatMpeg4). VP9/WebM целиком софтверно
        // не декодируем — это отдельная большая задача, а вот H.264/MP4 этой
        // миниатюры штатно тянет MediaElement, поэтому проигрываем её так же,
        // как обычный GIF.
        private bool _isStickerVideo = false;
        public bool IsStickerVideo {
            get => _isStickerVideo;
            set { _isStickerVideo = value; OnPropertyChanged("IsStickerVideo"); OnPropertyChanged("StickerVideoVisibility"); OnPropertyChanged("PhotoVisibility"); }
        }
        public Visibility StickerVideoVisibility => _isStickerVideo ? Visibility.Visible : Visibility.Collapsed;

        private Uri _stickerVideoSource;
        public Uri StickerVideoSource { get => _stickerVideoSource; set { _stickerVideoSource = value; OnPropertyChanged("StickerVideoSource"); } }

        private bool _isVideo;
        public bool IsVideo { get => _isVideo; set { _isVideo = value; OnPropertyChanged("IsVideo"); OnPropertyChanged("VideoIconVisibility"); OnPropertyChanged("PhotoVisibility"); } }
        public bool IsSticker { get; set; } = false;
        public double PhotoMaxWidth => IsSticker ? 128 : 250;
        // Стикеры — прозрачный пузырь без padding
        public string BubbleBackground => IsSticker ? "Transparent" : _background;
        public string BubblePadding    => IsSticker ? "0" : "8,5";
        public string TimeVisibility   => (IsSticker || !_isLastInGroup) ? "Collapsed" : "Visible";
        // VideoIcon показываем только для обычного видео (не GIF), когда нет прогресса скачивания
        public Visibility VideoIconVisibility => (IsVideo && !IsGif) ? Visibility.Visible : Visibility.Collapsed;

        // GIF: отдельный плеер через MediaElement
        private bool _isGif;
        public bool IsGif { get => _isGif; set { _isGif = value; OnPropertyChanged("IsGif"); OnPropertyChanged("GifPlayerVisibility"); OnPropertyChanged("PhotoVisibility"); OnPropertyChanged("VideoIconVisibility"); } }

        private Uri _gifSource;
        public Uri GifSource { get => _gifSource; set { _gifSource = value; OnPropertyChanged("GifSource"); OnPropertyChanged("GifPlayerVisibility"); } }
        // GifPlayer показываем если это GIF (есть source или грузится)
        public Visibility GifPlayerVisibility => IsGif ? Visibility.Visible : Visibility.Collapsed;

        // Документ
        private bool _isDocument;
        public bool IsDocument { get => _isDocument; set { _isDocument = value; OnPropertyChanged("IsDocument"); OnPropertyChanged("DocumentVisibility"); } }
        public Visibility DocumentVisibility => IsDocument ? Visibility.Visible : Visibility.Collapsed;

        // Опрос
        private bool _isPoll = false;
        public bool IsPoll { get => _isPoll; set { _isPoll = value; OnPropertyChanged("IsPoll"); OnPropertyChanged("PollVisibility"); } }
        public Visibility PollVisibility => _isPoll ? Visibility.Visible : Visibility.Collapsed;
        public string PollQuestion { get; set; } = "";
        public string PollType { get; set; } = ""; // "📊 Опрос" или "🔒 Анонимный опрос" и т.п.
        public System.Collections.ObjectModel.ObservableCollection<PollOptionItem> PollOptions { get; set; }
            = new System.Collections.ObjectModel.ObservableCollection<PollOptionItem>();

        public string DocumentName { get; set; }
        public string DocumentSize { get; set; }

        private string _downloadStatus = "⬇ Скачать";
        public string DownloadStatus { get => _downloadStatus; set { _downloadStatus = value; OnPropertyChanged("DownloadStatus"); } }

        private bool _isDownloaded = false;
        public bool IsDownloaded { get => _isDownloaded; set { _isDownloaded = value; OnPropertyChanged("IsDownloaded"); OnPropertyChanged("DownloadStatus"); } }

        // Реакции
        private string _reactions = "";
        public string Reactions { get => _reactions; set { _reactions = value; OnPropertyChanged("Reactions"); OnPropertyChanged("ReactionsVisibility"); } }
        public Visibility ReactionsVisibility => !string.IsNullOrEmpty(_reactions) ? Visibility.Visible : Visibility.Collapsed;

        // Статус прочтения
        private bool _isOutgoing = false;
        private bool _isRead = false;
        public bool IsOutgoing { get => _isOutgoing; set { _isOutgoing = value; OnPropertyChanged("IsOutgoing"); OnPropertyChanged("ReadStatusVisibility"); OnPropertyChanged("ReadStatusText"); OnPropertyChanged("SenderNameVisibility"); OnPropertyChanged("SenderAvatarVisibility"); } }
        public bool IsRead { get => _isRead; set { _isRead = value; OnPropertyChanged("IsRead"); OnPropertyChanged("ReadStatusText"); OnPropertyChanged("ReadStatusColor"); } }
        public Visibility ReadStatusVisibility => _isOutgoing ? Visibility.Visible : Visibility.Collapsed;
        public string ReadStatusText => _isRead ? "✓✓" : "✓";

        // Метка "изменено" — TDLib присылает edit_date в каждом сообщении
        private bool _isEdited = false;
        public bool IsEdited { get => _isEdited; set { _isEdited = value; OnPropertyChanged("IsEdited"); OnPropertyChanged("EditedVisibility"); } }
        public Visibility EditedVisibility => _isEdited ? Visibility.Visible : Visibility.Collapsed;
        // Прочитанные — зелёные в светлой теме, белые в тёмной; непрочитанные — серые
        public string ReadStatusColor {
            get {
                bool isLight = _background == "#FFFFFF" || _background == "#EFFDDE";
                if (_isRead) return isLight ? "#4CAF50" : "#CCFFFFFF";
                return isLight ? "#AAAAAA" : "#88FFFFFF";
            }
        }

        // Ник отправителя (для групп, входящих)
        private string _senderName = "";
        public string SenderName { get => _senderName; set { _senderName = value; OnPropertyChanged("SenderName"); OnPropertyChanged("SenderNameVisibility"); OnPropertyChanged("SenderAvatarInitials"); } }
        public Visibility SenderNameVisibility => !string.IsNullOrEmpty(_senderName) && !_isOutgoing && _isLastInGroup ? Visibility.Visible : Visibility.Collapsed;

        public string SenderColor { get; set; } = "#7EC8E3";

        // Аватарка отправителя (та же видимость, что и у ника — входящие в группах)
        public long SenderUserId { get; set; } = 0;
        public string SenderAvatarColor => AvatarPlaceholder.GetColor(SenderUserId);
        public string SenderAvatarInitials => AvatarPlaceholder.GetInitials(_senderName);
        public Visibility SenderAvatarVisibility => !_isOutgoing && _isLastInGroup ? Visibility.Visible : Visibility.Collapsed;

        private BitmapImage _senderPhoto;
        public BitmapImage SenderPhoto {
            get => _senderPhoto;
            set { _senderPhoto = value; OnPropertyChanged("SenderPhoto"); OnPropertyChanged("SenderAvatarPlaceholderVisibility"); }
        }
        public Visibility SenderAvatarPlaceholderVisibility => _senderPhoto == null ? Visibility.Visible : Visibility.Collapsed;

        // Ник автора цитаты
        public string ReplyAuthor { get; set; } = "";
        public string ReplyAuthorVisibility => !string.IsNullOrEmpty(ReplyAuthor) ? "Visible" : "Collapsed";

        // Аудио
        private bool _isAudio = false;
        private string _audioDuration = "";
        private string _audioTitle = "";
        private string _audioPlayStatus = "▶";
        private double _audioPosition = 0;
        private double _audioDurationSeconds = 1;
        private string _audioPositionText = "0:00";
        public bool IsAudio { get => _isAudio; set { _isAudio = value; OnPropertyChanged("IsAudio"); OnPropertyChanged("AudioVisibility"); } }
        public string AudioDuration { get => _audioDuration; set { _audioDuration = value; OnPropertyChanged("AudioDuration"); } }
        public string AudioTitle { get => _audioTitle; set { _audioTitle = value; OnPropertyChanged("AudioTitle"); } }
        public string AudioPlayStatus { get => _audioPlayStatus; set { _audioPlayStatus = value; OnPropertyChanged("AudioPlayStatus"); } }
        public double AudioPosition { get => _audioPosition; set { _audioPosition = value; OnPropertyChanged("AudioPosition"); } }
        public double AudioDurationSeconds { get => _audioDurationSeconds; set { _audioDurationSeconds = value > 0 ? value : 1; OnPropertyChanged("AudioDurationSeconds"); } }
        public string AudioPositionText { get => _audioPositionText; set { _audioPositionText = value; OnPropertyChanged("AudioPositionText"); } }
        public Visibility AudioVisibility => _isAudio ? Visibility.Visible : Visibility.Collapsed;

        // Inline-кнопки
        private ObservableCollection<InlineButtonRow> _inlineButtons = new ObservableCollection<InlineButtonRow>();
        public ObservableCollection<InlineButtonRow> InlineButtons { get => _inlineButtons; set { _inlineButtons = value; OnPropertyChanged("InlineButtons"); OnPropertyChanged("InlineButtonsVisibility"); } }
        public Visibility InlineButtonsVisibility => _inlineButtons != null && _inlineButtons.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        private string _videoDownloadProgress;
        public string VideoDownloadProgress { get => _videoDownloadProgress; set { _videoDownloadProgress = value; OnPropertyChanged("VideoDownloadProgress"); OnPropertyChanged("VideoProgressVisibility"); } }
        public Visibility VideoProgressVisibility => !string.IsNullOrEmpty(_videoDownloadProgress) ? Visibility.Visible : Visibility.Collapsed;

        // Полный file_id фото для загрузки полноразмерной версии
        public long FullPhotoFileId { get; set; }
        // Entities для ссылок
        public List<MessageEntity> Entities { get; set; }

        // Дата сообщения для вычисления разделителей
        public DateTime RawDate { get; set; }

        // Разделитель дат между сообщениями разных дней
        public bool IsSeparator { get; set; } = false;
        public bool IsUnreadSeparator { get; set; } = false;

        // Link Preview
        private string _linkPreviewUrl = "";
        private string _linkPreviewSiteName = "";
        private string _linkPreviewTitle = "";
        private string _linkPreviewDescription = "";

        public string LinkPreviewUrl { get => _linkPreviewUrl; set { _linkPreviewUrl = value; OnPropertyChanged("LinkPreviewUrl"); OnPropertyChanged("LinkPreviewVisibility"); } }
        public string LinkPreviewSiteName { get => _linkPreviewSiteName; set { _linkPreviewSiteName = value; OnPropertyChanged("LinkPreviewSiteName"); OnPropertyChanged("LinkPreviewSiteVisibility"); } }
        public string LinkPreviewTitle { get => _linkPreviewTitle; set { _linkPreviewTitle = value; OnPropertyChanged("LinkPreviewTitle"); OnPropertyChanged("LinkPreviewTitleVisibility"); } }
        public string LinkPreviewDescription { get => _linkPreviewDescription; set { _linkPreviewDescription = value; OnPropertyChanged("LinkPreviewDescription"); OnPropertyChanged("LinkPreviewDescriptionVisibility"); } }
        public Visibility LinkPreviewVisibility => !string.IsNullOrEmpty(_linkPreviewUrl) ? Visibility.Visible : Visibility.Collapsed;
        public Visibility LinkPreviewSiteVisibility => !string.IsNullOrEmpty(_linkPreviewSiteName) ? Visibility.Visible : Visibility.Collapsed;
        public Visibility LinkPreviewTitleVisibility => !string.IsNullOrEmpty(_linkPreviewTitle) ? Visibility.Visible : Visibility.Collapsed;
        public Visibility LinkPreviewDescriptionVisibility => !string.IsNullOrEmpty(_linkPreviewDescription) ? Visibility.Visible : Visibility.Collapsed;
        public string SeparatorLabel { get; set; } = "";
        public Visibility SeparatorVisibility => IsSeparator ? Visibility.Visible : Visibility.Collapsed;
        public Visibility MessageVisibility => IsSeparator ? Visibility.Collapsed : Visibility.Visible;
        // Разделитель непрочитанных — другой цвет фона
        public string SeparatorBackground => IsUnreadSeparator ? "#CC2AABEE" : "#AA333333";
        public string SeparatorTextColor => "#FFFFFF";

        // Пересланное сообщение — имя оригинального отправителя
        private string _forwardedFrom = "";
        public string ForwardedFrom {
            get => _forwardedFrom;
            set { _forwardedFrom = value; OnPropertyChanged("ForwardedFrom"); OnPropertyChanged("ForwardedVisibility"); }
        }
        public Visibility ForwardedVisibility => !string.IsNullOrEmpty(_forwardedFrom) ? Visibility.Visible : Visibility.Collapsed;

        // Комментарии к посту канала
        private int _replyCount = -1; // -1 = нет комментариев/не канал
        public int ReplyCount {
            get => _replyCount;
            set { _replyCount = value; OnPropertyChanged("ReplyCount"); OnPropertyChanged("CommentsText"); OnPropertyChanged("CommentsVisibility"); }
        }
        public string CommentsText => _replyCount > 0 ? "💬 " + _replyCount + " комментариев" : "💬 Оставить комментарий";
        public Visibility CommentsVisibility => _replyCount >= 0 ? Visibility.Visible : Visibility.Collapsed;

        public event PropertyChangedEventHandler PropertyChanged;
        public void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class PollOptionItem : INotifyPropertyChanged
    {
        public int OptionId { get; set; } = 0;
        public long MsgId   { get; set; } = 0;
        public string TextColor { get; set; } = "#FFFFFF"; // передаётся из MessageItem

        private string _text = "";
        public string Text { get => _text; set { _text = value; OnPropertyChanged("Text"); } }

        private int _voteCount = 0;
        public int VoteCount { get => _voteCount; set { _voteCount = value; OnPropertyChanged("VoteCount"); OnPropertyChanged("VoteText"); } }

        private int _percent = 0;
        public int Percent { get => _percent; set { _percent = value; OnPropertyChanged("Percent"); OnPropertyChanged("PercentText"); OnPropertyChanged("BarWidth"); } }

        private bool _isChosen = false;
        public bool IsChosen { get => _isChosen; set { _isChosen = value; OnPropertyChanged("IsChosen"); OnPropertyChanged("BarColor"); } }

        public string PercentText => _percent + "%";
        public string VoteText    => _voteCount > 0 ? _voteCount + " гол." : "";
        public string BarColor    => _isChosen ? "#0088cc" : "#555555";

        public event PropertyChangedEventHandler PropertyChanged;
        public void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
