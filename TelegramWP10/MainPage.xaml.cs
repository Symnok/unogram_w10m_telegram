using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Newtonsoft.Json.Linq;

namespace TelegramWP10
{
    public sealed partial class MainPage : Page
    {
        private IntPtr _client;
        private ObservableCollection<ChatItem> _chatListItems = new ObservableCollection<ChatItem>();
        private List<ChatItem> _allChatItems = new List<ChatItem>(); // все чаты для фильтрации
        private int _currentFolderId = -1;
        private Dictionary<int, List<long>> _folderChatIds = new Dictionary<int, List<long>>();
        private int _pendingFolderLoad = 0;
        private Queue<int> _folderLoadQueue = new Queue<int>();

        private void LoadNextFolder() {
            if (_folderLoadQueue.Count == 0 || _pendingFolderLoad != 0) return;
            _pendingFolderLoad = _folderLoadQueue.Dequeue();
            TdJson.SendUtf8(_client, "{\"@type\":\"getChats\",\"chat_list\":{\"@type\":\"chatListFolder\",\"chat_folder_id\":" + _pendingFolderLoad + "},\"limit\":100}");
        }
        private ObservableCollection<MessageItem> _messageItems = new ObservableCollection<MessageItem>();
        private Dictionary<long, ChatItem> _chatsDict = new Dictionary<long, ChatItem>();
        private Dictionary<long, JToken> _rawChatsDict = new Dictionary<long, JToken>(); // сырой JSON чата
        private Dictionary<long, JToken> _usersDict = new Dictionary<long, JToken>();
        private Dictionary<long, JToken> _supergroupDict = new Dictionary<long, JToken>();
        private Dictionary<long, long> _fileToChatId = new Dictionary<long, long>();
        private Dictionary<long, SearchResultItem> _fileToSearchResult = new Dictionary<long, SearchResultItem>();
        private Dictionary<long, bool> _pendingPinnedPositions = new Dictionary<long, bool>(); // chatId → isPinned до updateNewChat
        private Dictionary<long, long> _fileToMsgId = new Dictionary<long, long>();
        private Dictionary<string, long> _remoteUniqueIdToMsgId = new Dictionary<string, long>(); // remote.unique_id → msgId
        private Dictionary<long, long> _videoFileIds = new Dictionary<long, long>(); // file_id → msgId только для видеофайлов
        private Dictionary<long, MessageItem> _messagesDict = new Dictionary<long, MessageItem>();
        // replyMsgId → MessageItem которому нужно заполнить ReplyToText
        private Dictionary<long, MessageItem> _replyRequests = new Dictionary<long, MessageItem>();
        private long _currentChatId = 0;
        private long _myUserId = 0;
        private bool _waitingForMe = false;
        private bool _contactsPendingMyId = false;
        private long _fullPhotoMsgId = 0;
        private long _threadMessageId = 0;
        private long _threadChatId = 0;
        private bool _currentChatIsGroup = false;
        private Windows.UI.Xaml.DispatcherTimer _statusTimer;
        private Windows.UI.Xaml.DispatcherTimer _audioPositionTimer;
        private Windows.UI.Xaml.DispatcherTimer _typingTimer;
        private bool _audioSliderDragging = false;
        private long _pendingHistoryChatId = 0;
        private int _historyRetryCount = 0;
        private bool _loadingOlderHistory = false;
        private bool _hasMoreHistory = true;
        private bool _trimming = false;
        private bool _outOfMemory = false;
        private const ulong MemoryThreshold = 400 * 1024 * 1024;
        private Windows.UI.Xaml.DispatcherTimer _restoreTimer = null;
        private ItemsStackPanel _messagesStackPanel = null;

        private void SetScrollMode(ItemsUpdatingScrollMode mode) {
            if (_messagesStackPanel == null)
                _messagesStackPanel = FindVisualChild<ItemsStackPanel>(MessagesListView);
            if (_messagesStackPanel != null)
                _messagesStackPanel.ItemsUpdatingScrollMode = mode;
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject {
            int n = Windows.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < n; i++) {
                var c = Windows.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
                if (c is T t) return t;
                var r = FindVisualChild<T>(c);
                if (r != null) return r;
            }
            return null;
        }
        private Windows.UI.Xaml.DispatcherTimer _scrollTimer;
        private bool _autoScrolling = false;
        private long _pendingStickerFileId = 0;
        private long _pendingStickerChatId = 0;
        // uploadFile pending
        private string _pendingUploadType = ""; // "doc" или "voice"
        private long   _pendingUploadChatId = 0; // true пока идёт автоскролл вниз после загрузки
        private long _currentChatOutboxReadId = 0;
        private long _lastReadInboxMsgId = 0;
        private long _pinnedMessageId = 0;
        private long _pendingPinnedChatId = 0;
        private bool _currentChatIsBot = false;
        private long _pendingScrollToMsgId = 0; // скролл к сообщению после открытия чата
        private Dictionary<long, long> _pinnedTextRequests = new Dictionary<long, long>(); // pinnedMsgId → serviceMsgId
        private bool _loadingChats = false;
        private bool _mainListLoaded = false; // основной список полностью загружен
        private Queue<long> _pendingChatIds = new Queue<long>();
        private string _dbPath = "";
        private bool _connectionReady = false;
        private bool _isAuthorized = false;
        private bool _isLoadingHistory = false;

        // MTProxy
        private class ProxyEntry { public string Host; public int Port; public string Secret; }
        private List<ProxyEntry> _proxyList = new List<ProxyEntry>();
        private int _proxyIndex = 0;
        private int _currentProxyId = 0;
        private Windows.UI.Xaml.DispatcherTimer _proxyTimer;
        private Windows.UI.Xaml.DispatcherTimer _connectingTimer; // таймер 10с на подключение
        private bool _proxyConnected = false;
        private bool _proxyApplied = false;
        private bool _soundEnabled = true; // звук уведомлений
        // Стикеры
        private bool _stickerPanelOpen = false;
        private List<ContactItem> _contactItems = new List<ContactItem>();
        private long _pendingContactUserId = 0; // userId ожидающий createPrivateChat
        private List<StickerItem> _currentStickerItems = new List<StickerItem>();
        private Dictionary<long, long> _stickerThumbToItem = new Dictionary<long, long>(); // thumbFileId → fileId
        private List<long> _loadedStickerSetIds = new List<long>(); // чтобы не применять дважды

        // Режим прокси
        private enum ProxyMode { None, Auto, Mtproto, Http, Socks }
        private ProxyMode _proxyMode = ProxyMode.None;
        private bool _isLightTheme = false;
        // Цвета пузырей — обновляются при смене темы
        internal static string BubbleColorOut = "#0088cc";
        internal static string BubbleColorIn  = "#333333"; // по умолчанию — прямое подключение
        private bool _isRecording = false;
        private Windows.Media.Capture.MediaCapture _mediaCapture = null;
        private Windows.Storage.StorageFile _recordingFile = null;
        private Windows.Media.Capture.MediaCapture _videoCaptureCapture = null;
        private Windows.Storage.StorageFile _videoNoteFile = null;
        private Windows.UI.Xaml.DispatcherTimer _videoNoteTimer = null;
        private int _videoNoteSeconds = 0;
        private const int MaxVideoNoteSeconds = 60;
        private Windows.Media.Playback.MediaPlayer _currentAudioPlayer = null;
        private long _currentAudioMsgId = 0;
        private Windows.Media.Core.MediaSource _currentAudioSource = null;
        private TimeSpan _currentAudioPosition = TimeSpan.Zero;
        private string _currentAudioFilePath = null;
        private Windows.ApplicationModel.ExtendedExecution.ExtendedExecutionSession _mediaSession = null;
        private long _pendingDeleteChatId = 0;
        private StorageFolder _filesFolder = null;
        private StorageFile _logFile = null;
        private ObservableCollection<ChatItem> _archiveChatItems = new ObservableCollection<ChatItem>();
        private bool _inArchive = false;
        private bool _archiveLoaded = false;
        private bool _loadingArchive = false;
        private bool _loadingArchiveIds = false;   // pre-fetch id архива до загрузки главного
        private HashSet<long> _archiveChatIds = new HashSet<long>(); // id чатов архива
        private HashSet<long> _pendingGetChat = new HashSet<long>(); // id запрошенных через getChat из LoadNextChat

        public MainPage()
        {
            this.InitializeComponent();
            _client = TdJson.td_json_client_create();
            ChatListView.ItemsSource = _chatListItems;
            MessagesListView.ItemsSource = _messageItems;
            // Загружаем сохранённый режим прокси до старта TDLib
            var ls = Windows.Storage.ApplicationData.Current.LocalSettings;
            if (ls.Values.ContainsKey("proxy_mode"))
                _proxyMode = (ProxyMode)(int)ls.Values["proxy_mode"];
            // Загружаем тему
            if (ls.Values.ContainsKey("light_theme"))
                _isLightTheme = (bool)ls.Values["light_theme"];
            if (ls.Values.ContainsKey("sound_enabled"))
                _soundEnabled = (bool)ls.Values["sound_enabled"];
            // Подписка на скролл идёт через x:Name="MessagesScrollViewer" в XAML — ViewChanged там же
            this.Loaded += (s, e2) => {
                if (SoundToggleItem != null)
                    SoundToggleItem.Text = _soundEnabled ? "🔔 Звук: Вкл" : "🔕 Звук: Выкл";
            };
            // ApplyTheme вызывается в Loaded когда все элементы готовы
            this.Loaded += (s, e) => ApplyTheme();
            // Сбрасываем UI в начальное состояние (на случай restore после suspend)
            LoginPanel.Visibility = Visibility.Visible;
            ChatListView.Visibility = Visibility.Collapsed;
            MessagesPanel.Visibility = Visibility.Collapsed;
            StartPanel.Visibility = Visibility.Visible;
            LoadingIndicator.Visibility = Visibility.Collapsed;
            MessagesListView.Visibility = Visibility.Collapsed;
            LogoutButton.Visibility = Visibility.Collapsed;
            // Таймер обновления статуса "был(а) N мин. назад"
            _statusTimer = new Windows.UI.Xaml.DispatcherTimer();
            _statusTimer.Interval = TimeSpan.FromSeconds(60);
            _statusTimer.Tick += (s, e) => {
                if (_currentChatId != 0 && _usersDict.ContainsKey(_currentChatId))
                    UpdateChatStatus(_usersDict[_currentChatId]["status"]);
            };
            // Таймер сброса "печатает..." — 5 секунд
            _typingTimer = new Windows.UI.Xaml.DispatcherTimer();
            _typingTimer.Interval = TimeSpan.FromSeconds(7);
            _typingTimer.Tick += (s, e) => {
                _typingTimer.Stop();
                if (_currentChatId != 0 && _usersDict.ContainsKey(_currentChatId))
                    UpdateChatStatus(_usersDict[_currentChatId]["status"]);
                else if (_currentChatId != 0)
                    CurrentChatStatus.Text = "";
            };
            _statusTimer.Start();
            // Таймер обновления позиции аудио (каждые 500мс)
            _audioPositionTimer = new Windows.UI.Xaml.DispatcherTimer();
            _audioPositionTimer.Interval = TimeSpan.FromMilliseconds(500);
            _audioPositionTimer.Tick += (s, e) => {
                if (_currentAudioPlayer == null || _audioSliderDragging) return;
                var session = _currentAudioPlayer.PlaybackSession;
                if (session.NaturalDuration.TotalSeconds > 0 && _messagesDict.ContainsKey(_currentAudioMsgId)) {
                    var item = _messagesDict[_currentAudioMsgId];
                    item.AudioDurationSeconds = session.NaturalDuration.TotalSeconds;
                    item.AudioPosition = session.Position.TotalSeconds;
                    var pos = session.Position;
                    item.AudioPositionText = $"{(int)pos.TotalMinutes}:{pos.Seconds:D2}";
                    _currentAudioPosition = session.Position; // сохраняем для восстановления после resume
                }
            };
            _audioPositionTimer.Start();
            // Системная кнопка "назад"
            var sysNav = Windows.UI.Core.SystemNavigationManager.GetForCurrentView();
            sysNav.BackRequested += (s, e) => {
                if (PhotoOverlay.Visibility == Visibility.Visible) {
                    PhotoOverlay.Visibility = Visibility.Collapsed;
                    PhotoOverlayImage.Source = null;
                    _fullPhotoMsgId = 0;
                    e.Handled = true;
                } else if (_currentChatId != 0) {
                    BackButton_Click(null, null);
                    e.Handled = true;
                } else if (_inArchive) {
                    ArchiveBack_Click(null, null);
                    e.Handled = true;
                }
            };
            InitAsync();
            // Логируем lifecycle приложения для диагностики фонового аудио
            Application.Current.EnteredBackground += (s, e) => { };
            Application.Current.LeavingBackground += (s, e) => { };
            Application.Current.Suspending += (s, e) => {
                // Сохраняем позицию на случай если плеер упадёт после resume
                if (_currentAudioPlayer != null)
                    _currentAudioPosition = _currentAudioPlayer.PlaybackSession.Position;
            };
            Application.Current.Resuming += async (s, e) => {
                // Если плеер упал во время suspend — восстанавливаем
                await System.Threading.Tasks.Task.Delay(1500); // ждём пока AUDIO FAILED придёт
                if (_currentAudioPlayer == null && _currentAudioFilePath != null && _messagesDict.ContainsKey(_currentAudioMsgId)) {
                    var savedMsgId = _currentAudioMsgId;
                    var savedPos = _currentAudioPosition;
                    var savedPath = _currentAudioFilePath;
                    await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, async () => {
                        try {
                            var item = _messagesDict[savedMsgId];
                            var player = new Windows.Media.Playback.MediaPlayer();
                            player.AudioCategory = Windows.Media.Playback.MediaPlayerAudioCategory.Media;
                            var source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(savedPath));
                            _currentAudioSource = source;
                            player.Source = source;
                            _currentAudioPlayer = player;
                            _currentAudioMsgId = savedMsgId;
                            SetupPlayer(player, item, savedPos);
                            player.Play();
                        } catch (Exception ex) {
                            _currentAudioPlayer = null;
                            _currentAudioSource = null;
                            _currentAudioFilePath = null;
                        }
                    });
                }
            };
        }

        private async System.Threading.Tasks.Task RequestMediaSessionAsync() {
            _mediaSession?.Dispose();
            _mediaSession = null;
            var session = new Windows.ApplicationModel.ExtendedExecution.ExtendedExecutionSession();
            session.Reason = Windows.ApplicationModel.ExtendedExecution.ExtendedExecutionReason.Unspecified;
            session.Description = "Unogram audio";
            session.Revoked += (s, e) => { };
            var result = await session.RequestExtensionAsync();
            if (result == Windows.ApplicationModel.ExtendedExecution.ExtendedExecutionResult.Allowed)
                _mediaSession = session;
            else
                session.Dispose();
        }
        private void ReleaseMediaSession() {
            _mediaSession?.Dispose();
            _mediaSession = null;
        }

        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _logQueue
            = new System.Collections.Concurrent.ConcurrentQueue<string>();
        private bool _logFlushing = false;

        private void Log(string m) {
            _logQueue.Enqueue($"[{DateTime.Now:HH:mm:ss.fff}] {m}");
            if (!_logFlushing) FlushLog();
        }

        private async void FlushLog() {
            if (_logFlushing) return;
            _logFlushing = true;
            try {
                while (_logQueue.TryDequeue(out string line)) {
                    if (_logFile == null) break;
                    try { await FileIO.AppendTextAsync(_logFile, line + "\r\n"); } catch { }
                }
            } finally { _logFlushing = false; }
            // Если пока писали — добавили ещё
            if (!_logQueue.IsEmpty) FlushLog();
        }

        private async void InitAsync() {
            try {
                var localFolder = await Windows.Storage.StorageLibrary.GetLibraryAsync(Windows.Storage.KnownLibraryId.Music);
                var appFolder = await localFolder.SaveFolder.CreateFolderAsync("Unogram", CreationCollisionOption.OpenIfExists);
                _dbPath = appFolder.Path.Replace("\\", "/") + "/td_db";
                _filesFolder = await appFolder.CreateFolderAsync("td_db_files", CreationCollisionOption.OpenIfExists);
                string logName = "log_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt";
                _logFile = await appFolder.CreateFileAsync(logName, CreationCollisionOption.ReplaceExisting);
                // Логируем сохранённые настройки прокси
                var ls2 = Windows.Storage.ApplicationData.Current.LocalSettings;
            } catch (Exception ex) {
                await new Windows.UI.Popups.MessageDialog("Ошибка хранилища:\n" + ex.Message).ShowAsync();
                return;
            }
            Task.Run(() => LongPolling());
            // Прокси применяется после инициализации TDLib — см. authorizationStateWaitPhoneNumber
        }

        private async Task FetchAndApplyProxyAsync() {
            List<ProxyEntry> parsed = null;
            try {
                var http = new System.Net.Http.HttpClient();
                http.Timeout = TimeSpan.FromSeconds(10);
                var text = await http.GetStringAsync("https://open-amitie-radio-rs-89235677.koyeb.app/mtproxy.php");
                parsed = new List<ProxyEntry>();
                var lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
                foreach (var line in lines) {
                    var l = line.Trim();
                    if (string.IsNullOrEmpty(l)) continue;
                    try {
                        if (l.StartsWith("tg://proxy") || l.StartsWith("https://t.me/proxy")) {
                            string query = l.Contains("?") ? l.Substring(l.IndexOf('?') + 1) : "";
                            var qp = new Dictionary<string, string>();
                            foreach (var pair in query.Split('&')) {
                                var kv = pair.Split('=');
                                if (kv.Length == 2) qp[Uri.UnescapeDataString(kv[0])] = Uri.UnescapeDataString(kv[1]);
                            }
                            string server = qp.ContainsKey("server") ? qp["server"] : null;
                            string portStr = qp.ContainsKey("port") ? qp["port"] : null;
                            string secret = qp.ContainsKey("secret") ? qp["secret"] : null;
                            if (!string.IsNullOrEmpty(server) && !string.IsNullOrEmpty(secret) && int.TryParse(portStr, out int port))
                                parsed.Add(new ProxyEntry { Host = server, Port = port, Secret = secret });
                        } else if (l.Contains(":")) {
                            var parts = l.Split(':');
                            if (parts.Length >= 3 && int.TryParse(parts[1], out int port2) && !string.IsNullOrEmpty(parts[2]))
                                parsed.Add(new ProxyEntry { Host = parts[0], Port = port2, Secret = parts[2] });
                        }
                    } catch (Exception ex) { Log("PROXY parse ERR: " + ex.Message); }
                }
            } catch (Exception ex) {
                return;
            }
            if (parsed == null || parsed.Count == 0) return;
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                _proxyList = parsed;
                _proxyIndex = 0;
                var t = TryNextProxyAsync(); // fire-and-forget на UI потоке
            });
        }

        private async Task TryNextProxyAsync() {
            if (_proxyList.Count == 0) return;
            if (_proxyIndex >= _proxyList.Count) _proxyIndex = 0;
            var proxy = _proxyList[_proxyIndex];
            await ApplyProxyAsync(proxy.Host, proxy.Port, proxy.Secret);
            // Таймер на 5 секунд — если не подключились, пробуем следующий
            _proxyTimer?.Stop();
            _proxyTimer = new Windows.UI.Xaml.DispatcherTimer();
            _proxyTimer.Interval = TimeSpan.FromSeconds(5);
            _proxyTimer.Tick += async (s, e) => {
                _proxyTimer.Stop();
                if (!_proxyConnected) {
                    _proxyIndex++;
                    await TryNextProxyAsync();
                }
            };
            _proxyTimer.Start();
        }

        private void ClearAllProxies() {
            // Удаляем все известные прокси
            if (_currentProxyId != 0) {
                TdJson.SendUtf8(_client, "{\"@type\":\"removeProxy\",\"proxy_id\":" + _currentProxyId + "}");
                _currentProxyId = 0;
            }
            // Запрашиваем список чтобы удалить все остальные (накопленные)
            TdJson.SendUtf8(_client, "{\"@type\":\"getProxies\"}");
            _proxyConnected = false;
        }

        private async Task ApplyProxyAsync(string host, int port, string secret) {
            ClearAllProxies();
            string reqJson = "{\"@type\":\"addProxy\",\"proxy\":{\"@type\":\"proxy\",\"server\":\"" + host +
                             "\",\"port\":" + port +
                             ",\"type\":{\"@type\":\"proxyTypeMtproto\",\"secret\":\"" + secret + "\"}},\"enable\":true}";
            TdJson.SendUtf8(_client, reqJson);
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                ProxyStatusText.Text = "[..] " + host + ":" + port;
                ProxyStatusText.Visibility = Visibility.Visible;
            });
        }

        private void SendParameters() {
            JObject p = new JObject {
                ["@type"] = "setTdlibParameters",
                ["use_test_dc"] = false,
                ["database_directory"] = _dbPath,
                ["files_directory"] = _filesFolder?.Path.Replace("\\", "/") ?? _dbPath + "_files",
                ["database_encryption_key"] = "",
                ["use_file_database"] = true,
                ["use_chat_info_database"] = true,
                ["use_message_database"] = true,
                ["use_secret_chats"] = false,
                ["api_id"] = 26688287,
                ["api_hash"] = "5f4afe72bc71dc6ec40f7dcb0c9a822b",
                ["system_language_code"] = "ru",
                ["device_model"] = "Lumia",
                ["system_version"] = "10",
                ["application_version"] = "1.2"
            };
            TdJson.SendUtf8(_client, p.ToString(Newtonsoft.Json.Formatting.None));
        }

        private void LongPolling() {
            while (true) {
                IntPtr resPtr = TdJson.td_json_client_receive(_client, 1.0);
                if (resPtr != IntPtr.Zero) {
                    string json = TdJson.IntPtrToStringUtf8(resPtr);
                    if (string.IsNullOrEmpty(json)) continue;
                    var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                        try {
                            var update = JObject.Parse(json);
                            string type = update["@type"]?.ToString();
                            // Логируем всё для диагностики прокси (кроме очень частых апдейтов)
                            if (type != "updateOption" && type != "updateChatReadOutbox")
                            HandleUpdate(type, update);
                        } catch (Exception ex) { Log("PARSE ERR: " + ex.Message); }
                    });
                }
            }
        }

        private void HandleUpdate(string type, JObject update) {
            switch (type) {
                case "updateAuthorizationState":
                    var s = update["authorization_state"]?["@type"]?.ToString();
                    if (s == "authorizationStateWaitTdlibParameters") {
                        SendParameters();
                        TdJson.SendUtf8(_client, "{\"@type\":\"getOption\",\"name\":\"version\"}");
                    }

                    // Прокси применяется только если пользователь выбрал режим в настройках
                    // _proxyMode == None по умолчанию — автозапуска нет

                    if (s == "authorizationStateWaitPhoneNumber") {
                        LoginStatus.Text = "Введите номер телефона";
                        PhoneInput.IsEnabled = true;
                        PhoneButton.IsEnabled = true;
                        // Применяем прокси согласно сохранённому режиму
                        if (!_proxyApplied) {
                            _proxyApplied = true;
                            ApplySavedProxy();
                        }
                    }
                    if (s == "authorizationStateWaitCode") {
                        LoginStatus.Text = "Код отправлен. Проверьте Telegram или SMS.";
                        PhoneInput.IsEnabled = false;
                        PhoneButton.IsEnabled = false;
                        CodeInput.Visibility = Visibility.Visible;
                        CodeButton.Visibility = Visibility.Visible;
                        CodeInput.Focus(FocusState.Programmatic);
                    }
                    if (s == "authorizationStateWaitPassword") {
                        LoginStatus.Text = "Введите пароль 2FA";
                        CodeInput.Visibility = Visibility.Collapsed;
                        CodeButton.Visibility = Visibility.Collapsed;
                        PasswordInput.Visibility = Visibility.Visible;
                        PasswordButton.Visibility = Visibility.Visible;
                        PasswordInput.Focus(FocusState.Programmatic);
                    }
                    if (s == "authorizationStateReady") {
                        _isAuthorized = true;
                        LoginPanel.Visibility = Visibility.Collapsed;
                        ChatListView.Visibility = Visibility.Visible;
                        if (SearchPanel != null) SearchPanel.Visibility = Visibility.Visible;
                        LogoutButton.Visibility = Visibility.Visible;
                        if (!_proxyApplied) {
                            _proxyApplied = true;
                            ApplySavedProxy();
                        }
                        TdJson.SendUtf8(_client, "{\"@type\":\"getChats\",\"chat_list\":{\"@type\":\"chatListArchive\"},\"limit\":1000}");
                        _waitingForMe = true;
                        TdJson.SendUtf8(_client, "{\"@type\":\"getMe\"}");
                        _loadingArchiveIds = true;
                    }
                    if (s == "authorizationStateLoggingOut" || s == "authorizationStateClosed") {
                        _isAuthorized = false;
                        _chatListItems.Clear();
                        _allChatItems.Clear();
                        _chatsDict.Clear();
                        _folderChatIds.Clear();
                        _mainListLoaded = false;
                        ChatListView.Visibility = Visibility.Collapsed;
                        LogoutButton.Visibility = Visibility.Collapsed;
                        LoginPanel.Visibility = Visibility.Visible;
                        LoginStatus.Text = "Введите номер телефона";
                        PhoneInput.Text = "";
                        PhoneInput.IsEnabled = true;
                        PhoneButton.IsEnabled = true;
                        CodeInput.Visibility = Visibility.Collapsed;
                        CodeButton.Visibility = Visibility.Collapsed;
                        PasswordInput.Password = "";
                        PasswordInput.Visibility = Visibility.Collapsed;
                        PasswordButton.Visibility = Visibility.Collapsed;
                        LoginStatus.Text = "";
                    }
                    break;

                case "error":
                    string errMsg = update["message"]?.ToString();
                    // Если нет закреплённого сообщения — сбрасываем флаг
                    if (_pinnedMessageId == -1)
                        _pinnedMessageId = 0;
                    // Не показываем proxy ошибки в UI
                    if (errMsg != null && (
                        errMsg.Contains("Proxy") ||
                        errMsg.Contains("proxy") ||
                        errMsg.Contains("proxy secret") ||
                        errMsg.Contains("Unsupported proxy"))) {
                        // При невалидном секрете — сразу пробуем следующий прокси
                        if (errMsg.Contains("secret") || errMsg.Contains("non-empty")) {
                            _proxyTimer?.Stop();
                            _proxyIndex++;
                            var skipTask = TryNextProxyAsync();
                        }
                        break;
                    }
                    LoginStatus.Text = "Ошибка: " + errMsg;
                    PhoneButton.IsEnabled = true;
                    CodeButton.IsEnabled = true;
                    if (_loadingChats && (errMsg?.Contains("CHAT_LIST_EMPTY") ?? false)) {
                        _loadingChats = false;
                    }
                    break;

                case "updateChatAddedToList":
                    // Игнорируем во время начальной загрузки — порядок формирует LoadNextChat.
                    // Реагируем только когда чат реально переходит между списками (архив ↔ главный).
                    if (_loadingChats || _loadingArchive || _loadingArchiveIds) break;
                    long addedChatId = update["chat_id"]?.ToObject<long>() ?? 0;
                    string addedList = update["chat_list"]?["@type"]?.ToString() ?? "";
                    if (addedChatId != 0 && _chatsDict.ContainsKey(addedChatId)) {
                        var addedItem = _chatsDict[addedChatId];
                        if (addedList == "chatListMain") {
                            if (_archiveChatItems.Contains(addedItem)) {
                                _archiveChatIds.Remove(addedChatId);
                                _archiveChatItems.Remove(addedItem);
                                UpdateArchiveUnreadBadge();
                            }
                            if (!_chatListItems.Contains(addedItem)) {
                                InsertAfterPinned(_chatListItems, addedItem);
                                ChatCountText.Text = _chatListItems.Count.ToString();
                            }
                        } else if (addedList == "chatListArchive") {
                            if (_chatListItems.Contains(addedItem)) {
                                _chatListItems.Remove(addedItem);
                                _allChatItems.RemoveAll(c => c.Id == addedChatId);
                                ChatCountText.Text = _chatListItems.Count.ToString();
                            }
                            if (!_archiveChatItems.Contains(addedItem)) {
                                _archiveChatIds.Add(addedChatId);
                                InsertAfterPinned(_archiveChatItems, addedItem);
                            }
                        }
                    }
                    break;

                case "updateNewChat":
                    var chatUpd = update["chat"];
                    long chatId = (long)chatUpd["id"];
                    _rawChatsDict[chatId] = chatUpd; // сохраняем сырой JSON для last_read_inbox_message_id
                    // Если пришёл updateNewChat — TDLib уже авторизован (сессия сохранена)
                    if (!_isAuthorized) {
                        _isAuthorized = true;
                        LoginPanel.Visibility = Visibility.Collapsed;
                        ChatListView.Visibility = Visibility.Visible;
                        if (SearchPanel != null) SearchPanel.Visibility = Visibility.Visible;
                        LogoutButton.Visibility = Visibility.Visible;
                        // Pre-fetch архива перед main — как и при обычной авторизации
                        TdJson.SendUtf8(_client, "{\"@type\":\"getChats\",\"chat_list\":{\"@type\":\"chatListArchive\"},\"limit\":1000}");
                        _loadingArchiveIds = true;
                        _waitingForMe = true;
                        TdJson.SendUtf8(_client, "{\"@type\":\"getMe\"}");
                    }
                    if (!_chatsDict.ContainsKey(chatId)) {
                        bool isChannel = chatUpd["type"]?["@type"]?.ToString() == "chatTypeSupergroup"
                            && (chatUpd["type"]?["is_channel"]?.ToObject<bool>() ?? false);
                        long initOutboxRead = chatUpd["last_read_outbox_message_id"]?.ToObject<long>() ?? 0;
                        string chatTitle = chatUpd["title"]?.ToString();
                        // Чат с собой — называем "⭐ Избранное"
                        bool isSavedMessages = chatUpd["type"]?["@type"]?.ToString() == "chatTypePrivate"
                            && (chatUpd["type"]?["user_id"]?.ToObject<long>() ?? 0) == _myUserId
                            && _myUserId != 0;
                        if (isSavedMessages) chatTitle = "⭐ Избранное";
                        _chatsDict[chatId] = new ChatItem { Id = chatId, Title = chatTitle, OutboxReadId = initOutboxRead > 0 ? initOutboxRead : 0, IsChannel = isChannel };
                    }
                    var chatItem = _chatsDict[chatId];
                    // Заполняем последнее сообщение
                    var lastMsg = chatUpd["last_message"];
                    if (lastMsg != null) FillChatLastMessage(chatItem, lastMsg, chatUpd);
                    // Непрочитанные
                    chatItem.UnreadCount = chatUpd["unread_count"]?.ToObject<int>() ?? 0;
                    chatItem.IsMarkedUnread = chatUpd["is_marked_as_unread"]?.ToObject<bool>() ?? false;
                    // _archiveChatIds заполняется ДО загрузки главного списка — надёжнее чем positions
                    // (при bump positions уже содержит chatListMain вместо chatListArchive)
                    var positions = chatUpd["positions"] as JArray;
                    bool isArchiveChat = _archiveChatIds.Contains(chatId) ||
                        (positions != null && positions.Any(p => p["list"]?["@type"]?.ToString() == "chatListArchive"));
                    bool isMainChat = !isArchiveChat;
                    // Закреплён ли чат — берём из positions нужного списка
                    if (positions != null) {
                        string targetListType = isArchiveChat ? "chatListArchive" : "chatListMain";
                        var pos = positions.FirstOrDefault(p => p["list"]?["@type"]?.ToString() == targetListType
                                  || p["list"]?["@type"]?.ToString() == "chatListMain");
                        chatItem.IsPinned = pos?["is_pinned"]?.ToObject<bool>() ?? false;
                    } else {
                    }
                    if (_pendingPinnedPositions.ContainsKey(chatId)) {
                        bool pendingPin = _pendingPinnedPositions[chatId];
                        chatItem.IsPinned = pendingPin;
                        _pendingPinnedPositions.Remove(chatId);
                    }

                    // updateNewChat только обновляет _chatsDict.
                    // Добавление в видимый список — исключительно через LoadNextChat (100ms throttle).
                    // Исключение: если это ответ на getChat из else-ветки LoadNextChat — продолжаем цепочку.
                    if (_pendingGetChat.Contains(chatId)) {
                        _pendingGetChat.Remove(chatId);
                        LoadNextChat(); // продолжаем очередь
                    }
                    var phSmall = chatUpd["photo"]?["small"];
                    if (phSmall != null) {
                        long phFileId = (long)phSmall["id"];
                        _fileToChatId[phFileId] = chatId;
                        string phPath = phSmall["local"]?["path"]?.ToString();
                        if (!string.IsNullOrEmpty(phPath))
                            { var t = UpdateAvatar(chatId, phPath); }
                        else
                            TdJson.SendUtf8(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + phFileId + ",\"priority\":1,\"synchronous\":false}");
                    }
                    break;

                case "updateFile":
                case "file":
                    var fileObj = (type == "updateFile") ? update["file"] as JObject : update;
                    if (fileObj != null) {
                        long fid = fileObj["id"] != null ? (long)fileObj["id"] : 0;
                        string fpath = fileObj["local"]?["path"]?.ToString();
                        bool isCompleted = fileObj["local"]?["is_downloading_completed"]?.ToObject<bool>() ?? false;
                        bool isUploaded  = fileObj["remote"]?["is_uploading_completed"]?.ToObject<bool>() ?? false;
                        long downloaded = fileObj["local"]?["downloaded_size"]?.ToObject<long>() ?? 0;
                        long total = fileObj["size"]?.ToObject<long>() ?? 0;

                        // Обработка uploadFile — отправляем сообщение когда файл загружен
                        if (isUploaded && fid != 0 && !string.IsNullOrEmpty(_pendingUploadType) && _pendingUploadChatId != 0) {
                            string uType = _pendingUploadType;
                            long uChatId = _pendingUploadChatId;
                            _pendingUploadType = "";
                            _pendingUploadChatId = 0;
                            string sendReq;
                            if (uType == "doc") {
                                sendReq = "{\"@type\":\"sendMessage\",\"chat_id\":" + uChatId +
                                    ",\"input_message_content\":{\"@type\":\"inputMessageDocument\"" +
                                    ",\"document\":{\"@type\":\"inputDocument\"" +
                                    ",\"document\":{\"@type\":\"inputFileId\",\"id\":" + fid + "}" +
                                    ",\"disable_content_type_detection\":false}" +
                                    ",\"caption\":{\"@type\":\"formattedText\",\"text\":\"\"}}}";
                            } else if (uType.StartsWith("voice_")) {
                                int dur = int.TryParse(uType.Replace("voice_",""), out int d) ? d : 0;
                                sendReq = "{\"@type\":\"sendMessage\",\"chat_id\":" + uChatId +
                                    ",\"input_message_content\":{\"@type\":\"inputMessageVoiceNote\"" +
                                    ",\"voice_note\":{\"@type\":\"inputVoiceNote\"" +
                                    ",\"voice_note\":{\"@type\":\"inputFileId\",\"id\":" + fid + "}" +
                                    ",\"duration\":" + dur +
                                    ",\"waveform\":\"\"}" +
                                    ",\"caption\":{\"@type\":\"formattedText\",\"text\":\"\"}}}";
                            } else sendReq = null;
                            if (sendReq != null) {
                                TdJson.SendUtf8(_client, sendReq);
                            }
                        }
                        if (fid != 0) {
                            if (_fileToChatId.ContainsKey(fid) && !string.IsNullOrEmpty(fpath))
                                { var t = UpdateAvatar(_fileToChatId[fid], fpath); }

                            // Аватарка для результатов поиска
                            if (isCompleted && !string.IsNullOrEmpty(fpath) && _fileToSearchResult.ContainsKey(fid)) {
                                var srItm = _fileToSearchResult[fid];
                                _fileToSearchResult.Remove(fid);
                                { var t = UpdateAvatarSearchResult(srItm, fpath); }
                            }

                            // Thumbnail для панели стикеров
                            if (isCompleted && !string.IsNullOrEmpty(fpath) && _stickerThumbToItem.ContainsKey(fid))
                                HandleStickerThumbDownloaded(fid, fpath);

                            // Отправка стикера — ждём пока файл скачается
                            if (isCompleted && fid == _pendingStickerFileId && _pendingStickerChatId != 0) {
                                long sChatId   = _pendingStickerChatId;
                                long sFileId   = _pendingStickerFileId;
                                long sThreadId = _threadMessageId;
                                _pendingStickerFileId = 0;
                                _pendingStickerChatId = 0;
                                string sReq = "{\"@type\":\"sendMessage\",\"chat_id\":" + sChatId +
                                    (sThreadId != 0 ? ",\"message_thread_id\":" + sThreadId : "") +
                                    ",\"input_message_content\":{\"@type\":\"inputMessageSticker\"" +
                                    ",\"sticker\":{\"@type\":\"inputSticker\"" +
                                    ",\"sticker\":{\"@type\":\"inputFileId\",\"id\":" + sFileId + "}" +
                                    ",\"width\":512,\"height\":512}}}";
                                TdJson.SendUtf8(_client, sReq);
                            }

                            // Фолбэк для стикеров: TDLib может вернуть новый file_id при скачивании.
                            if (!_fileToMsgId.ContainsKey(fid) && isCompleted && !string.IsNullOrEmpty(fpath)
                                && (fpath.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
                                 || fpath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))) {
                                string remoteUid = fileObj["remote"]?["unique_id"]?.ToString();
                                if (!string.IsNullOrEmpty(remoteUid) && _remoteUniqueIdToMsgId.ContainsKey(remoteUid)) {
                                    long mid2 = _remoteUniqueIdToMsgId[remoteUid];
                                    _fileToMsgId[fid] = mid2;
                                    var t2 = UpdateMessagePhoto(mid2, fpath);
                                }
                            }

                            if (_fileToMsgId.ContainsKey(fid)) {
                                long mid = _fileToMsgId[fid];
                                bool isImg = !string.IsNullOrEmpty(fpath) &&
                                    (fpath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                     fpath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                                     fpath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                                     fpath.EndsWith(".webp", StringComparison.OrdinalIgnoreCase));
                                if (isImg)
                                    { var t = UpdateMessagePhoto(mid, fpath); }
                                // Если это полноразмерное фото для оверлея
                                if (isCompleted && isImg && _fullPhotoMsgId == mid && !string.IsNullOrEmpty(fpath))
                                    { var t = ShowFullPhoto(fpath); }
                                if (_messagesDict.ContainsKey(mid)) {
                                    var msgItem = _messagesDict[mid];
                                    if (msgItem.IsGif) {
                                        bool isGifFile = _videoFileIds.ContainsKey(fid);
                                        if (isCompleted && isGifFile && !string.IsNullOrEmpty(fpath)) {
                                            msgItem.GifSource = new Uri(fpath);
                                            msgItem.VideoDownloadProgress = null;
                                        } else if (isGifFile && total > 0) {
                                            int pct = (int)(downloaded * 100 / total);
                                            msgItem.VideoDownloadProgress = "⏳ " + pct + "%";
                                        }
                                    } else if (msgItem.IsVideo) {
                                        bool isVideoFile = _videoFileIds.ContainsKey(fid);
                                        if (isCompleted && isVideoFile && !string.IsNullOrEmpty(fpath)) {
                                            msgItem.FilePath = fpath;
                                            msgItem.VideoDownloadProgress = null;
                                        } else if (isVideoFile && total > 0) {
                                            int pct = (int)(downloaded * 100 / total);
                                            msgItem.VideoDownloadProgress = "⏳ " + pct + "%";
                                        }
                                    }
                                    if (msgItem.IsDocument) {
                                        if (isCompleted && !string.IsNullOrEmpty(fpath)) {
                                            msgItem.FilePath = fpath;
                                            msgItem.IsDownloaded = true;
                                            msgItem.DownloadStatus = "📂 Открыть";
                                        } else if (total > 0) {
                                            int pct = (int)(downloaded * 100 / total);
                                            msgItem.DownloadStatus = "⏳ " + pct + "%";
                                        }
                                    }
                                    if (msgItem.IsAudio) {
                                        if (isCompleted && !string.IsNullOrEmpty(fpath)) {
                                            msgItem.FilePath = fpath;
                                            msgItem.AudioPlayStatus = "▶";
                                        } else if (total > 0) {
                                            int pct = (int)(downloaded * 100 / total);
                                            msgItem.AudioPlayStatus = "⏳" + pct + "%";
                                        }
                                    }
                                }
                            }
                        }
                    }
                    break;

                case "updateNewMessage":
                    var newMsg = update["message"];
                    long newMsgChatId = newMsg?["chat_id"]?.ToObject<long>() ?? 0;
                    // Игнорируем если история ещё не загружена (LoadingIndicator видим)
                    if (newMsgChatId == _currentChatId && newMsg != null && !_isLoadingHistory) {
                        var newItem = ParseMessage(newMsg);
                        if (newItem != null) {
                            var lastReal = _messageItems.LastOrDefault(m => !m.IsSeparator);
                            if (lastReal == null || lastReal.RawDate.Date != newItem.RawDate.Date)
                                _messageItems.Add(MakeSeparator(newItem.RawDate.Date, DateTime.Today));
                            _messageItems.Add(newItem);
                            StartBotButton.Visibility = Visibility.Collapsed;

                            double scrollable3 = MessagesScrollViewer.ScrollableHeight;
                            double offset2 = MessagesScrollViewer.VerticalOffset;
                            bool wasAtBottom = scrollable3 <= 0 || (scrollable3 - offset2) < 200;
                            if (wasAtBottom) {
                                var t = new Windows.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                                t.Tick += (ts, te) => { t.Stop(); MessagesScrollViewer.ChangeView(null, MessagesScrollViewer.ScrollableHeight + 1000, null, false); };
                                t.Start();
                            }
                        }
                        // Помечаем как прочитанное если чат открыт
                        long newMsgId = newMsg["id"]?.ToObject<long>() ?? 0;
                        if (newMsgId != 0)
                            TdJson.SendUtf8(_client, "{\"@type\":\"viewMessages\",\"chat_id\":" + newMsgChatId + ",\"message_ids\":[" + newMsgId + "],\"force_read\":true}");
                    }
                    // Обновляем бейдж архива если сообщение пришло в архивный чат
                    if (_archiveChatItems.Any(ch => ch.Id == newMsgChatId))
                        UpdateArchiveUnreadBadge();
                    // Звук и уведомление для входящих личных сообщений
                    if (_soundEnabled && newMsg != null) {
                        bool isOutgoing = newMsg["is_outgoing"]?.ToObject<bool>() ?? false;
                        bool isPrivate  = newMsgChatId > 0;
                        if (!isOutgoing && isPrivate) {
                            // Собираем имя и текст для уведомления
                            string senderName = "";
                            if (_chatsDict.ContainsKey(newMsgChatId))
                                senderName = _chatsDict[newMsgChatId].Title;
                            else if (_usersDict.ContainsKey(newMsgChatId)) {
                                var u = _usersDict[newMsgChatId];
                                senderName = (u["first_name"]?.ToString() + " " + u["last_name"]?.ToString()).Trim();
                            }
                            var mc = newMsg["content"];
                            string msgText = mc?["text"]?["text"]?.ToString()
                                          ?? mc?["caption"]?["text"]?.ToString()
                                          ?? (mc?["@type"]?.ToString()?.Replace("message","") ?? "Сообщение");
                            ShowToastNotification(senderName, msgText);
                        }
                    }
                    break;

                case "updateChatFolders":
                    var folders = update["chat_folders"] as Newtonsoft.Json.Linq.JArray;
                    if (folders != null) {
                        var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => BuildFolderTabs(folders));
                    }
                    break;

                case "updateConnectionState":
                    var connState = update["state"]?["@type"]?.ToString();
                    if (connState == "connectionStateReady") {
                        _connectionReady = true;
                        _proxyConnected = true;
                        _proxyTimer?.Stop();
                        _connectingTimer?.Stop();
                        ConnectionStatusText.Text = "";
                        ConnectionProgressRing.IsActive = false;
                        ConnectionProgressRing.Visibility = Visibility.Collapsed;
                        if (_currentProxyId != 0) {
                            ProxyStatusText.Text = ProxyStatusText.Text.Replace("[..] ", "[ok] ");
                            ProxyStatusText.Visibility = Visibility.Visible;
                        }
                    } else {
                        _connectionReady = false;
                        bool spinning = connState == "connectionStateConnecting"
                                     || connState == "connectionStateConnectingToProxy"
                                     || connState == "connectionStateUpdating";
                        string connText = connState == "connectionStateConnecting"          ? "подключение..."
                            : connState == "connectionStateConnectingToProxy"               ? "подключение к прокси..."
                            : connState == "connectionStateUpdating"                        ? "обновление..."
                            : connState == "connectionStateWaitingForNetwork"               ? "· нет сети"
                            : "...";
                        ConnectionStatusText.Text = connText;
                        ConnectionProgressRing.IsActive = spinning;
                        ConnectionProgressRing.Visibility = spinning ? Visibility.Visible : Visibility.Collapsed;
                        // Если подключение через прокси зависло — через 10с пробуем следующий
                        if ((connState == "connectionStateConnecting" ||
                             connState == "connectionStateConnectingToProxy") &&
                            _proxyList.Count > 0) {
                            _connectingTimer?.Stop();
                            _connectingTimer = new Windows.UI.Xaml.DispatcherTimer();
                            _connectingTimer.Interval = TimeSpan.FromSeconds(10);
                            _connectingTimer.Tick += async (s2, e2) => {
                                _connectingTimer.Stop();
                                if (!_connectionReady && _proxyList.Count > 0) {
                                    _proxyTimer?.Stop();
                                    _proxyIndex++;
                                    await TryNextProxyAsync();
                                }
                            };
                            _connectingTimer.Start();
                        } else {
                            _connectingTimer?.Stop();
                        }
                    }
                    break;

                case "addedProxies":
                case "proxies":
                    var proxyItems = update["proxies"] as JArray;
                    if (proxyItems != null) {
                        // Удаляем все прокси кроме текущего активного
                        foreach (var pi in proxyItems) {
                            int pid = pi["id"]?.ToObject<int>() ?? 0;
                            if (pid != 0 && pid != _currentProxyId)
                                TdJson.SendUtf8(_client, "{\"@type\":\"removeProxy\",\"proxy_id\":" + pid + "}");
                        }
                    }
                    break;

                case "addedProxy":
                    long newProxyId = update["id"]?.ToObject<long>() ?? 0;
                    if (newProxyId != 0) {
                        _currentProxyId = (int)newProxyId;
                        var proxyObj = update["proxy"];
                        string ph = proxyObj?["server"]?.ToString() ?? "";
                        int pp = proxyObj?["port"]?.ToObject<int>() ?? 0;
                        string status = _connectionReady ? "[ok] " : "[..] ";
                        var ignored2 = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                            ProxyStatusText.Text = status + ph + ":" + pp;
                            ProxyStatusText.Visibility = Visibility.Visible;
                        });
                    }
                    break;

                case "user":
                    long gUid = update["id"]?.ToObject<long>() ?? 0;
                    if (gUid != 0) {
                        _usersDict[gUid] = update;
                        // Ответ на getMe
                        if (_waitingForMe) {
                            _waitingForMe = false;
                            _myUserId = gUid;
                            // Переименовываем чат с собой в списке чатов
                            if (_chatsDict.ContainsKey(_myUserId)) {
                                _chatsDict[_myUserId].Title = "⭐ Избранное";
                            }
                            if (_contactsPendingMyId && _contactItems != null) {
                                _contactsPendingMyId = false;
                                var selfContact = _contactItems.FirstOrDefault(c => c.UserId == gUid);
                                if (selfContact != null) {
                                    selfContact.FullName = "⭐ Избранное";
                                    selfContact.Username = "";
                                    selfContact.LastSeen = "";
                                }
                            }
                        }
                        if (gUid == _currentChatId)
                            UpdateChatStatus(update["status"]);
                        // Обновляем контакт если он в списке контактов
                        var matchContact = _contactItems.FirstOrDefault(ctItem => ctItem.UserId == gUid);
                        if (matchContact != null) {
                            string fn = (update["first_name"]?.ToString() + " " + update["last_name"]?.ToString()).Trim();
                            matchContact.FullName = string.IsNullOrEmpty(fn) ? gUid.ToString() : fn;
                            matchContact.Username = update["username"]?.ToString() ?? update["usernames"]?["editable_username"]?.ToString() ?? "";
                            { var t = LoadContactAvatarFromUser(matchContact, update); }
                        }
                    }
                    break;

                case "updateSupergroup":
                    var sg = update["supergroup"];
                    if (sg != null) {
                        long sgId = sg["id"]?.ToObject<long>() ?? 0;
                        if (sgId != 0) _supergroupDict[sgId] = sg;
                    }
                    break;

                case "supergroup":
                    long sg2Id = update["id"]?.ToObject<long>() ?? 0;
                    if (sg2Id != 0) _supergroupDict[sg2Id] = update;
                    break;

                case "updateUser":
                    var user = update["user"];
                    long uid = user?["id"]?.ToObject<long>() ?? 0;
                    if (uid != 0) {
                        _usersDict[uid] = user;
                        if (_chatsDict.ContainsKey(uid)) {
                            string uStatus = user["status"]?["@type"]?.ToString();
                            _chatsDict[uid].IsOnline = uStatus == "userStatusOnline";
                        }
                        // Обновляем шапку если открыт чат с этим пользователем
                        if (uid == _currentChatId)
                            UpdateChatStatus(user["status"]);
                    }
                    break;

                case "updateChatAction":
                    long actionChatId = update["chat_id"]?.ToObject<long>() ?? 0;
                    string actionType = update["action"]?["@type"]?.ToString() ?? "";
                    if (actionChatId == _currentChatId && actionType == "chatActionTyping") {
                        CurrentChatStatus.Text = "печатает...";
                        CurrentChatStatus.Foreground = CB("#2AABEE");
                        _typingTimer.Stop();
                        _typingTimer.Start();
                    }
                    break;

                case "updateUserStatus":
                    long userId = update["user_id"]?.ToObject<long>() ?? 0;
                    string statusType = update["status"]?["@type"]?.ToString();
                    bool isOnline = statusType == "userStatusOnline";
                    // expires — серверное время, используем для калибровки часов
                    if (isOnline) {
                        long expires = update["status"]?["expires"]?.ToObject<long>() ?? 0;
                        if (expires > 0) UpdateServerTimeOffset(expires - 30); // expires = now+30s на сервере
                    }
                    if (_chatsDict.ContainsKey(userId))
                        _chatsDict[userId].IsOnline = isOnline;
                    // Синхронизируем статус в _usersDict чтобы при открытии чата был актуальный
                    if (_usersDict.ContainsKey(userId) && update["status"] != null)
                        _usersDict[userId]["status"] = update["status"];
                    if (userId == _currentChatId) {
                        long wo = update["status"]?["was_online"]?.ToObject<long>() ?? 0;
                        long nowUnix = LocalUnixNow();
                        UpdateChatStatus(update["status"]);
                    }
                    break;

                case "updateChatLastMessage":
                    long ulcId = update["chat_id"]?.ToObject<long>() ?? 0;
                    var ulcMsg = update["last_message"];
                    if (ulcId != 0 && ulcMsg != null && _chatsDict.ContainsKey(ulcId)) {
                        string ulcType = ulcMsg["content"]?["@type"]?.ToString() ?? "null";
                        FillChatLastMessage(_chatsDict[ulcId], ulcMsg, update);
                        MoveChatToTop(ulcId);
                    }
                    break;

                case "updateChatPosition":
                    long ucpId = update["chat_id"]?.ToObject<long>() ?? 0;
                    if (ucpId != 0) {
                        var ucpPos = update["position"];
                        string ucpListType = ucpPos?["list"]?["@type"]?.ToString() ?? "";
                        // Игнорируем позиции папок — они не влияют на закрепление в основном списке
                        if (ucpListType == "chatListFolder") break;
                        bool ucpPinned = ucpPos?["is_pinned"]?.ToObject<bool>() ?? false;
                        if (_chatsDict.ContainsKey(ucpId)) {
                            _chatsDict[ucpId].IsPinned = ucpPinned;
                            var allItem = _allChatItems.FirstOrDefault(ch => ch.Id == ucpId);
                            if (allItem != null) allItem.IsPinned = ucpPinned;
                            var ucpList = _archiveChatItems.Any(ch => ch.Id == ucpId) ? _archiveChatItems : _chatListItems;
                            var ucpItem = ucpList.FirstOrDefault(ch => ch.Id == ucpId);
                            if (ucpItem != null) {
                                ucpList.Remove(ucpItem);
                                InsertAfterPinned(ucpList, ucpItem);
                            }
                        } else {
                            _pendingPinnedPositions[ucpId] = ucpPinned;
                        }
                    }
                    break;

                case "updateChatLastPinnedMessageId":
                    long pinnedChatId = update["chat_id"]?.ToObject<long>() ?? 0;
                    long newPinnedId  = update["pinned_message_id"]?.ToObject<long>() ?? 0;
                    // Обновляем rawChatsDict
                    if (pinnedChatId != 0 && _rawChatsDict.ContainsKey(pinnedChatId)) {
                        var rawC = _rawChatsDict[pinnedChatId] as Newtonsoft.Json.Linq.JObject;
                        if (rawC != null) rawC["pinned_message_id"] = newPinnedId;
                    }
                    // Если это текущий чат — обновляем полоску
                    if (pinnedChatId == _currentChatId) {
                        _pinnedMessageId = newPinnedId;
                        if (newPinnedId == 0) {
                            PinnedMessageBar.Visibility = Visibility.Collapsed;
                            PinnedMessageText.Text = "";
                        } else {
                            TdJson.SendUtf8(_client, "{\"@type\":\"getMessage\",\"chat_id\":" + pinnedChatId + ",\"message_id\":" + newPinnedId + "}");
                        }
                    }
                    break;

                case "updateChatReadInbox":
                    long ucriId = update["chat_id"]?.ToObject<long>() ?? 0;
                    if (ucriId != 0 && _chatsDict.ContainsKey(ucriId)) {
                        _chatsDict[ucriId].UnreadCount = update["unread_count"]?.ToObject<int>() ?? 0;
                        if (_chatsDict[ucriId].UnreadCount == 0)
                            _chatsDict[ucriId].IsMarkedUnread = false;
                        if (_archiveChatItems.Any(ch => ch.Id == ucriId))
                            UpdateArchiveUnreadBadge();
                        // Убираем разделитель "Новые сообщения" если это текущий чат
                        if (ucriId == _currentChatId) {
                            long newLastRead = update["last_read_inbox_message_id"]?.ToObject<long>() ?? 0;
                            // Ищем и удаляем разделитель если сообщения прочитаны
                            var sepIdx = -1;
                            for (int si = 0; si < _messageItems.Count; si++) {
                                if (_messageItems[si].IsUnreadSeparator) { sepIdx = si; break; }
                            }
                            if (sepIdx >= 0 && newLastRead > 0) {
                                _messageItems.RemoveAt(sepIdx);
                            }
                        }
                        // Обновляем rawChatsDict для следующего открытия чата
                        if (_rawChatsDict.ContainsKey(ucriId)) {
                            var raw = _rawChatsDict[ucriId] as JObject;
                            if (raw != null)
                                raw["last_read_inbox_message_id"] = update["last_read_inbox_message_id"];
                        }
                    }
                    break;

                case "updateChatIsMarkedAsUnread":
                    long ucimId = update["chat_id"]?.ToObject<long>() ?? 0;
                    if (ucimId != 0 && _chatsDict.ContainsKey(ucimId))
                        _chatsDict[ucimId].IsMarkedUnread = update["is_marked_as_unread"]?.ToObject<bool>() ?? false;
                    break;

                case "updateUnreadChatCount":
                    if (update["chat_list"]?["@type"]?.ToString() == "chatListMain") {
                        int totalUnread = update["unread_unmuted_count"]?.ToObject<int>() ?? 0;
                        if (totalUnread == 0)
                            totalUnread = update["unread_count"]?.ToObject<int>() ?? 0;
                        UpdateAppBadge(totalUnread);
                        // Если всё прочитано — очищаем бейдж
                        if (totalUnread == 0)
                            Windows.UI.Notifications.BadgeUpdateManager.CreateBadgeUpdaterForApplication().Clear();
                    }
                    // TDLib присылает готовый счётчик непрочитанных при старте — используем для бейджа архива
                    if (update["chat_list"]?["@type"]?.ToString() == "chatListArchive") {
                        int archiveUnread = update["unread_unmuted_count"]?.ToObject<int>() ?? 0;
                        if (archiveUnread == 0)
                            archiveUnread = update["unread_count"]?.ToObject<int>() ?? 0;
                        if (archiveUnread > 0) {
                            ArchiveUnreadText.Text = archiveUnread > 99 ? "99+" : archiveUnread.ToString();
                            ArchiveUnreadBadge.Visibility = Visibility.Visible;
                            ArchiveArrow.Visibility = Visibility.Collapsed;
                        } else {
                            ArchiveUnreadBadge.Visibility = Visibility.Collapsed;
                            ArchiveArrow.Visibility = Visibility.Visible;
                        }
                    }
                    break;

                case "updateMessageInteractionInfo":
                    long umiChatId = update["chat_id"]?.ToObject<long>() ?? 0;
                    long umiMsgId = update["message_id"]?.ToObject<long>() ?? 0;
                    if (umiChatId == _currentChatId && _messagesDict.ContainsKey(umiMsgId)) {
                        var reacts = update["interaction_info"]?["reactions"]?["reactions"] as JArray;
                        _messagesDict[umiMsgId].Reactions = reacts != null && reacts.Count > 0
                            ? BuildReactionsString(reacts) : "";
                        var replyInfo = update["interaction_info"]?["reply_info"];
                        if (replyInfo != null)
                            _messagesDict[umiMsgId].ReplyCount = replyInfo["reply_count"]?.ToObject<int>() ?? 0;
                    }
                    break;

                case "updateChatReadOutbox":
                    long ucrId = update["chat_id"]?.ToObject<long>() ?? 0;
                    long ucrMsgId = update["last_read_outbox_message_id"]?.ToObject<long>() ?? 0;
                    if (ucrId != 0 && ucrMsgId > 0 && _chatsDict.ContainsKey(ucrId)) {
                        _chatsDict[ucrId].IsRead = true;
                        _chatsDict[ucrId].OutboxReadId = ucrMsgId;
                    }
                    // Обновляем галочки в открытом чате
                    if (ucrId == _currentChatId && ucrMsgId > 0) {
                        _currentChatOutboxReadId = ucrMsgId;
                        foreach (var m in _messageItems)
                            if (m.IsOutgoing && m.Id <= ucrMsgId)
                                m.IsRead = true;
                    }
                    break;

                case "messageThreadInfo":
                    // Ответ на getMessageThread — открываем тред
                    long threadChatId = update["chat_id"]?.ToObject<long>() ?? 0;
                    long threadMsgId  = update["message_thread_id"]?.ToObject<long>() ?? 0;
                    if (threadChatId != 0 && threadMsgId != 0 && _chatsDict.ContainsKey(threadChatId)) {
                        _threadMessageId = threadMsgId;
                        _threadChatId = threadChatId;
                        OpenChat(_chatsDict[threadChatId], threadMsgId);
                    }
                    break;

                case "stickerSets":
                    HandleStickerSets(update);
                    break;

                case "stickerSet":
                    HandleStickerSet(update["sticker_set"] ?? update);
                    break;

                case "updatePoll":
                    // Обновление результатов опроса
                    var updPoll = update["poll"];
                    if (updPoll != null) {
                        long updPollId = updPoll["id"]?.ToObject<long>() ?? 0;
                        // Ищем сообщение с этим опросом в текущем чате
                        var pollMsg = _messagesDict.Values.FirstOrDefault(m => m.IsPoll && m.Id == updPollId);
                        if (pollMsg == null) {
                            // Ищем по любому совпадению — poll id может не совпадать с msg id
                            pollMsg = _messagesDict.Values.FirstOrDefault(m => m.IsPoll);
                        }
                        if (pollMsg != null) {
                            int totalVotes = updPoll["total_voter_count"]?.ToObject<int>() ?? 0;
                            var opts = updPoll["options"] as JArray;
                            if (opts != null && pollMsg.PollOptions.Count == opts.Count) {
                                for (int i = 0; i < opts.Count; i++) {
                                    int votes = opts[i]["voter_count"]?.ToObject<int>() ?? 0;
                                    int pct = totalVotes > 0 ? (int)Math.Round(votes * 100.0 / totalVotes) : 0;
                                    pollMsg.PollOptions[i].VoteCount = votes;
                                    pollMsg.PollOptions[i].Percent   = pct;
                                    pollMsg.PollOptions[i].IsChosen  = opts[i]["is_chosen"]?.ToObject<bool>() ?? false;
                                }
                            }
                        }
                    }
                    break;

                case "users":
                    var contactUserIds = update["user_ids"] as JArray;
                    if (contactUserIds != null) {
                        var uids = contactUserIds;
                        var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, async () => {
                            try { await HandleContactsLoaded(uids); }
                            catch (Exception ex) { Log("CONTACTS ERR: " + ex.Message); }
                        });
                    }
                    break;

                case "userFullInfo":
                    // Bio пользователя
                    if (ProfileOverlay.Visibility == Visibility.Visible) {
                        string bio = update["bio"]?["text"]?.ToString() ?? "";
                        if (!string.IsNullOrEmpty(bio)) {
                            ProfileBio.Text = bio;
                            ProfileBioPanel.Visibility = Visibility.Visible;
                        }
                    }
                    break;

                case "supergroupFullInfo":
                    // Description группы/канала
                    if (ProfileOverlay.Visibility == Visibility.Visible) {
                        string desc = update["description"]?.ToString() ?? "";
                        if (!string.IsNullOrEmpty(desc)) {
                            ProfileBio.Text = desc;
                            ProfileBioPanel.Visibility = Visibility.Visible;
                        }
                    }
                    break;

                case "ok":
                    break;

                case "updateMessageContent":
                    long umcChatId = update["chat_id"]?.ToObject<long>() ?? 0;
                    long umcMsgId = update["message_id"]?.ToObject<long>() ?? 0;
                    if (umcChatId == _currentChatId && _messagesDict.ContainsKey(umcMsgId)) {
                        var content = update["new_content"];
                        string cType = content?["@type"]?.ToString() ?? "";
                        if (cType == "messageText") {
                            string newText = content["text"]?["text"]?.ToString() ?? "";
                            _messagesDict[umcMsgId].Text = newText;
                        }
                    }
                    break;

                case "updateMessageEdited":
                    // TDLib шлёт updateMessageEdited при редактировании — дозапрашиваем сообщение
                    long umeChat = update["chat_id"]?.ToObject<long>() ?? 0;
                    long umeMsg = update["message_id"]?.ToObject<long>() ?? 0;
                    if (umeChat == _currentChatId && _messagesDict.ContainsKey(umeMsg)) {
                        TdJson.SendUtf8(_client, "{\"@type\":\"getMessage\",\"chat_id\":" + umeChat + ",\"message_id\":" + umeMsg + "}");
                    }
                    break;

                case "chat":
                    long openChatId = update["id"]?.ToObject<long>() ?? 0;
                    // Открыть чат по упоминанию (searchPublicChat / createPrivateChat)
                    if (_pendingOpenChat && openChatId != 0) {
                        _pendingOpenChat = false;
                        if (_chatsDict.ContainsKey(openChatId))
                            OpenChat(_chatsDict[openChatId], 0);
                        else
                            _pendingHistoryChatId = openChatId;
                    }
                    // Ответ на getChat — берём pinned_message_id
                    long getChatId = openChatId;
                    if (getChatId != 0 && getChatId == _pendingPinnedChatId) {
                        _pendingPinnedChatId = 0;
                        // TDLib 1.8+ хранит список в pinned_message_ids
                        var pinnedIds = update["pinned_message_ids"] as Newtonsoft.Json.Linq.JArray;
                        long pinnedId = pinnedIds != null && pinnedIds.Count > 0
                            ? pinnedIds[0].ToObject<long>()
                            : update["pinned_message_id"]?.ToObject<long>() ?? 0;
                        if (pinnedId != 0 && getChatId == _currentChatId) {
                            _pinnedMessageId = pinnedId;
                            TdJson.SendUtf8(_client, "{\"@type\":\"getMessage\",\"chat_id\":" + getChatId + ",\"message_id\":" + pinnedId + "}");
                        }
                    }
                    // Ответ на createPrivateChat — открываем чат
                    if (_pendingContactUserId != 0) {
                        long newChatId = update["id"]?.ToObject<long>() ?? 0;
                        _pendingContactUserId = 0;
                        if (newChatId != 0) {
                            // updateNewChat придёт и добавит в _chatsDict, но может опоздать
                            // Создаём ChatItem на месте если ещё нет
                            if (!_chatsDict.ContainsKey(newChatId)) {
                                var ci = new ChatItem {
                                    Id = newChatId,
                                    Title = update["title"]?.ToString() ?? "Чат"
                                };
                                _chatsDict[newChatId] = ci;
                            }
                            OpenChat(_chatsDict[newChatId], 0);
                        }
                    }
                    break;

                case "message":
                    long fetchedMsgId = update["id"]?.ToObject<long>() ?? 0;
                    long fetchedChatId2 = update["chat_id"]?.ToObject<long>() ?? 0;
                    // Ответ на getChatPinnedMessage
                    // Ответ на getMessage для сервисного сообщения о закреплении
                    if (fetchedMsgId != 0 && _pinnedTextRequests.ContainsKey(fetchedMsgId)) {
                        long serviceMsgId = _pinnedTextRequests[fetchedMsgId];
                        _pinnedTextRequests.Remove(fetchedMsgId);
                        if (_messagesDict.ContainsKey(serviceMsgId)) {
                            var serviceItem = _messagesDict[serviceMsgId];
                            var pinnedContent = update["content"];
                            string pinnedType = pinnedContent?["@type"]?.ToString() ?? "";
                            string pinnedText = pinnedType == "messageText"
                                ? pinnedContent["text"]?["text"]?.ToString()
                                : pinnedType == "messagePhoto" ? "📷 Фото"
                                : pinnedType == "messageVideo" ? "🎥 Видео"
                                : pinnedType == "messageSticker" ? pinnedContent["sticker"]?["emoji"]?.ToString()
                                : "сообщение";
                            if (!string.IsNullOrEmpty(pinnedText))
                                serviceItem.Text = serviceItem.Text + "\n«" + pinnedText.Split('\n')[0].Substring(0, Math.Min(pinnedText.Length, 50)) + "»";
                        }
                    }

                    if (_pinnedMessageId == -1 && fetchedChatId2 == _currentChatId && fetchedMsgId != 0) {
                        _pinnedMessageId = fetchedMsgId;
                        var pc = update["content"];
                        string pType = pc?["@type"]?.ToString() ?? "";
                        string pText = pType == "messageText" ? pc["text"]?["text"]?.ToString()
                            : pType == "messagePhoto" ? "📷 Фото"
                            : pType == "messageVideo" ? "🎥 Видео"
                            : pType == "messageDocument" ? "📄 " + (pc["document"]?["file_name"]?.ToString() ?? "Файл")
                            : pType == "messageAudio" ? "🎵 Аудио"
                            : pType == "messageVoiceNote" ? "🎤 Голосовое"
                            : pType == "messageVideoNote" ? "⏺ Видеосообщение"
                            : pType == "messageSticker" ? pc["sticker"]?["emoji"]?.ToString() + " Стикер"
                            : "Сообщение";
                        PinnedMessageText.Text = pText ?? "";
                        PinnedMessageBar.Visibility = Visibility.Visible;
                    }
                    // Ответ на getMessage — заполняем ReplyToText если ждали
                    if (fetchedMsgId != 0 && _replyRequests.ContainsKey(fetchedMsgId)) {
                        var waitingItem = _replyRequests[fetchedMsgId];
                        _replyRequests.Remove(fetchedMsgId);
                        var fc = update["content"];
                        string fType = fc?["@type"]?.ToString() ?? "";
                        string fText = fType == "messageText"
                            ? fc["text"]?["text"]?.ToString()
                            : fType == "messagePhoto" ? "📷 Фото"
                            : fType == "messageVideo" ? "🎥 Видео"
                            : fType == "messageDocument" ? "📄 Файл"
                            : fType == "messageAudio" ? "🎵 Аудио"
                            : fType == "messageVoiceNote" ? "🎤 Голосовое"
                            : "Сообщение";
                        waitingItem.ReplyToText = string.IsNullOrEmpty(fText) ? "Сообщение" : fText;
                    }
                    // Обновляем текст если это ответ после редактирования
                    if (fetchedMsgId != 0 && _messagesDict.ContainsKey(fetchedMsgId)) {
                        var mc = update["content"];
                        string mcType = mc?["@type"]?.ToString() ?? "";
                        if (mcType == "messageText") {
                            string refreshed = mc["text"]?["text"]?.ToString() ?? "";
                            _messagesDict[fetchedMsgId].Text = refreshed;
                        } else if (mcType == "messagePoll") {
                            // Полная перезагрузка — заменяем MessageItem в списке целиком
                            var newItem = ParseMessage(update);
                            if (newItem != null && newItem.IsPoll) {
                                int idx = -1;
                                for (int i = 0; i < _messageItems.Count; i++)
                                    if (_messageItems[i].Id == fetchedMsgId) { idx = i; break; }
                                if (idx >= 0) {
                                    _messageItems[idx] = newItem;
                                    _messagesDict[fetchedMsgId] = newItem;
                                }
                            }
                        }
                    }
                    break;

                case "chats":
                    var chatIds = update["chat_ids"] as JArray;
                    if (chatIds != null) {
                        // Результаты поиска — если поисковый запрос активен
                        if (!string.IsNullOrEmpty(_searchQuery) && !_loadingArchiveIds && !_loadingChats && _pendingFolderLoad == 0) {
                                var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                                foreach (var cId in chatIds) {
                                    long id = (long)cId;
                                    if (_searchAllResults.Any(r => r.ChatId == id && r.Type == SearchResultItem.ResultType.Chat)) continue;
                                    if (!_searchAllResults.Any(r => r.IsHeader && r.Title == "Чаты"))
                                        _searchAllResults.Insert(0, new SearchResultItem { Type = SearchResultItem.ResultType.Header, Title = "Чаты" });
                                    // Берём данные из _chatsDict или _rawChatsDict
                                    string srTitle = _chatsDict.ContainsKey(id) ? _chatsDict[id].Title : "";
                                    BitmapImage srPhoto = _chatsDict.ContainsKey(id) ? _chatsDict[id].Photo : null;
                                    string srUsername = "";
                                    if (_rawChatsDict.ContainsKey(id)) {
                                        var raw = _rawChatsDict[id] as Newtonsoft.Json.Linq.JObject;
                                        if (string.IsNullOrEmpty(srTitle)) srTitle = raw?["title"]?.ToString() ?? "";
                                        srUsername = raw?["username"]?.ToString()
                                                  ?? raw?["usernames"]?["editable_username"]?.ToString()
                                                  ?? raw?["type"]?["username"]?.ToString()
                                                  ?? raw?["type"]?["usernames"]?["editable_username"]?.ToString()
                                                  ?? "";
                                        // Для супергрупп — ищем в _supergroupDict
                                        if (string.IsNullOrEmpty(srUsername)) {
                                            long sgId = raw?["type"]?["supergroup_id"]?.ToObject<long>() ?? 0;
                                            if (sgId != 0 && _supergroupDict.ContainsKey(sgId)) {
                                                var sg3 = _supergroupDict[sgId];
                                                srUsername = sg3["username"]?.ToString()
                                                          ?? sg3["usernames"]?["editable_username"]?.ToString() ?? "";
                                            }
                                            if (string.IsNullOrEmpty(srUsername) && sgId != 0)
                                                TdJson.SendUtf8(_client, "{\"@type\":\"getSupergroup\",\"supergroup_id\":" + sgId + "}");
                                        }
                                        // Для приватных чатов (пользователей) — берём username из _usersDict
                                        if (string.IsNullOrEmpty(srUsername)) {
                                            long uid3 = raw?["type"]?["user_id"]?.ToObject<long>() ?? 0;
                                            if (uid3 != 0 && _usersDict.ContainsKey(uid3)) {
                                                var u3 = _usersDict[uid3];
                                                srUsername = u3["username"]?.ToString()
                                                          ?? u3["usernames"]?["editable_username"]?.ToString() ?? "";
                                            }
                                        }
                                    }
                                    if (string.IsNullOrEmpty(srTitle)) continue;
                                    string srSubtitle = !string.IsNullOrEmpty(srUsername) ? "@" + srUsername : "";
                                    var srItem = new SearchResultItem {
                                        Type = SearchResultItem.ResultType.Chat,
                                        ChatId = id, Title = srTitle,
                                        Subtitle = srSubtitle, Photo = srPhoto
                                    };
                                    _searchAllResults.Add(srItem);
                                    // Если фото нет — запускаем загрузку
                                    if (srPhoto == null && _rawChatsDict.ContainsKey(id)) {
                                        var rawCh = _rawChatsDict[id] as Newtonsoft.Json.Linq.JObject;
                                        var phSmallSr = rawCh?["photo"]?["small"];
                                        if (phSmallSr != null) {
                                            long phFid = phSmallSr["id"]?.ToObject<long>() ?? 0;
                                            string phPath = phSmallSr["local"]?["path"]?.ToString();
                                            if (!string.IsNullOrEmpty(phPath))
                                                { var t2 = UpdateAvatarSearchResult(srItem, phPath); }
                                            else if (phFid > 0) {
                                                _fileToSearchResult[phFid] = srItem;
                                                TdJson.SendUtf8(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + phFid + ",\"priority\":10,\"synchronous\":false}");
                                            }
                                        }
                                    }
                                }
                            });
                            break;
                        }
                        if (_loadingArchiveIds) {
                            // Pre-fetch: сохраняем id архивных чатов, потом грузим главный список
                            _loadingArchiveIds = false;
                            _archiveChatIds.Clear();
                            foreach (var cId in chatIds)
                                _archiveChatIds.Add((long)cId);
                            TdJson.SendUtf8(_client, "{\"@type\":\"getChats\",\"chat_list\":{\"@type\":\"chatListMain\"},\"limit\":1000}");
                            _loadingChats = true;
                        } else if (_pendingFolderLoad != 0) {
                            // Чаты папки
                            int fid = _pendingFolderLoad;
                            _pendingFolderLoad = 0;
                            var folderIds = new List<long>();
                            foreach (var cId in chatIds)
                                folderIds.Add((long)cId);
                            _folderChatIds[fid] = folderIds;
                            if (_currentFolderId == fid)
                                SwitchFolder(fid);
                            LoadNextFolder(); // загружаем следующую папку
                        } else {
                            _pendingChatIds.Clear();
                            foreach (var cId in chatIds)
                                _pendingChatIds.Enqueue((long)cId);
                            if (chatIds.Count == 0 && _loadingArchive) {
                                _loadingArchive = false;
                                ArchiveChatCountText.Text = "архив пуст";
                            }
                            LoadNextChat();
                        }
                    }
                    break;

                case "foundMessages":
                    // Ответ на searchMessages
                    if (!string.IsNullOrEmpty(_searchQuery)) {
                        var foundMsgs2 = update["messages"] as JArray;
                        if (foundMsgs2 != null && foundMsgs2.Count > 0) {
                            var ignored3 = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                                bool hadHdr = _searchAllResults.Any(r => r.IsHeader && r.Title == "Сообщения");
                                foreach (var fm in foundMsgs2) {
                                    long fmChatId = fm["chat_id"]?.ToObject<long>() ?? 0;
                                    long fmMsgId  = fm["id"]?.ToObject<long>() ?? 0;
                                    string fmText = fm["content"]?["text"]?["text"]?.ToString()
                                                 ?? fm["content"]?["caption"]?["text"]?.ToString() ?? "";
                                    if (string.IsNullOrEmpty(fmText)) continue;
                                    if (_searchAllResults.Any(r => r.MessageId == fmMsgId)) continue;
                                    if (!hadHdr) {
                                        _searchAllResults.Add(new SearchResultItem { Type = SearchResultItem.ResultType.Divider });
                                        _searchAllResults.Add(new SearchResultItem { Type = SearchResultItem.ResultType.Header, Title = "Сообщения" });
                                        hadHdr = true;
                                    }
                                    string chatTitle = _chatsDict.ContainsKey(fmChatId) ? _chatsDict[fmChatId].Title : "Чат";
                                    BitmapImage chatPhoto = _chatsDict.ContainsKey(fmChatId) ? _chatsDict[fmChatId].Photo : null;
                                    int date = fm["date"]?.ToObject<int>() ?? 0;
                                    string dateStr = date > 0 ? DateTimeOffset.FromUnixTimeSeconds(date).LocalDateTime.ToString("dd.MM HH:mm") : "";
                                    _searchAllResults.Add(new SearchResultItem {
                                        Type = SearchResultItem.ResultType.Message,
                                        ChatId = fmChatId, MessageId = fmMsgId,
                                        Title = chatTitle, Subtitle = fmText,
                                        DateText = dateStr, Photo = chatPhoto
                                    });
                                }
                            });
                        }
                    }
                    break;

                case "messages":
                    // Результаты searchMessages
                    if (!string.IsNullOrEmpty(_searchQuery) && update["total_count"] != null && _pendingHistoryChatId == 0) {
                        var foundMsgs = update["messages"] as JArray;
                        if (foundMsgs != null && foundMsgs.Count > 0) {
                            var ignored2 = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                                bool hadHeader = _searchAllResults.Any(r => r.IsHeader && r.Title == "Сообщения");
                                foreach (var fm in foundMsgs) {
                                    long fmChatId = fm["chat_id"]?.ToObject<long>() ?? 0;
                                    long fmMsgId  = fm["id"]?.ToObject<long>() ?? 0;
                                    string fmText = fm["content"]?["text"]?["text"]?.ToString()
                                                 ?? fm["content"]?["caption"]?["text"]?.ToString() ?? "";
                                    if (string.IsNullOrEmpty(fmText)) continue;
                                    if (_searchAllResults.Any(r => r.MessageId == fmMsgId)) continue;
                                    if (!hadHeader) {
                                        _searchAllResults.Add(new SearchResultItem { Type = SearchResultItem.ResultType.Header, Title = "Сообщения" });
                                        hadHeader = true;
                                    }
                                    string chatTitle = _chatsDict.ContainsKey(fmChatId) ? _chatsDict[fmChatId].Title : "Чат";
                                    BitmapImage chatPhoto = _chatsDict.ContainsKey(fmChatId) ? _chatsDict[fmChatId].Photo : null;
                                    int date = fm["date"]?.ToObject<int>() ?? 0;
                                    string dateStr = date > 0 ? DateTimeOffset.FromUnixTimeSeconds(date).LocalDateTime.ToString("dd.MM HH:mm") : "";
                                    _searchAllResults.Add(new SearchResultItem {
                                        Type = SearchResultItem.ResultType.Message,
                                        ChatId = fmChatId, MessageId = fmMsgId,
                                        Title = chatTitle, Subtitle = fmText,
                                        DateText = dateStr, Photo = chatPhoto
                                    });
                                }
                            });
                        }
                        break;
                    }
                    long expectedChat = _pendingHistoryChatId;
                    var msgs = update["messages"] as JArray;
                    int totalCount = update["total_count"]?.ToObject<int>() ?? 0;
                    if (expectedChat != _currentChatId) { Log("SKIP — user switched chat"); break; }
                    int gotCount = msgs?.Count ?? 0;

                    if (!_loadingOlderHistory) {
                        // Начальная загрузка — retry если пришло слишком мало
                        if (gotCount < 2 && _historyRetryCount < 2) {
                            _historyRetryCount++;
                            var retryChat = _currentChatId;
                            Task.Delay(800).ContinueWith(_ =>
                                Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                                    if (_currentChatId == retryChat)
                                        TdJson.SendUtf8(_client, "{\"@type\":\"getChatHistory\",\"chat_id\":" + retryChat + ",\"from_message_id\":0,\"offset\":0,\"limit\":50}");
                                }));
                            break;
                        }
                        _messageItems.Clear();
                        _hasMoreHistory = gotCount > 0;
                        _outOfMemory = false;
                        for (int i = msgs.Count - 1; i >= 0; i--) {
                            var it = ParseMessage(msgs[i]);
                            if (it != null) _messageItems.Add(it);
                        }
                        InsertDateSeparators();
                        // Если получили меньше 50 — дозагружаем более старые
                        if (gotCount > 0 && gotCount < 50) {
                            long oldestId = msgs[msgs.Count - 1]?["id"]?.ToObject<long>() ?? 0;
                            if (oldestId != 0) {
                                _loadingOlderHistory = true;
                                TdJson.SendUtf8(_client, "{\"@type\":\"getChatHistory\",\"chat_id\":" + expectedChat + ",\"from_message_id\":" + oldestId + ",\"offset\":0,\"limit\":" + (50 - gotCount) + "}");
                            }
                        }
                        _isLoadingHistory = false;
                        LoadingIndicator.Visibility = Visibility.Collapsed;
                        MessagesListView.Visibility = Visibility.Visible;
                        // Кнопка Старт для ботов с пустой историей
                        if (_currentChatIsBot && _messageItems.Count == 0)
                            StartBotButton.Visibility = Visibility.Visible;
                        else
                            StartBotButton.Visibility = Visibility.Collapsed;
                        if (_messageItems.Count > 0) {
                            if (_pendingScrollToMsgId != 0) {
                                // Скроллим к конкретному сообщению из поиска
                                long scrollTarget = _pendingScrollToMsgId;
                                _pendingScrollToMsgId = 0;
                                // Ждём рендера и скроллим
                                var st = new Windows.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                                st.Tick += (ts, te) => {
                                    st.Stop();
                                    var target = _messageItems.FirstOrDefault(m => !m.IsSeparator && m.Id == scrollTarget);
                                    if (target != null)
                                        MessagesListView.ScrollIntoView(target, ScrollIntoViewAlignment.Leading);
                                    else
                                        MessagesScrollViewer.ChangeView(null, MessagesScrollViewer.ScrollableHeight, null, false);
                                };
                                st.Start();
                            } else {
                                ScrollToBottomDelayed();
                            }
                        }
                        long lastMsgId = _messageItems.Count > 0 ? _messageItems[_messageItems.Count - 1].Id : 0;
                        if (lastMsgId != 0)
                            TdJson.SendUtf8(_client, "{\"@type\":\"viewMessages\",\"chat_id\":" + expectedChat + ",\"message_ids\":[" + lastMsgId + "],\"force_read\":true}");
                    } else if (_loadingOlderHistory) {
                        // Дозагрузка старых — вставляем в начало, сохраняем позицию скролла
                        _loadingOlderHistory = false;
                        OlderLoadingIndicator.Visibility = Visibility.Collapsed;
                        OlderProgressRing.IsActive = false;
                        if (gotCount == 0) {
                            _hasMoreHistory = false;
                        } else {
                            ulong memUsage = Windows.System.MemoryManager.AppMemoryUsage;
                            if (memUsage > MemoryThreshold) {
                                _hasMoreHistory = false;
                                _outOfMemory = true;
                                MemoryWarningBanner.Visibility = Visibility.Visible;
                            } else {
                                _scrollTimer?.Stop();
                                _autoScrolling = false;
                                double oldHeight = MessagesScrollViewer.ExtentHeight;
                                double oldOffset = MessagesScrollViewer.VerticalOffset;
                                int insertIdx = 0;
                                for (int i = msgs.Count - 1; i >= 0; i--) {
                                    var it = ParseMessage(msgs[i]);
                                    if (it != null) _messageItems.Insert(insertIdx++, it);
                                }
                                RebuildDateSeparators();
                                _hasMoreHistory = gotCount > 0;
                                _trimming = true;
                                double capturedOld = oldOffset;
                                double capturedOldH = oldHeight;
                                int attempts = 0;
                                var fixTimer = new Windows.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
                                fixTimer.Tick += (ft, fe) => {
                                    double newH = MessagesScrollViewer.ExtentHeight;
                                    if (newH > capturedOldH || attempts >= 10) {
                                        fixTimer.Stop();
                                        MessagesScrollViewer.ChangeView(null, capturedOld + (newH - capturedOldH), null, true);
                                        _trimming = false;
                                    }
                                    attempts++;
                                };
                                fixTimer.Start();
                            }
                        }
                    }
                    break;
            }
        }

        private ScrollViewer FindScrollViewer(DependencyObject element) {
            if (element is ScrollViewer sv) return sv;
            int count = Windows.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(element);
            for (int i = 0; i < count; i++) {
                var result = FindScrollViewer(Windows.UI.Xaml.Media.VisualTreeHelper.GetChild(element, i));
                if (result != null) return result;
            }
            return null;
        }

        private double _prevScrollOffset = 0;

        private void MessagesScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e) {
            double offset    = MessagesScrollViewer.VerticalOffset;
            double scrollable = MessagesScrollViewer.ScrollableHeight;
            bool atBottom = scrollable <= 0 || (scrollable - offset) < 50;

            // Если пользователь скроллит вверх вручную — останавливаем автоскролл вниз
            bool scrollingUp = offset < _prevScrollOffset;
            if (scrollingUp && _autoScrolling) {
                _scrollTimer?.Stop();
                _autoScrolling = false;
            }
            _prevScrollOffset = offset;

            if (_autoScrolling && atBottom) {
                _autoScrolling = false;
            }

            ScrollToBottomButton.Visibility = atBottom ? Visibility.Collapsed : Visibility.Visible;
            ScrollToBottomButton.Content = "↓";

            bool nearTop = offset < 50;
            if (nearTop && !_loadingOlderHistory && !_isLoadingHistory && _hasMoreHistory
                && _currentChatId != 0 && !_autoScrolling && !_outOfMemory && !_trimming) {
                LoadOlderMessages();
            }
        }

        private void ScrollToBottom_Click(object sender, RoutedEventArgs e) {
            MessagesScrollViewer.ChangeView(null, MessagesScrollViewer.ScrollableHeight, null, false);
        }

        private void ScrollToBottomDelayed() {
            _scrollTimer?.Stop();
            _autoScrolling = true;
            double prevExtent = -1;
            int stableTicks = 0;
            int totalTicks = 0;
            _scrollTimer = new Windows.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _scrollTimer.Tick += (s2, e2) => {
                totalTicks++;
                double sh = MessagesScrollViewer.ExtentHeight;
                if (sh > 0 && sh == prevExtent) {
                    stableTicks++;
                    if (stableTicks >= 2) {
                        _scrollTimer.Stop();
                        var unreadSep = _messageItems.FirstOrDefault(m => m.IsUnreadSeparator);
                        if (unreadSep != null) {
                            int sepIdx = _messageItems.IndexOf(unreadSep);
                            double itemH = sh / Math.Max(_messageItems.Count, 1);
                            MessagesScrollViewer.ChangeView(null, sepIdx * itemH, null, false);
                        } else {
                            MessagesScrollViewer.ChangeView(null, MessagesScrollViewer.ScrollableHeight, null, false);
                        }
                    }
                } else {
                    stableTicks = 0;
                    prevExtent = sh;
                }
                if (totalTicks >= 30) {
                    _scrollTimer.Stop();
                    MessagesScrollViewer.ChangeView(null, MessagesScrollViewer.ScrollableHeight, null, false);
                    _autoScrolling = false;
                }
            };
            _scrollTimer.Start();
        }

        private void LoadOlderMessages() {
            // Берём самое старое сообщение — оно в начале списка (старые = индекс 0)
            var oldest = _messageItems.FirstOrDefault(m => !m.IsSeparator);
            if (oldest == null) return;
            _loadingOlderHistory = true;
            OlderLoadingIndicator.Visibility = Visibility.Visible;
            OlderProgressRing.IsActive = true;
            string req = _threadMessageId != 0
                ? "{\"@type\":\"getMessageThreadHistory\",\"chat_id\":" + _currentChatId + ",\"message_id\":" + _threadMessageId + ",\"from_message_id\":" + oldest.Id + ",\"offset\":0,\"limit\":50}"
                : "{\"@type\":\"getChatHistory\",\"chat_id\":" + _currentChatId + ",\"from_message_id\":" + oldest.Id + ",\"offset\":0,\"limit\":50}";
            TdJson.SendUtf8(_client, req);
        }

        private void UpdateChatStatus(JToken status) {
            if (status == null) { CurrentChatStatus.Text = ""; return; }
            string type = status["@type"]?.ToString();
            string text = "";
            switch (type) {
                case "userStatusOnline":
                    text = "в сети";
                    CurrentChatStatus.Foreground = CB("#2AABEE");
                    break;
                case "userStatusOffline":
                    long wasOnline = status["was_online"]?.ToObject<long>() ?? 0;
                    text = wasOnline > 0 ? "был(а) " + FormatLastSeen(wasOnline) : "не в сети";
                    CurrentChatStatus.Foreground = CB(_isLightTheme ? "#000000" : "#CCE8FF");
                    break;
                case "userStatusRecently":
                    text = "был(а) недавно";
                    CurrentChatStatus.Foreground = CB(_isLightTheme ? "#000000" : "#CCE8FF");
                    break;
                case "userStatusLastWeek":
                    text = "был(а) на этой неделе";
                    CurrentChatStatus.Foreground = CB(_isLightTheme ? "#000000" : "#CCE8FF");
                    break;
                case "userStatusLastMonth":
                    text = "был(а) в этом месяце";
                    CurrentChatStatus.Foreground = CB(_isLightTheme ? "#000000" : "#CCE8FF");
                    break;
            }
            CurrentChatStatus.Text = text;
        }

        private void LoadNextChat() {
            if (_pendingChatIds.Count == 0) {
                if (_loadingChats) {
                    _loadingChats = false;
                    _mainListLoaded = true;
                    for (int pi2 = 0; pi2 < Math.Min(_chatListItems.Count, 15); pi2++)
                    LoadNextFolder();
                }
                if (_loadingArchive) {
                    _loadingArchive = false;
                    ArchiveChatCountText.Text = _archiveChatItems.Count == 0
                        ? "архив пуст" : "чатов: " + _archiveChatItems.Count;
                    UpdateArchiveUnreadBadge();
                }
                return;
            }
            long nextId = _pendingChatIds.Dequeue();
            Task.Delay(100).ContinueWith(_ =>
                Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                    if (_chatsDict.ContainsKey(nextId)) {
                        var existing = _chatsDict[nextId];
                        // Определяем список по флагу загрузки
                        if (_loadingArchive) {
                            if (!_archiveChatItems.Contains(existing)) {
                                _archiveChatItems.Add(existing);
                                ArchiveChatCountText.Text = "чатов: " + _archiveChatItems.Count;
                            }
                        } else {
                            if (!_chatListItems.Contains(existing)) {
                                if (existing.IsPinned) {
                                    int insertAt = 0;
                                    for (int pi = 0; pi < _chatListItems.Count; pi++)
                                        if (_chatListItems[pi].IsPinned) insertAt = pi + 1;
                                    _chatListItems.Insert(insertAt, existing);
                                } else {
                                    _chatListItems.Add(existing);
                                }
                                if (!_allChatItems.Contains(existing))
                                    _allChatItems.Add(existing);
                                ChatCountText.Text = _chatListItems.Count.ToString();
                            }
                        }
                    } else {
                        // Чат ещё не известен — запрашиваем, updateNewChat вызовет LoadNextChat сам
                        _pendingGetChat.Add(nextId);
                        TdJson.SendUtf8(_client, "{\"@type\":\"getChat\",\"chat_id\":" + nextId + "}");
                        return; // не вызываем LoadNextChat здесь — иначе двойной поток
                    }
                    LoadNextChat();
                }));
        }

        // Вставляет разделители дат в _messageItems (полная перестройка)
        private void InsertDateSeparators() {
            var today = DateTime.Today;
            DateTime? lastDate = null;
            int i = 0;
            while (i < _messageItems.Count) {
                var item = _messageItems[i];
                if (item.IsSeparator) { i++; continue; }
                var msgDay = item.RawDate.Date;
                if (lastDate == null || msgDay != lastDate.Value) {
                    _messageItems.Insert(i, MakeSeparator(msgDay, today));
                    i += 2;
                } else { i++; }
                lastDate = msgDay;
            }
            InsertUnreadSeparator();
        }

        private void InsertUnreadSeparator() {
            if (_lastReadInboxMsgId <= 0) return;
            // Перевёрнутый список: новые в начале (индекс 0)
            // Разделитель вставляем перед первым сообщением у которого Id <= _lastReadInboxMsgId
            for (int i = 0; i < _messageItems.Count; i++) {
                var item = _messageItems[i];
                if (item.IsSeparator) continue;
                if (!item.IsOutgoing && item.Id > _lastReadInboxMsgId) {
                    // Это первое непрочитанное — разделитель перед ним
                    var sep = new MessageItem {
                        IsSeparator = true,
                        SeparatorLabel = "Новые сообщения",
                        IsUnreadSeparator = true,
                        Background = "#00000000"
                    };
                    _messageItems.Insert(i, sep);
                    return;
                }
            }
        }

        // Удаляет все разделители и вставляет заново (после дозагрузки старых сообщений)
        // Вставляет разделители дат только для диапазона [0..count] новых сообщений
        // Не трогает остальной список
        private void InsertDateSeparatorsForRange(int start, int count) {
            var today = DateTime.Today;
            // Обрабатываем только новые сообщения + первое старое для проверки границы
            int end = start + count;
            // Идём с конца диапазона к началу чтобы вставка не сбивала индексы
            DateTime? prevDay = null;
            // Узнаём день первого сообщения после нашего диапазона
            for (int i = end; i < _messageItems.Count; i++) {
                if (!_messageItems[i].IsSeparator) { prevDay = _messageItems[i].RawDate.Date; break; }
            }
            // Вставляем разделители для новых сообщений
            int i2 = end - 1;
            while (i2 >= start) {
                var item = _messageItems[i2];
                if (item.IsSeparator) { i2--; continue; }
                var day = item.RawDate.Date;
                if (prevDay == null || day != prevDay.Value) {
                    // Нужен разделитель перед следующим сообщением с другим днём
                    // Ищем следующее не-сепаратор после i2
                    bool needSep = prevDay == null || day != prevDay.Value;
                    if (needSep && i2 + 1 < _messageItems.Count) {
                        var next = _messageItems[i2 + 1];
                        if (!next.IsSeparator || !next.SeparatorLabel.Equals(MakeSeparator(day, today).SeparatorLabel))
                            _messageItems.Insert(i2 + 1, MakeSeparator(day, today));
                    }
                }
                prevDay = day;
                i2--;
            }
            // Проверяем нужен ли разделитель в самом начале
            if (_messageItems.Count > 0) {
                var first = _messageItems[0];
                if (!first.IsSeparator)
                    _messageItems.Insert(0, MakeSeparator(first.RawDate.Date, today));
            }
        }

        private void RebuildDateSeparators() {
            for (int i = _messageItems.Count - 1; i >= 0; i--)
                if (_messageItems[i].IsSeparator) _messageItems.RemoveAt(i);
            InsertDateSeparators();
        }

        private MessageItem MakeSeparator(DateTime day, DateTime today) {
            string label;
            int diff = (today - day).Days;
            if (diff == 0)       label = "Сегодня";
            else if (diff == 1)  label = "Вчера";
            else if (diff == 2)  label = "Позавчера";
            else if (day.Year == today.Year)
                                 label = day.ToString("d MMMM", new System.Globalization.CultureInfo("ru-RU"));
            else                 label = day.ToString("d MMMM yyyy", new System.Globalization.CultureInfo("ru-RU"));
            return new MessageItem { IsSeparator = true, SeparatorLabel = label };
        }

        // Вставляет незакреплённый чат сразу после последнего закреплённого
        // Закреплённый чат всегда вставляется в самый верх (позиция 0)
        private void InsertAfterPinned(ObservableCollection<ChatItem> list, ChatItem item) {
            if (item.IsPinned) {
                // Вставляем после других закреплённых
                int pinnedIdx = 0;
                for (int i = 0; i < list.Count; i++)
                    if (list[i].IsPinned) pinnedIdx = i + 1;
                list.Insert(pinnedIdx, item);
                return;
            }
            int insertAt = 0;
            for (int i = 0; i < list.Count; i++) {
                if (list[i].IsPinned) insertAt = i + 1;
            }
            list.Insert(insertAt, item);
        }

        private void MoveChatToTop(long chatId) {
            var list = _inArchive ? _archiveChatItems : _chatListItems;
            var item = list.FirstOrDefault(c => c.Id == chatId);
            if (item == null) return;
            // Закреплённые не двигаем — они всегда наверху
            if (item.IsPinned) return;
            // Уже на правильной позиции (сразу после закреплённых)?
            int pinnedCount = list.Count(c => c.IsPinned);
            if (list.IndexOf(item) == pinnedCount) return;
            list.Remove(item);
            InsertAfterPinned(list, item);
        }

        private long _serverTimeOffset = 0;
        private bool _serverTimeOffsetSet = false;

        private void UpdateServerTimeOffset(long serverUnix) {
            if (_serverTimeOffsetSet) return; // устанавливаем только один раз
            long localUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _serverTimeOffset = serverUnix - localUnix;
            _serverTimeOffsetSet = true;
        }

        private long LocalUnixNow() {
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds() + _serverTimeOffset;
        }

        private string FormatLastSeen(long unixTime) {
            long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long diffSec = nowUnix - unixTime;
            if (diffSec < 0) diffSec = 0;
            if (diffSec < 60) return "только что";
            if (diffSec < 3600) return (diffSec / 60) + " мин. назад";
            var dtLocal = DateTimeOffset.FromUnixTimeSeconds(unixTime).LocalDateTime;
            var nowLocal = DateTimeOffset.UtcNow.ToLocalTime().DateTime;
            if (dtLocal.Day == nowLocal.Day) return "сегодня в " + dtLocal.ToString("HH:mm");
            if (dtLocal.Day == nowLocal.AddDays(-1).Day) return "вчера в " + dtLocal.ToString("HH:mm");
            return dtLocal.ToString("d MMM в HH:mm");
        }

        private string FormatCallDuration(int seconds) {
            if (seconds < 60) return seconds + " сек";
            int m = seconds / 60, s = seconds % 60;
            return m + ":" + s.ToString("D2");
        }

        private string FormatFileSize(long bytes) {
            if (bytes <= 0) return "";
            if (bytes < 1024) return bytes + " Б";
            if (bytes < 1024 * 1024) return (bytes / 1024) + " КБ";
            if (bytes < 1024 * 1024 * 1024) return (bytes / (1024 * 1024)) + " МБ";
            return (bytes / (1024 * 1024 * 1024)) + " ГБ";
        }

        private void FillChatLastMessage(ChatItem item, JToken msg, JToken chatOrUpdate) {
            try {
                var content = msg["content"];
                string mtype = content?["@type"]?.ToString() ?? "";
                string text = mtype == "messageText"
                    ? content["text"]?["text"]?.ToString() ?? ""
                    : mtype == "messagePhoto" ? "📷 Фото"
                    : mtype == "messageVideo" && (content["video"]?["is_animation"]?.ToObject<bool>() ?? false) ? "🎞 GIF"
                    : mtype == "messageVideo" ? "🎥 Видео"
                    : mtype == "messageVoiceNote" ? "🎤 Голосовое"
                    : mtype == "messageSticker" ? "Стикер"
                    : mtype == "messagePoll" ? "📊 Опрос"
                    : mtype == "messageDocument" ? "📄 Документ"
                    : mtype == "messageAnimation" ? "🎞 GIF"
                    : mtype == "messageCall" ? ((content["is_video"]?.ToObject<bool>() ?? false) ? "📹" : "📞") + " Звонок"
                    : mtype == "messageAudio" ? "🎵 Аудио"
                    : "[" + mtype.Replace("message", "") + "]";
                item.LastMessage = text;
                long date = msg["date"]?.ToObject<long>() ?? 0;
                if (date > 0)
                    item.LastMessageTime = DateTimeOffset.FromUnixTimeSeconds(date).LocalDateTime.ToString("HH:mm");
                item.IsOutgoing = msg["is_outgoing"]?.ToObject<bool>() ?? false;
                long msgId = msg["id"]?.ToObject<long>() ?? 0;
                long readOutbox = chatOrUpdate["last_read_outbox_message_id"]?.ToObject<long>() ?? -1;
                if (readOutbox > 0) item.OutboxReadId = readOutbox;
                // IsRead только если OutboxReadId > 0 и >= msgId (защита от непрогретой базы)
                item.IsRead = item.IsOutgoing && item.OutboxReadId > 0 && item.OutboxReadId >= msgId;
            } catch { }
        }

        // Цвета для ников (по user_id % количество цветов)
        private static readonly string[] _senderColors = {
            "#E17076", "#7EC8E3", "#A695E7", "#76C99F",
            "#F2C94C", "#F78C6C", "#67D7CC", "#FF8A65"
        };

        private string GetSenderName(JToken senderId) {
            if (senderId == null) return "";
            string sType = senderId["@type"]?.ToString();
            if (sType == "messageSenderUser") {
                long uid = senderId["user_id"]?.ToObject<long>() ?? 0;
                if (_usersDict.ContainsKey(uid)) {
                    var u = _usersDict[uid];
                    string fn = u["first_name"]?.ToString() ?? "";
                    string ln = u["last_name"]?.ToString() ?? "";
                    return (fn + " " + ln).Trim();
                }
                return "User " + uid;
            }
            if (sType == "messageSenderChat") {
                long cid = senderId["chat_id"]?.ToObject<long>() ?? 0;
                if (_chatsDict.ContainsKey(cid)) return _chatsDict[cid].Title;
                return "Chat " + cid;
            }
            return "";
        }

        private string GetSenderColor(JToken senderId) {
            if (senderId == null) return _senderColors[0];
            long id = senderId["user_id"]?.ToObject<long>()
                   ?? senderId["chat_id"]?.ToObject<long>() ?? 0;
            return _senderColors[Math.Abs((int)(id % _senderColors.Length))];
        }

        private MessageItem ParseMessage(JToken msg) {
            try {
                long msgId = (long)msg["id"];
                var content = msg["content"];
                string type = content["@type"]?.ToString();
                string txt = type == "messageText"
                    ? content["text"]?["text"]?.ToString() ?? ""
                    : content["caption"]?["text"]?.ToString() ?? "";

                // Парсим entities для ссылок и упоминаний
                var entitiesJson = type == "messageText"
                    ? content["text"]?["entities"] as Newtonsoft.Json.Linq.JArray
                    : content["caption"]?["entities"] as Newtonsoft.Json.Linq.JArray;
                var entities = new List<MessageEntity>();
                if (entitiesJson != null) {
                    foreach (var ent in entitiesJson) {
                        string eType = ent["type"]?["@type"]?.ToString() ?? "";
                        int offset = ent["offset"]?.ToObject<int>() ?? 0;
                        int length = ent["length"]?.ToObject<int>() ?? 0;
                        string url = null;
                        string mention = null;
                        if (eType == "textEntityTypeUrl")
                            url = txt.Substring(Math.Max(0, offset), Math.Min(length, txt.Length - offset));
                        else if (eType == "textEntityTypeTextUrl")
                            url = ent["type"]?["url"]?.ToString();
                        else if (eType == "textEntityTypeMention" && txt.Length >= offset + length)
                            mention = txt.Substring(offset, length); // @username
                        else if (eType == "textEntityTypeMentionName")
                            mention = "@id" + (ent["type"]?["user_id"]?.ToString() ?? "");
                        if (url != null) entities.Add(new MessageEntity { Offset = offset, Length = length, Url = url });
                        if (mention != null) entities.Add(new MessageEntity { Offset = offset, Length = length, Mention = mention });
                    }
                }

                bool outgoing = (bool)msg["is_outgoing"];
                var senderId = msg["sender_id"];
                var msgDate = DateTimeOffset.FromUnixTimeSeconds((long)msg["date"]).LocalDateTime;
                var item = new MessageItem {
                    Id = msgId, Text = txt,
                    Entities = entities.Count > 0 ? entities : null,
                    RawDate = msgDate,
                    Date = msgDate.ToString("HH:mm"),
                    Alignment = outgoing ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                    Background = outgoing ? BubbleColorOut : BubbleColorIn,
                    IsOutgoing = outgoing,
                    IsRead = outgoing && (msg["id"]?.ToObject<long>() ?? 0) <= _currentChatOutboxReadId,
                    SenderName = outgoing ? "" : (_currentChatIsGroup ? GetSenderName(senderId) : ""),
                    SenderColor = GetSenderColor(senderId)
                };

                var replyTo = msg["reply_to"];
                if (replyTo != null && replyTo["@type"]?.ToString() == "messageReplyToMessage") {
                    // Автор цитаты
                    var replyOrigin = replyTo["origin"];
                    if (replyOrigin != null) {
                        string oType = replyOrigin["@type"]?.ToString();
                        if (oType == "messageOriginUser") {
                            long oUid = replyOrigin["sender_user_id"]?.ToObject<long>() ?? 0;
                            if (_usersDict.ContainsKey(oUid)) {
                                var u = _usersDict[oUid];
                                item.ReplyAuthor = (u["first_name"]?.ToString() + " " + u["last_name"]?.ToString()).Trim();
                            }
                        } else if (oType == "messageOriginChat" || oType == "messageOriginChannel") {
                            long oCid = replyOrigin["sender_chat_id"]?.ToObject<long>() ?? 0;
                            if (_chatsDict.ContainsKey(oCid)) item.ReplyAuthor = _chatsDict[oCid].Title;
                        }
                    }
                    // Текст цитаты — сначала quote (выделенный фрагмент), потом content
                    // quote.text — это formattedText объект, поэтому нужно ["text"]["text"]
                    var quoteObj = replyTo["quote"]?["text"];
                    string replyText = quoteObj?["text"]?.ToString()  // formattedText.text
                                    ?? quoteObj?.ToString();           // fallback если вдруг строка
                    if (string.IsNullOrEmpty(replyText)) {
                        var replyContent = replyTo["content"];
                        if (replyContent != null) {
                            string rType = replyContent["@type"]?.ToString();
                            replyText = rType == "messageText"
                                ? replyContent["text"]?["text"]?.ToString()
                                : rType == "messagePhoto" ? "📷 Фото"
                                : rType == "messageVideo" ? "🎥 Видео"
                                : rType == "messageDocument" ? "📄 Файл"
                                : rType == "messageAudio" ? "🎵 Аудио"
                                : rType == "messageVoiceNote" ? "🎤 Голосовое"
                                : null;
                        }
                    }
                    item.ReplyToText = string.IsNullOrEmpty(replyText) ? "…" : replyText;
                    // Если текст не получили — запрашиваем сообщение явно
                    if (string.IsNullOrEmpty(replyText)) {
                        long replyMsgId = replyTo["message_id"]?.ToObject<long>() ?? 0;
                        long replyChatId = replyTo["chat_id"]?.ToObject<long>() ?? 0;
                        if (replyChatId == 0) replyChatId = (long)msg["chat_id"];
                        if (replyMsgId != 0) {
                            _replyRequests[replyMsgId] = item;
                            TdJson.SendUtf8(_client, "{\"@type\":\"getMessage\",\"chat_id\":" + replyChatId + ",\"message_id\":" + replyMsgId + "}");
                        }
                    }
                }

                // Пересланное сообщение — извлекаем имя оригинального отправителя
                var fwdInfo = msg["forward_info"];
                if (fwdInfo != null) {
                    var origin = fwdInfo["origin"];
                    if (origin != null) {
                        string oType = origin["@type"]?.ToString();
                        if (oType == "messageOriginUser") {
                            long oUid = origin["sender_user_id"]?.ToObject<long>() ?? 0;
                            if (_usersDict.ContainsKey(oUid)) {
                                var u = _usersDict[oUid];
                                item.ForwardedFrom = (u["first_name"]?.ToString() + " " + u["last_name"]?.ToString()).Trim();
                            } else {
                                item.ForwardedFrom = "Пользователь";
                            }
                        } else if (oType == "messageOriginHiddenUser") {
                            item.ForwardedFrom = origin["sender_name"]?.ToString() ?? "Скрытый пользователь";
                        } else if (oType == "messageOriginChat") {
                            long oCid = origin["sender_chat_id"]?.ToObject<long>() ?? 0;
                            item.ForwardedFrom = _chatsDict.ContainsKey(oCid)
                                ? _chatsDict[oCid].Title
                                : origin["author_signature"]?.ToString() ?? "Чат";
                        } else if (oType == "messageOriginChannel") {
                            long oCid = origin["chat_id"]?.ToObject<long>() ?? 0;
                            string sig = origin["author_signature"]?.ToString();
                            string chanName = _chatsDict.ContainsKey(oCid) ? _chatsDict[oCid].Title : "Канал";
                            item.ForwardedFrom = string.IsNullOrEmpty(sig) ? chanName : chanName + " (" + sig + ")";
                        }
                    }
                }

                // Реакции
                var reactions = msg["interaction_info"]?["reactions"]?["reactions"] as JArray;
                if (reactions != null && reactions.Count > 0)
                    item.Reactions = BuildReactionsString(reactions);

                // Комментарии к постам канала
                var replyInfo = msg["interaction_info"]?["reply_info"];
                if (replyInfo != null) {
                    int replyCount = replyInfo["reply_count"]?.ToObject<int>() ?? 0;
                    item.ReplyCount = replyCount;
                }

                // Inline-кнопки
                var markup = msg["reply_markup"];
                if (markup != null && markup["@type"]?.ToString() == "replyMarkupInlineKeyboard") {
                    var rows = markup["rows"] as JArray;
                    if (rows != null) {
                        var buttonRows = new System.Collections.ObjectModel.ObservableCollection<InlineButtonRow>();
                        foreach (var row in rows) {
                            var btnRow = new InlineButtonRow();
                            foreach (var btn in row as JArray ?? new JArray()) {
                                string bType = btn["type"]?["@type"]?.ToString() ?? "";
                                btnRow.Buttons.Add(new InlineButton {
                                    Text = btn["text"]?.ToString() ?? "",
                                    CallbackData = bType == "inlineKeyboardButtonTypeCallback"
                                        ? btn["type"]?["data"]?.ToString() : null,
                                    Url = bType == "inlineKeyboardButtonTypeUrl"
                                        ? btn["type"]?["url"]?.ToString() : null,
                                });
                            }
                            if (btnRow.Buttons.Count > 0) buttonRows.Add(btnRow);
                        }
                        item.InlineButtons = buttonRows;
                    }
                }

                if (type == "messagePhoto") {
                    var sizes = content["photo"]?["sizes"] as JArray;
                    if (sizes != null && sizes.Count > 0) {
                        var fileToken = sizes[sizes.Count - 1]["photo"] as JObject;
                        if (fileToken != null) {
                            long pfid = (long)fileToken["id"];
                            item.FullPhotoFileId = pfid;
                            _fileToMsgId[pfid] = msgId;
                            _messagesDict[msgId] = item;
                            string phPath = fileToken["local"]?["path"]?.ToString();
                            if (!string.IsNullOrEmpty(phPath))
                                { var t = UpdateMessagePhoto(msgId, phPath); }
                            else
                                TdJson.SendUtf8(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + pfid + ",\"priority\":10,\"synchronous\":false}");
                        }
                    }
                } else if (type == "messageVideo") {
                    bool isAnim = content["video"]?["is_animation"]?.ToObject<bool>() ?? false;
                    item.IsVideo = !isAnim;
                    item.IsGif = isAnim;
                    if (isAnim) item.Text = "";
                    var videoFile = content["video"]?["video"] as JObject;
                    var thumb = content["video"]?["thumbnail"]?["file"] as JObject;
                    if (videoFile != null) {
                        long vfid = (long)videoFile["id"];
                        _fileToMsgId[vfid] = msgId;
                        _videoFileIds[vfid] = msgId;
                        _messagesDict[msgId] = item;
                        string vPath = videoFile["local"]?["path"]?.ToString();
                        if (!string.IsNullOrEmpty(vPath)) {
                            if (isAnim) item.GifSource = new Uri(vPath);
                            else item.FilePath = vPath;
                        }
                    }
                    if (thumb != null) {
                        long tfid = (long)thumb["id"];
                        string tPath = thumb["local"]?["path"]?.ToString();
                        bool isImgThumb = !string.IsNullOrEmpty(tPath) &&
                            (tPath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                             tPath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                             tPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
                        if (isImgThumb && !isAnim) {
                            _fileToMsgId[tfid] = msgId;
                            _messagesDict[msgId] = item;
                            var t = UpdateMessagePhoto(msgId, tPath);
                        } else if (!isImgThumb && !isAnim) {
                            _fileToMsgId[tfid] = msgId;
                            _messagesDict[msgId] = item;
                            TdJson.SendUtf8(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + tfid + ",\"priority\":10,\"synchronous\":false}");
                        }
                        // Для GIF тумбнейл не нужен — грузим сразу сам файл
                    }
                } else if (type == "messageAnimation") {
                    item.IsGif = true;
                    item.IsVideo = false;
                    var animFile = content["animation"]?["animation"] as JObject;
                    string animCaption = content["caption"]?["text"]?.ToString() ?? "";
                    item.Text = animCaption; // пустой если нет подписи
                    if (animFile != null) {
                        long afid = (long)animFile["id"];
                        _fileToMsgId[afid] = msgId;
                        _videoFileIds[afid] = msgId;
                        _messagesDict[msgId] = item;
                        string aPath = animFile["local"]?["path"]?.ToString();
                        if (!string.IsNullOrEmpty(aPath))
                            item.GifSource = new Uri(aPath);
                        else {
                            item.VideoDownloadProgress = "⏳ 0%";
                            TdJson.SendUtf8(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + afid + ",\"priority\":10,\"synchronous\":false}");
                        }
                    }
                    // Тумбнейл для GIF не нужен — MediaElement покажет сам файл
                } else if (type == "messageSticker") {
                    var sticker = content["sticker"];
                    bool isAnimated = sticker?["is_animated"]?.ToObject<bool>() ?? false;
                    bool isVideo = sticker?["is_video"]?.ToObject<bool>() ?? false;
                    item.IsSticker = true;
                    item.Text = "";
                    _messagesDict[msgId] = item;

                    var stickerFile = sticker?["sticker"] as JObject;
                    string stickerPath = stickerFile?["local"]?["path"]?.ToString() ?? "";
                    // Определяем тип по расширению — .tgs это gzip+lottie (анимированный)
                    bool isTgs = stickerPath.EndsWith(".tgs", StringComparison.OrdinalIgnoreCase);
                    bool isStaticWebp = stickerPath.EndsWith(".webp", StringComparison.OrdinalIgnoreCase);

                    if ((!isAnimated && !isVideo && !isTgs) || isStaticWebp) {
                        // Статичный WebP стикер — декодируем через libwebp
                        if (stickerFile != null) {
                            long sfid = (long)stickerFile["id"];
                            _fileToMsgId[sfid] = msgId;
                            string remoteUid = stickerFile["remote"]?["unique_id"]?.ToString();
                            if (!string.IsNullOrEmpty(remoteUid))
                                _remoteUniqueIdToMsgId[remoteUid] = msgId;
                            if (!string.IsNullOrEmpty(stickerPath))
                                { var t = UpdateMessagePhoto(msgId, stickerPath); }
                            else
                                TdJson.SendUtf8(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + sfid + ",\"priority\":10,\"synchronous\":false}");
                        }
                    } else {
                        // Анимированный (.tgs) или видео стикер — берём thumbnail
                        var thumb = sticker?["thumbnail"];
                        var thumbFile = thumb?["file"] as JObject;
                        if (thumbFile != null) {
                            long tfid = (long)thumbFile["id"];
                            _fileToMsgId[tfid] = msgId;
                            string remoteUid = thumbFile["remote"]?["unique_id"]?.ToString();
                            if (!string.IsNullOrEmpty(remoteUid))
                                _remoteUniqueIdToMsgId[remoteUid] = msgId;
                            string tPath = thumbFile["local"]?["path"]?.ToString();
                            if (!string.IsNullOrEmpty(tPath))
                                { var t = UpdateMessagePhoto(msgId, tPath); }
                            else
                                TdJson.SendUtf8(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + tfid + ",\"priority\":10,\"synchronous\":false}");
                        } else if (stickerFile != null) {
                            // Thumbnail нет — пробуем скачать сам файл и смотрим что придёт
                            long sfid = (long)stickerFile["id"];
                        }
                    }
                } else if (type == "messagePoll") {
                    var poll = content["poll"];
                    if (poll != null) {
                        item.IsPoll = true;
                        item.Text = "";
                        item.PollQuestion = poll["question"]?["text"]?.ToString() ?? poll["question"]?.ToString() ?? "";
                        // Тип опроса
                        bool isAnonymous = poll["is_anonymous"]?.ToObject<bool>() ?? true;
                        bool isQuiz = poll["type"]?["@type"]?.ToString() == "pollTypeQuiz";
                        item.PollType = isQuiz ? "🎯 Викторина" : (isAnonymous ? "📊 Анонимный опрос" : "📊 Опрос");
                        // Варианты ответа
                        int totalVotes = poll["total_voter_count"]?.ToObject<int>() ?? 0;
                        var options = poll["options"] as JArray;
                        item.PollOptions.Clear();
                        if (options != null) {
                            for (int oi = 0; oi < options.Count; oi++) {
                                var opt = options[oi];
                                int votes = opt["voter_count"]?.ToObject<int>() ?? 0;
                                int pct = totalVotes > 0 ? (int)Math.Round(votes * 100.0 / totalVotes) : 0;
                                item.PollOptions.Add(new PollOptionItem {
                                    OptionId  = oi,
                                    MsgId     = msgId,
                                    TextColor = item.TextColor,
                                    Text      = opt["text"]?["text"]?.ToString() ?? opt["text"]?.ToString() ?? "",
                                    VoteCount = votes,
                                    Percent  = pct,
                                    IsChosen = opt["is_chosen"]?.ToObject<bool>() ?? false
                                });
                            }
                        }
                    }
                } else if (type == "messageDocument") {
                    var doc = content["document"];
                    var docFile = doc?["document"] as JObject;
                    string docName = doc?["file_name"]?.ToString() ?? "Файл";
                    long docSize = docFile?["size"]?.ToObject<long>() ?? 0;
                    item.IsDocument = true;
                    item.DocumentName = docName;
                    item.DocumentSize = FormatFileSize(docSize);
                    if (docFile != null) {
                        long dfid = (long)docFile["id"];
                        _fileToMsgId[dfid] = msgId;
                        _messagesDict[msgId] = item;
                        string dPath = docFile["local"]?["path"]?.ToString();
                        if (!string.IsNullOrEmpty(dPath)) {
                            item.FilePath = dPath;
                            item.IsDownloaded = true;
                            item.DownloadStatus = "📂 Открыть";
                        }
                    }
                } else if (type == "messageVoiceNote") {
                    var voiceNote = content["voice_note"];
                    var voiceFile = voiceNote?["voice"] as JObject;
                    int dur = voiceNote?["duration"]?.ToObject<int>() ?? 0;
                    item.IsAudio = true;
                    item.AudioTitle = "🎤 Голосовое";
                    item.AudioDuration = dur > 0 ? FormatCallDuration(dur) : "";
                    item.AudioPlayStatus = "▶";
                    if (voiceFile != null) {
                        long vfid = (long)voiceFile["id"];
                        _fileToMsgId[vfid] = msgId;
                        _messagesDict[msgId] = item;
                        string vPath = voiceFile["local"]?["path"]?.ToString();
                        if (!string.IsNullOrEmpty(vPath)) {
                            item.FilePath = vPath;
                            item.DownloadStatus = "ready";
                        } else {
                            item.AudioPlayStatus = "⏳";
                            TdJson.SendUtf8(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + vfid + ",\"priority\":10,\"synchronous\":false}");
                        }
                    }
                } else if (type == "messageVideoNote") {
                    var videoNote = content["video_note"];
                    var videoFile = videoNote?["video"] as JObject;
                    int vnDur = videoNote?["duration"]?.ToObject<int>() ?? 0;
                    item.IsVideo = true;
                    item.Text = "⏺ " + (vnDur > 0 ? FormatCallDuration(vnDur) : "Видеосообщение");
                    if (videoFile != null) {
                        long vnFid = (long)videoFile["id"];
                        _fileToMsgId[vnFid] = msgId;
                        _videoFileIds[vnFid] = msgId;
                        _messagesDict[msgId] = item;
                        string vnPath = videoFile["local"]?["path"]?.ToString();
                        if (!string.IsNullOrEmpty(vnPath))
                            item.FilePath = vnPath;
                        else
                            TdJson.SendUtf8(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + vnFid + ",\"priority\":10,\"synchronous\":false}");
                    }
                    // Превью (миниатюра)
                    var vnThumb = videoNote?["thumbnail"]?["file"] as JObject;
                    if (vnThumb != null) {
                        string vnTPath = vnThumb["local"]?["path"]?.ToString();
                        if (!string.IsNullOrEmpty(vnTPath)) {
                            try { item.AttachedPhoto = new Windows.UI.Xaml.Media.Imaging.BitmapImage(new Uri(vnTPath)); } catch { }
                        }
                    }
                } else if (type == "messageAudio") {
                    var audio = content["audio"];
                    var audioFile = audio?["audio"] as JObject;
                    string title = audio?["title"]?.ToString() ?? "";
                    string performer = audio?["performer"]?.ToString() ?? "";
                    int dur = audio?["duration"]?.ToObject<int>() ?? 0;
                    item.IsAudio = true;
                    item.AudioTitle = !string.IsNullOrEmpty(performer) ? performer + " — " + title
                                    : !string.IsNullOrEmpty(title) ? title : "Голосовое сообщение";
                    item.AudioDuration = dur > 0 ? FormatCallDuration(dur) : "";
                    item.AudioPlayStatus = "▶";
                    if (audioFile != null) {
                        long afid = (long)audioFile["id"];
                        _fileToMsgId[afid] = msgId;
                        _messagesDict[msgId] = item;
                        string aPath = audioFile["local"]?["path"]?.ToString();
                        if (!string.IsNullOrEmpty(aPath)) {
                            item.FilePath = aPath;
                            item.DownloadStatus = "ready";
                        } else {
                            item.AudioPlayStatus = "⏳";
                            TdJson.SendUtf8(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + afid + ",\"priority\":10,\"synchronous\":false}");
                        }
                    }
                }
                if (string.IsNullOrEmpty(item.Text) && type != "messagePhoto" && type != "messageVideo" && type != "messageAnimation" && type != "messageDocument" && type != "messageAudio" && type != "messageVoiceNote" && type != "messageVideoNote" && type != "messageSticker" && type != "messagePoll") {
                    if (type == "messageCall") {
                        var callContent = content;
                        bool isVideo = callContent["is_video"]?.ToObject<bool>() ?? false;
                        string callEmoji = isVideo ? "📹" : "📞";
                        bool isOutgoing = (bool)msg["is_outgoing"];
                        string direction = isOutgoing ? "Исходящий" : "Входящий";
                        int duration = callContent["duration"]?.ToObject<int>() ?? 0;
                        string discardReason = callContent["discard_reason"]?["@type"]?.ToString() ?? "";
                        string durationStr = duration > 0 ? " · " + FormatCallDuration(duration) : "";
                        if (discardReason == "callDiscardReasonMissed")
                            item.Text = callEmoji + " Пропущенный звонок";
                        else if (discardReason == "callDiscardReasonDeclined")
                            item.Text = callEmoji + " Отклонённый звонок";
                        else
                            item.Text = callEmoji + " " + direction + " звонок" + durationStr;
                    } else if (type == "messageAudio") {
                        string title = content["audio"]?["title"]?.ToString() ?? "";
                        string performer = content["audio"]?["performer"]?.ToString() ?? "";
                        int dur = content["audio"]?["duration"]?.ToObject<int>() ?? 0;
                        string durStr = dur > 0 ? " · " + FormatCallDuration(dur) : "";
                        string label = !string.IsNullOrEmpty(performer) ? performer + " — " + title : title;
                        item.Text = "🎵 " + (string.IsNullOrEmpty(label) ? "Аудио" : label) + durStr;
                    } else if (type == "messagePinMessage") {
                        long pinnedMsgId = content["message_id"]?.ToObject<long>() ?? 0;
                        // Получаем имя отправителя
                        string senderName = "";
                        var pinSenderId = msg["sender_id"];
                        if (pinSenderId?["@type"]?.ToString() == "messageSenderUser") {
                            long uid = pinSenderId["user_id"]?.ToObject<long>() ?? 0;
                            if (_usersDict.ContainsKey(uid)) {
                                var u = _usersDict[uid];
                                senderName = u["first_name"]?.ToString() ?? "";
                            }
                        }
                        item.Text = "📌 " + (string.IsNullOrEmpty(senderName) ? "Пользователь" : senderName) + " закрепил сообщение";
                        // Запрашиваем текст закреплённого чтобы показать его
                        if (pinnedMsgId != 0) {
                            _pinnedTextRequests[pinnedMsgId] = msgId;
                            TdJson.SendUtf8(_client, "{\"@type\":\"getMessage\",\"chat_id\":" + (long)msg["chat_id"] + ",\"message_id\":" + pinnedMsgId + "}");
                        }
                    } else {
                        item.Text = "[" + type.Replace("message", "") + "]";
                    }
                }
                // Всегда регистрируем в словаре — нужно для редактирования и обновлений
                _messagesDict[msgId] = item;
                return item;
            } catch (Exception ex) { Log("ParseMessage ERR: " + ex.Message); return null; }
        }

        private async Task UpdateAvatarSearchResult(SearchResultItem item, string path) {
            try {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
                var bmp = new BitmapImage();
                using (var stream = await file.OpenReadAsync())
                    await bmp.SetSourceAsync(stream);
                item.Photo = bmp;
            } catch { }
        }

        private async Task UpdateAvatar(long chatId, string path) {
            try {
                var file = await StorageFile.GetFileFromPathAsync(path);
                var bitmap = new BitmapImage();
                using (var stream = await file.OpenReadAsync())
                    await bitmap.SetSourceAsync(stream);
                if (_chatsDict.ContainsKey(chatId)) {
                    _chatsDict[chatId].Photo = bitmap;
                }
                // Если этот чат открыт — обновляем аватарку в шапке
                if (chatId == _currentChatId) {
                    ChatHeaderAvatarBrush.ImageSource = bitmap;
                    ChatHeaderAvatarEllipse.Visibility = Windows.UI.Xaml.Visibility.Visible;
                }
            } catch (Exception ex) { Log("UpdateAvatar ERR chat=" + chatId + " | " + ex.Message); }
        }

        private async Task UpdateMessagePhoto(long msgId, string path) {
            try {
                // .tgs это gzip+lottie — не можем отобразить, пропускаем
                if (path.EndsWith(".tgs", StringComparison.OrdinalIgnoreCase)) {
                    return;
                }
                var file = await StorageFile.GetFileFromPathAsync(path);
                Windows.UI.Xaml.Media.ImageSource bitmap;

                if (path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)) {
                    // WebP — декодируем через libwebp
                    byte[] data;
                    using (var stream = await file.OpenReadAsync())
                    using (var reader = new Windows.Storage.Streams.DataReader(stream)) {
                        await reader.LoadAsync((uint)stream.Size);
                        data = new byte[stream.Size];
                        reader.ReadBytes(data);
                    }
                    bitmap = await WebPDecoder.DecodeAsync(data);
                } else {
                    // Обычное изображение
                    var bmp = new BitmapImage();
                    using (var stream = await file.OpenReadAsync())
                        await bmp.SetSourceAsync(stream);
                    bitmap = bmp;
                }

                if (bitmap != null && _messagesDict.ContainsKey(msgId)) {
                    _messagesDict[msgId].AttachedPhoto = bitmap;
                } else {
                }
            } catch (Exception ex) { Log("UpdateMsgPhoto ERR msg=" + msgId + " | " + ex.Message); }
        }

        

        private void ChatListView_ItemClick(object sender, ItemClickEventArgs e) {
            var chat = (ChatItem)e.ClickedItem;
            if (chat.Id == _currentChatId && _threadMessageId == 0) return;
            _threadMessageId = 0;
            _threadChatId = 0;
            // Очищаем поиск
            if (!string.IsNullOrEmpty(_searchQuery)) {
                SearchBox.Text = "";
                _searchQuery = "";
                SearchClearButton.Visibility = Visibility.Collapsed;
                SearchResultsView.Visibility = Visibility.Collapsed;
                ChatListView.Visibility = Visibility.Visible;
                        if (SearchPanel != null) SearchPanel.Visibility = Visibility.Visible;
            }
            if (_chatsDict.ContainsKey(chat.Id))
                OpenChat(_chatsDict[chat.Id], 0);
        }

        // Открыть чат по ID (используется при возврате из треда)
        private void OpenChatById(long chatId) {
            if (!_chatsDict.ContainsKey(chatId)) return;
            var chat = _chatsDict[chatId];
            // Эмулируем клик по чату
            var fakeItem = new ChatItem { Id = chatId, Title = chat.Title,
                Photo = chat.Photo, IsChannel = chat.IsChannel, OutboxReadId = chat.OutboxReadId };
            OpenChat(fakeItem, 0);
        }

        // Открыть тред комментариев поста
        private void PollOption_Click(object sender, RoutedEventArgs e) {
            var btn = sender as Windows.UI.Xaml.Controls.Button;
            var opt = btn?.Tag as PollOptionItem;
            if (opt == null) return;
            string req = "{\"@type\":\"setPollAnswer\",\"chat_id\":" + _currentChatId +
                         ",\"message_id\":" + opt.MsgId +
                         ",\"option_ids\":[" + opt.OptionId + "]}";
            TdJson.SendUtf8(_client, req);
            // После небольшой задержки перезагружаем сообщение
            long msgId = opt.MsgId;
            var timer = new Windows.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            timer.Tick += (s2, e2) => {
                timer.Stop();
                TdJson.SendUtf8(_client, "{\"@type\":\"getMessage\",\"chat_id\":" + _currentChatId + ",\"message_id\":" + msgId + "}");
            };
            timer.Start();
        }

        private void CommentsButton_Click(object sender, RoutedEventArgs e) {
            var btn = sender as Windows.UI.Xaml.Controls.Button;
            if (btn == null) return;
            long msgId = (long)btn.Tag;
            _threadChatId = _currentChatId;
            _threadMessageId = msgId;
            TdJson.SendUtf8(_client, "{\"@type\":\"getMessageThread\",\"chat_id\":" + _currentChatId + ",\"message_id\":" + msgId + "}");
        }

        // Открыть чат с опциональным thread_id
        private void OpenChat(ChatItem chat, long threadId) {
            if (_currentChatId != 0)
                TdJson.SendUtf8(_client, "{\"@type\":\"closeChat\",\"chat_id\":" + _currentChatId + "}");
            _currentChatId = chat.Id;
            _currentChatIsGroup = _chatsDict.ContainsKey(chat.Id) &&
                !_chatsDict[chat.Id].IsChannel && chat.Id < 0;
            _pendingHistoryChatId = chat.Id;
            _historyRetryCount = 0;
            _loadingOlderHistory = false;

            _hasMoreHistory = true;



            _trimming = false;
            _outOfMemory = false;
            _autoScrolling = false;
            _scrollTimer?.Stop();
            _restoreTimer?.Stop();
            if (MemoryWarningBanner != null)
                MemoryWarningBanner.Visibility = Visibility.Collapsed;
            if (OlderLoadingIndicator != null) {
                OlderLoadingIndicator.Visibility = Visibility.Collapsed;
                OlderProgressRing.IsActive = false;
            }
            if (ScrollToBottomButton != null)
                ScrollToBottomButton.Visibility = Visibility.Collapsed;
            _currentChatOutboxReadId = chat.OutboxReadId;
            // Сохраняем последнее прочитанное входящее — для разделителя "Новые сообщения"
            _lastReadInboxMsgId = 0;
            if (_chatsDict.ContainsKey(chat.Id)) {
                var rawChat = _rawChatsDict.ContainsKey(chat.Id) ? _rawChatsDict[chat.Id] : null;
                if (rawChat != null)
                    _lastReadInboxMsgId = rawChat["last_read_inbox_message_id"]?.ToObject<long>() ?? 0;
            }
            _messageItems.Clear();
            _messagesDict.Clear();
            _fileToMsgId.Clear();
            _videoFileIds.Clear();
            _replyRequests.Clear();
            _remoteUniqueIdToMsgId.Clear();
            _editingMessageId = 0;
            _replyToMessageId = 0;
            ReplyPreviewPanel.Visibility = Visibility.Collapsed;
            ReplyPreviewText.Text = "";
            _fullPhotoMsgId = 0;
            PhotoOverlay.Visibility = Visibility.Collapsed;
            PhotoOverlayImage.Source = null;
            MessageInput.Text = "";
            SendButton.Content = "➤";
            StartPanel.Visibility = Visibility.Collapsed;
            MessagesPanel.Visibility = Visibility.Visible;
            // Заголовок — если тред, показываем "Комментарии"
            CurrentChatTitle.Text = threadId != 0 ? "Комментарии" : chat.Title;
            if (threadId != 0) {
                CurrentChatStatus.Text = "← " + chat.Title;
                CurrentChatStatus.Foreground = CB(_isLightTheme ? "#000000" : "#CCE8FF");
            } else if (_usersDict.ContainsKey(chat.Id)) {
                UpdateChatStatus(_usersDict[chat.Id]["status"]);
            } else if (chat.IsChannel) {
                CurrentChatStatus.Text = "Канал";
                CurrentChatStatus.Foreground = CB(_isLightTheme ? "#000000" : "#CCE8FF");
            } else {
                CurrentChatStatus.Text = "";
                // Запрашиваем пользователя — статус появится когда придёт updateUser
                TdJson.SendUtf8(_client, "{\"@type\":\"getUser\",\"user_id\":" + chat.Id + "}");
            }
            InputBorder.Visibility = (chat.IsChannel && threadId == 0) ? Visibility.Collapsed : Visibility.Visible;
            // Проверяем бот ли это
            _currentChatIsBot = false;
            StartBotButton.Visibility = Visibility.Collapsed;
            if (_rawChatsDict.ContainsKey(chat.Id)) {
                var rawC = _rawChatsDict[chat.Id] as Newtonsoft.Json.Linq.JObject;
                long botUserId = rawC?["type"]?["user_id"]?.ToObject<long>() ?? 0;
                if (botUserId != 0 && _usersDict.ContainsKey(botUserId)) {
                    string utype = _usersDict[botUserId]["type"]?["@type"]?.ToString() ?? "";
                    _currentChatIsBot = utype == "userTypeBot";
                }
            }
            // Аватарка
            if (chat.Photo != null) ChatHeaderAvatarBrush.ImageSource = chat.Photo;
            else ChatHeaderAvatarBrush.ImageSource = null;
            ChatHeaderAvatarEllipse.Visibility = chat.Photo != null ? Visibility.Visible : Visibility.Collapsed;
            // Закреплённое сообщение — запрашиваем напрямую
            PinnedMessageBar.Visibility = Visibility.Collapsed;
            PinnedMessageText.Text = "";
            _pinnedMessageId = -1; // -1 = ждём ответ getChatPinnedMessage
            TdJson.SendUtf8(_client, "{\"@type\":\"getChatPinnedMessage\",\"chat_id\":" + chat.Id + "}");
            _isLoadingHistory = true;
            LoadingIndicator.Visibility = Visibility.Visible;
            MessagesListView.Visibility = Visibility.Collapsed;
            TdJson.SendUtf8(_client, "{\"@type\":\"openChat\",\"chat_id\":" + _currentChatId + "}");
            string histReq = threadId != 0
                ? "{\"@type\":\"getMessageThreadHistory\",\"chat_id\":" + _currentChatId + ",\"message_id\":" + threadId + ",\"from_message_id\":0,\"offset\":0,\"limit\":50}"
                : "{\"@type\":\"getChatHistory\",\"chat_id\":" + _currentChatId + ",\"from_message_id\":0,\"offset\":0,\"limit\":50}";
            TdJson.SendUtf8(_client, histReq);
        }

        private void ForwardMessage_Click(object sender, RoutedEventArgs e) {
            if (_pendingContextMsg == null) return;
            // Заполняем список чатов — main + archive
            var allChats = _chatListItems.Concat(_archiveChatItems).ToList();
            ForwardChatList.ItemsSource = allChats;
            ForwardOverlay.Visibility = Visibility.Visible;
        }

        private void ForwardOverlay_Close(object sender, RoutedEventArgs e) {
            ForwardOverlay.Visibility = Visibility.Collapsed;
        }

        private void ForwardChatList_ItemClick(object sender, ItemClickEventArgs e) {
            var targetChat = e.ClickedItem as ChatItem;
            if (targetChat == null || _pendingContextMsg == null) return;
            ForwardOverlay.Visibility = Visibility.Collapsed;

            long fromChatId = _currentChatId;
            long msgId = _pendingContextMsg.Id;
            _pendingContextMsg = null;

            // forwardMessages с send_copy=false — сохраняет оригинального отправителя в заголовке
            var req = new JObject {
                ["@type"] = "forwardMessages",
                ["chat_id"] = targetChat.Id,
                ["from_chat_id"] = fromChatId,
                ["message_ids"] = new JArray { msgId },
                ["send_copy"] = false,
                ["remove_caption"] = false
            };
            TdJson.SendUtf8(_client, req.ToString(Newtonsoft.Json.Formatting.None));
        }

        private void React_Click(object sender, RoutedEventArgs e) {
            var item = sender as MenuFlyoutItem;
            if (item == null || _selectedMessageForCopy == null) return;
            string emoji = item.Tag?.ToString() ?? "";
            if (string.IsNullOrEmpty(emoji)) return;
            bool alreadyReacted = _selectedMessageForCopy.Reactions != null &&
                                  _selectedMessageForCopy.Reactions.Contains(emoji);
            string req = "{\"@type\":\"" + (alreadyReacted ? "removeMessageReaction" : "addMessageReaction") + "\"" +
                ",\"chat_id\":" + _currentChatId +
                ",\"message_id\":" + _selectedMessageForCopy.Id +
                ",\"reaction_type\":{\"@type\":\"reactionTypeEmoji\",\"emoji\":\"" + emoji + "\"}" +
                (alreadyReacted ? "" : ",\"is_big\":false") + "}";
            TdJson.SendUtf8(_client, req);
        }

        private void ReplyMessage_Click(object sender, RoutedEventArgs e) {
            var msg = _pendingContextMsg;
            if (msg == null) return;
            _replyToMessageId = msg.Id;
            // Текст превью — первые 80 символов
            string preview = string.IsNullOrEmpty(msg.Text) ? "(медиа)" : msg.Text;
            if (preview.Length > 80) preview = preview.Substring(0, 80) + "…";
            ReplyPreviewText.Text = preview;
            ReplyPreviewPanel.Visibility = Visibility.Visible;
            MessageInput.Focus(FocusState.Programmatic);
        }

        private void CancelReply_Click(object sender, RoutedEventArgs e) {
            _replyToMessageId = 0;
            ReplyPreviewPanel.Visibility = Visibility.Collapsed;
            ReplyPreviewText.Text = "";
        }

        private void SendMessage_Click(object sender, RoutedEventArgs e) {
            if (string.IsNullOrWhiteSpace(MessageInput.Text)) return;
            string text = MessageInput.Text;
            MessageInput.Text = "";

            // Режим редактирования
            if (_editingMessageId != 0) {
                long editId = _editingMessageId;
                _editingMessageId = 0;
                SendButton.Content = "➤";
                JObject req = new JObject {
                    ["@type"] = "editMessageText",
                    ["chat_id"] = _currentChatId,
                    ["message_id"] = editId,
                    ["input_message_content"] = new JObject {
                        ["@type"] = "inputMessageText",
                        ["text"] = new JObject { ["@type"] = "formattedText", ["text"] = text }
                    }
                };
                TdJson.SendUtf8(_client, req.ToString(Newtonsoft.Json.Formatting.None));
                // Обновляем UI сразу — не ждём updateMessageEdited (он не содержит нового текста)
                if (_messagesDict.ContainsKey(editId))
                    _messagesDict[editId].Text = text;
                return;
            }

            JObject sendReq = new JObject {
                ["@type"] = "sendMessage",
                ["chat_id"] = _currentChatId,
                ["input_message_content"] = new JObject {
                    ["@type"] = "inputMessageText",
                    ["text"] = new JObject { ["@type"] = "formattedText", ["text"] = text }
                }
            };
            // Если открыт тред комментариев — передаём message_thread_id
            if (_threadMessageId != 0)
                sendReq["message_thread_id"] = _threadMessageId;
            if (_replyToMessageId != 0) {
                sendReq["reply_to"] = new JObject {
                    ["@type"] = "inputMessageReplyToMessage",
                    ["message_id"] = _replyToMessageId
                };
                _replyToMessageId = 0;
                ReplyPreviewPanel.Visibility = Visibility.Collapsed;
                ReplyPreviewText.Text = "";
            }
            TdJson.SendUtf8(_client, sendReq.ToString(Newtonsoft.Json.Formatting.None));
        }

        private void SubscribeRichText(Windows.UI.Xaml.Controls.RichTextBlock rtb, MessageItem item) {
            BuildRichText(rtb, item);
            item.PropertyChanged += async (s, e2) => {
                if (e2.PropertyName == "Text")
                    await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal,
                        () => BuildRichText(rtb, rtb.DataContext as MessageItem ?? item));
            };
        }

        private void MsgRichText_DataContextChanged(Windows.UI.Xaml.FrameworkElement sender, Windows.UI.Xaml.DataContextChangedEventArgs args) {
            var rtb = sender as Windows.UI.Xaml.Controls.RichTextBlock;
            if (rtb == null) return;
            var item = rtb.DataContext as MessageItem;
            if (item == null) return;
            SubscribeRichText(rtb, item);
        }

        private void MsgRichText_Loaded(object sender, RoutedEventArgs e) {
            var rtb = sender as Windows.UI.Xaml.Controls.RichTextBlock;
            if (rtb == null) return;
            var item = rtb.DataContext as MessageItem;
            if (item == null) return;
            SubscribeRichText(rtb, item);
        }

        private void BuildRichText(Windows.UI.Xaml.Controls.RichTextBlock rtb, MessageItem item) {
            rtb.Blocks.Clear();
            var para = new Windows.UI.Xaml.Documents.Paragraph();
            string text = item.Text ?? "";
            Windows.UI.Color linkColor;
            if (_isLightTheme) {
                // Светлая тема: синий на зелёном и белом фоне
                linkColor = Windows.UI.Color.FromArgb(255, 33, 150, 243); // #2196F3
            } else {
                // Тёмная тема: исходящие — светло-жёлтый, входящие — голубой
                linkColor = item.IsOutgoing
                    ? Windows.UI.Color.FromArgb(255, 255, 229, 127)  // #FFE57F
                    : Windows.UI.Color.FromArgb(255, 100, 200, 255); // #64C8FF
            }

            if (item.Entities == null || item.Entities.Count == 0) {
                para.Inlines.Add(new Windows.UI.Xaml.Documents.Run { Text = text });
            } else {
                int pos = 0;
                var sorted = item.Entities.OrderBy(x => x.Offset).ToList();
                foreach (var ent in sorted) {
                    int offset = ent.Offset, length = ent.Length;
                    string url = ent.Url;
                    if (offset > pos)
                        para.Inlines.Add(new Windows.UI.Xaml.Documents.Run { Text = text.Substring(pos, offset - pos) });
                    int safeLen = Math.Min(length, text.Length - offset);
                    if (safeLen > 0 && offset < text.Length) {
                        string linkText = text.Substring(offset, safeLen);
                        try {
                            var hl = new Windows.UI.Xaml.Documents.Hyperlink {
                                NavigateUri = new Uri(url.StartsWith("http") ? url : "https://" + url),
                                Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(linkColor)
                            };
                            hl.Inlines.Add(new Windows.UI.Xaml.Documents.Run { Text = linkText });
                            para.Inlines.Add(hl);
                        } catch {
                            para.Inlines.Add(new Windows.UI.Xaml.Documents.Run { Text = linkText });
                        }
                    }
                    pos = offset + safeLen;
                }
                if (pos < text.Length)
                    para.Inlines.Add(new Windows.UI.Xaml.Documents.Run { Text = text.Substring(pos) });
            }
            rtb.Blocks.Add(para);
        }

        private async void PhotoImage_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e) {
            e.Handled = true;
            var img = sender as Image;
            var item = img?.DataContext as MessageItem;
            if (item == null || item.IsVideo) return;

            // Показываем оверлей сразу с превью
            PhotoOverlay.Visibility = Visibility.Visible;
            PhotoOverlayImage.Source = item.AttachedPhoto;
            PhotoOverlayStatus.Text = "Загрузка полного размера...";

            if (item.FullPhotoFileId == 0) { PhotoOverlayStatus.Text = ""; return; }

            // Запрашиваем полноразмерный файл
            _fullPhotoMsgId = item.Id;
            TdJson.SendUtf8(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + item.FullPhotoFileId + ",\"priority\":32,\"synchronous\":false}");
        }

        private void PhotoOverlay_Tapped(object sender, RoutedEventArgs e) {
            PhotoOverlay.Visibility = Visibility.Collapsed;
            PhotoOverlayImage.Source = null;
            _fullPhotoMsgId = 0;
        }

        private async Task ShowFullPhoto(string path) {
            try {
                var file = await StorageFile.GetFileFromPathAsync(path);
                using (var stream = await file.OpenReadAsync()) {
                    var bitmap = new Windows.UI.Xaml.Media.Imaging.BitmapImage();
                    await bitmap.SetSourceAsync(stream);
                    PhotoOverlayImage.Source = bitmap;
                    PhotoOverlayStatus.Text = "";
                }
            } catch (Exception ex) { Log("FULLPHOTO ERR: " + ex.Message); }
        }

        private async void MessagesListView_ItemClick(object sender, ItemClickEventArgs e) {
            var item = e.ClickedItem as MessageItem;
            if (item == null || !item.IsVideo) return;
            if (string.IsNullOrEmpty(item.FilePath)) {
                foreach (var kv in _videoFileIds)
                    if (kv.Value == item.Id) {
                        TdJson.SendUtf8(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + kv.Key + ",\"priority\":32,\"synchronous\":false}");
                        break;
                    }
                return;
            }
            try {
                var file = await StorageFile.GetFileFromPathAsync(item.FilePath);
                await Windows.System.Launcher.LaunchFileAsync(file);
            } catch (Exception ex) { Log("VIDEO ERR: " + ex.Message); }
        }

        private async void DocumentButton_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e) {
            var btn = sender as Windows.UI.Xaml.Controls.Button;
            if (btn?.Tag == null) return;
            long msgId = (long)btn.Tag;
            if (!_messagesDict.ContainsKey(msgId)) return;
            var item = _messagesDict[msgId];
            if (item.IsDownloaded && !string.IsNullOrEmpty(item.FilePath)) {
                try {
                    var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(item.FilePath);
                    await Windows.System.Launcher.LaunchFileAsync(file);
                } catch (Exception ex) { Log("DOC open ERR: " + ex.Message); }
            } else {
                // Запускаем скачивание — ищем file_id по msgId
                foreach (var kv in _fileToMsgId) {
                    if (kv.Value == msgId) {
                        item.DownloadStatus = "⏳ Загрузка...";
                        TdJson.SendUtf8(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + kv.Key + ",\"priority\":10,\"synchronous\":false}");
                        break;
                    }
                }
            }
        }

        private string BuildReactionsString(JArray reactions) {
            var parts = new System.Text.StringBuilder();
            foreach (var r in reactions) {
                string emoji = r["type"]?["emoji"]?.ToString() ?? "👍";
                int count = r["total_count"]?.ToObject<int>() ?? 0;
                if (count > 0) {
                    if (parts.Length > 0) parts.Append("  ");
                    parts.Append(emoji);
                    if (count > 1) parts.Append(" " + count);
                }
            }
            return parts.ToString();
        }

        private async void AttachFile_Click(object sender, RoutedEventArgs e) {
            if (_currentChatId == 0) return;
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary;
            picker.FileTypeFilter.Add("*");
            var file = await picker.PickSingleFileAsync();
            if (file == null) return;
            var copy = await file.CopyAsync(_filesFolder, file.Name, Windows.Storage.NameCollisionOption.ReplaceExisting);
            string path = copy.Path.Replace("\\", "/");
            string ext = file.FileType?.ToLower() ?? "";
            bool isPhoto = ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".webp" || ext == ".bmp";
            string req;
            if (isPhoto) {
                // Отправляем как фото
                req = "{\"@type\":\"sendMessage\",\"chat_id\":" + _currentChatId +
                    ",\"input_message_content\":{\"@type\":\"inputMessagePhoto\"" +
                    ",\"photo\":{\"@type\":\"inputPhoto\"" +
                    ",\"photo\":{\"@type\":\"inputFileLocal\",\"path\":\"" + path.Replace("\"","\\\"") + "\"}}" +
                    ",\"caption\":{\"@type\":\"formattedText\",\"text\":\"\"}}}";
            } else {
                // Отправляем как документ
                req = "{\"@type\":\"sendMessage\",\"chat_id\":" + _currentChatId +
                    ",\"input_message_content\":{\"@type\":\"inputMessageDocument\"" +
                    ",\"document\":{\"@type\":\"inputDocument\"" +
                    ",\"document\":{\"@type\":\"inputFileLocal\",\"path\":\"" + path.Replace("\"","\\\"") + "\"}" +
                    ",\"disable_content_type_detection\":false}" +
                    ",\"caption\":{\"@type\":\"formattedText\",\"text\":\"\"}}}";
            }
            TdJson.SendUtf8(_client, req);
        }

        private bool _pendingOpenChat = false; // ждём chat после searchPublicChat/createPrivateChat для открытия

        private async void OpenMention_Click(object sender, RoutedEventArgs e) {
            var mentions = _selectedMessageForCopy?.Entities?.Where(en => en.Mention != null).ToList();
            if (mentions == null || mentions.Count == 0) return;
            string mention = mentions[0].Mention;
            _pendingOpenChat = true;
            if (mention.StartsWith("@id")) {
                long uid = 0;
                long.TryParse(mention.Substring(3), out uid);
                if (uid != 0)
                    TdJson.SendUtf8(_client, "{\"@type\":\"createPrivateChat\",\"user_id\":" + uid + ",\"force\":true}");
                else _pendingOpenChat = false;
            } else {
                string username = mention.TrimStart('@');
                TdJson.SendUtf8(_client, "{\"@type\":\"searchPublicChat\",\"username\":\"" + username + "\"}");
            }
        }

        private void PinMessage_Click(object sender, RoutedEventArgs e) {
            if (_selectedMessageForCopy == null || _currentChatId == 0) return;
            bool isPinned = _selectedMessageForCopy.Id == _pinnedMessageId && _pinnedMessageId != 0;
            if (isPinned) {
                // Открепляем
                TdJson.SendUtf8(_client, "{\"@type\":\"unpinChatMessage\",\"chat_id\":" + _currentChatId +
                    ",\"message_id\":" + _selectedMessageForCopy.Id + "}");
                _pinnedMessageId = 0;
                PinnedMessageBar.Visibility = Visibility.Collapsed;
                PinnedMessageText.Text = "";
            } else {
                // Закрепляем
                TdJson.SendUtf8(_client, "{\"@type\":\"pinChatMessage\",\"chat_id\":" + _currentChatId +
                    ",\"message_id\":" + _selectedMessageForCopy.Id +
                    ",\"disable_notification\":false,\"only_for_self\":false}");
                _pinnedMessageId = _selectedMessageForCopy.Id;
                // Обновляем текст полоски
                string pinText = !string.IsNullOrEmpty(_selectedMessageForCopy.Text)
                    ? _selectedMessageForCopy.Text
                    : "Сообщение";
                PinnedMessageText.Text = pinText;
                PinnedMessageBar.Visibility = Visibility.Visible;
            }
        }

        private void PinnedMessage_Click(object sender, RoutedEventArgs e) {
            if (_pinnedMessageId <= 0) return;
            var pinned = _messageItems.FirstOrDefault(m => !m.IsSeparator && m.Id == _pinnedMessageId);
            if (pinned != null) {
                // Вычисляем позицию через индекс и среднюю высоту
                int idx = _messageItems.IndexOf(pinned);
                double sh = MessagesScrollViewer.ScrollableHeight;
                double itemH = sh / Math.Max(_messageItems.Count, 1);
                double target = Math.Max(0, idx * itemH - 60);
                MessagesScrollViewer.ChangeView(null, target, null, false);
            } else {
                // Сообщение не загружено — запрашиваем историю вокруг него
                _pendingScrollToMsgId = _pinnedMessageId;
                TdJson.SendUtf8(_client, "{\"@type\":\"getChatHistory\",\"chat_id\":" + _currentChatId +
                    ",\"from_message_id\":" + _pinnedMessageId + ",\"offset\":-10,\"limit\":20}");
            }
        }

        private void ChatHeaderProfile_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e) {
            if (_currentChatId == 0) return;
            // Показываем профиль
            ProfileOverlay.Visibility = Visibility.Visible;
            ProfileName.Text = CurrentChatTitle.Text;
            ProfileUsername.Visibility = Visibility.Collapsed;
            ProfilePhonePanel.Visibility = Visibility.Collapsed;
            ProfileBioPanel.Visibility = Visibility.Collapsed;
            // Аватарка
            ProfileAvatarBrush.ImageSource = ChatHeaderAvatarBrush.ImageSource;
            // Берём данные из rawChatsDict
            if (_rawChatsDict.ContainsKey(_currentChatId)) {
                var raw = _rawChatsDict[_currentChatId] as Newtonsoft.Json.Linq.JObject;
                long userId = raw?["type"]?["user_id"]?.ToObject<long>() ?? 0;
                if (userId != 0 && _usersDict.ContainsKey(userId)) {
                    var u = _usersDict[userId];
                    // Username
                    string uname = u["username"]?.ToString()
                                ?? u["usernames"]?["editable_username"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(uname)) {
                        ProfileUsername.Text = "@" + uname;
                        ProfileUsername.Visibility = Visibility.Visible;
                    }
                    // Телефон
                    string phone = u["phone_number"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(phone)) {
                        ProfilePhone.Text = "+" + phone;
                        ProfilePhonePanel.Visibility = Visibility.Visible;
                    }
                    // Запрашиваем bio через getUserFullInfo
                    TdJson.SendUtf8(_client, "{\"@type\":\"getUserFullInfo\",\"user_id\":" + userId + "}");
                } else {
                    // Группа/канал — запрашиваем getSupergroupFullInfo
                    long sgId = raw?["type"]?["supergroup_id"]?.ToObject<long>() ?? 0;
                    if (sgId != 0)
                        TdJson.SendUtf8(_client, "{\"@type\":\"getSupergroupFullInfo\",\"supergroup_id\":" + sgId + "}");
                }
            }
        }

        private void ProfileOverlay_Close(object sender, RoutedEventArgs e) {
            ProfileOverlay.Visibility = Visibility.Collapsed;
        }

        private void ProfileOverlay_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e) {
            // Закрываем при клике на фон
            if (e.OriginalSource == ProfileOverlay)
                ProfileOverlay.Visibility = Visibility.Collapsed;
        }

        private void StartBotButton_Click(object sender, RoutedEventArgs e) {
            if (_currentChatId == 0) return;
            StartBotButton.Visibility = Visibility.Collapsed;
            // Отправляем /start
            string req = "{\"@type\":\"sendMessage\",\"chat_id\":" + _currentChatId +
                ",\"input_message_content\":{\"@type\":\"inputMessageText\"" +
                ",\"text\":{\"@type\":\"formattedText\",\"text\":\"/start\"}}}";
            TdJson.SendUtf8(_client, req);
        }

        private void AudioSlider_ManipulationStarted(object sender, Windows.UI.Xaml.Input.ManipulationStartedRoutedEventArgs e) {
            _audioSliderDragging = true;
        }
        private void AudioSlider_ManipulationCompleted(object sender, Windows.UI.Xaml.Input.ManipulationCompletedRoutedEventArgs e) {
            _audioSliderDragging = false;
            if (_currentAudioPlayer == null) return;
            var slider = sender as Windows.UI.Xaml.Controls.Slider;
            if (slider == null) return;
            _currentAudioPlayer.PlaybackSession.Position = TimeSpan.FromSeconds(slider.Value);
        }

        private async void AudioButton_Click(object sender, RoutedEventArgs e) {
            var btn = sender as Button;
            long msgId = (long)btn.Tag;
            if (!_messagesDict.ContainsKey(msgId)) return;
            var item = _messagesDict[msgId];
            // Если уже играет — стоп
            if (_currentAudioMsgId == msgId && _currentAudioPlayer != null) {
                _currentAudioPlayer.Pause();
                _currentAudioPlayer.Source = null;
                _currentAudioPlayer.SystemMediaTransportControls.PlaybackStatus = Windows.Media.MediaPlaybackStatus.Stopped;
                _currentAudioPlayer = null; // сбрасываем ссылку (не сам плеер — он синглтон)
                _currentAudioSource = null;
                item.AudioPlayStatus = "▶";
                _currentAudioMsgId = 0;
                _currentAudioFilePath = null;
                ReleaseMediaSession();
                return;
            }
            // Остановить предыдущий трек
            if (_currentAudioPlayer != null) {
                _currentAudioPlayer.Pause();
                _currentAudioPlayer.Source = null;
                _currentAudioPlayer.SystemMediaTransportControls.PlaybackStatus = Windows.Media.MediaPlaybackStatus.Stopped;
                if (_messagesDict.ContainsKey(_currentAudioMsgId))
                    _messagesDict[_currentAudioMsgId].AudioPlayStatus = "▶";
                _currentAudioPlayer = null;
                _currentAudioSource = null;
                _currentAudioFilePath = null;
                ReleaseMediaSession();
            }
            if (string.IsNullOrEmpty(item.FilePath)) {
                return;
            }
            try {
                var player = new Windows.Media.Playback.MediaPlayer();
                player.AudioCategory = Windows.Media.Playback.MediaPlayerAudioCategory.Media;
                var source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(item.FilePath));
                _currentAudioSource = source;
                player.Source = source;

                SetupPlayer(player, item, TimeSpan.Zero);

                player.Play();
                AudioPlayerHost.Children.Clear();
                _currentAudioPlayer = player;
                _currentAudioMsgId = msgId;
                item.AudioPlayStatus = "⏹";
                _currentAudioFilePath = item.FilePath;
                _currentAudioPosition = TimeSpan.Zero;
                await RequestMediaSessionAsync();
            } catch (Exception ex) {
            }
        }

        // Настройка SMTC и обработчиков событий плеера. Вызывается и при старте, и при восстановлении после suspend.
        private void SetupPlayer(Windows.Media.Playback.MediaPlayer player, MessageItem item, TimeSpan startPosition) {
            var smtc = player.SystemMediaTransportControls;
            smtc.IsEnabled = true;
            smtc.IsPlayEnabled = true;
            smtc.IsPauseEnabled = true;
            smtc.IsStopEnabled = false;
            smtc.IsNextEnabled = false;
            smtc.IsPreviousEnabled = false;
            smtc.DisplayUpdater.Type = Windows.Media.MediaPlaybackType.Music;
            smtc.DisplayUpdater.MusicProperties.Title = item.AudioTitle ?? "";
            smtc.DisplayUpdater.Update();
            smtc.PlaybackPositionChangeRequested += (ss, ee) => {
                player.PlaybackSession.Position = ee.RequestedPlaybackPosition;
            };
            player.PlaybackSession.PositionChanged += (session, args) => {
                smtc.UpdateTimelineProperties(new Windows.Media.SystemMediaTransportControlsTimelineProperties {
                    StartTime = TimeSpan.Zero, MinSeekTime = TimeSpan.Zero,
                    Position = session.Position,
                    MaxSeekTime = session.NaturalDuration,
                    EndTime = session.NaturalDuration
                });
            };
            player.PlaybackSession.PlaybackStateChanged += (session, args) => {
                var _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                    if (session.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing)
                        item.AudioPlayStatus = "⏹";
                    else if (session.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Paused)
                        item.AudioPlayStatus = "▶";
                });
            };
            player.MediaOpened += (s, ev) => {
                var _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                    if (startPosition > TimeSpan.Zero)
                        player.PlaybackSession.Position = startPosition;
                    var dur = player.PlaybackSession.NaturalDuration;
                    if (dur.TotalSeconds > 0) item.AudioDurationSeconds = dur.TotalSeconds;
                });
            };
            player.MediaEnded += (s, ev) => {
                var _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                    item.AudioPlayStatus = "▶";
                    _currentAudioPlayer = null; _currentAudioSource = null;
                    _currentAudioMsgId = 0; _currentAudioFilePath = null;
                    ReleaseMediaSession();
                });
            };
            player.MediaFailed += (s, ev) => {
                var _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                    item.AudioPlayStatus = "▶";
                    _currentAudioPlayer = null; _currentAudioSource = null;
                    _currentAudioMsgId = 0;
                    // НЕ сбрасываем _currentAudioFilePath — нужен для восстановления в Resuming
                    ReleaseMediaSession();
                });
            };
        }

        private async void MicButton_PointerPressed(object sender, Windows.UI.Xaml.Input.PointerRoutedEventArgs e) {
            if (_currentChatId == 0 || _isRecording) return;
            try {
                _mediaCapture = new Windows.Media.Capture.MediaCapture();
                await _mediaCapture.InitializeAsync(new Windows.Media.Capture.MediaCaptureInitializationSettings {
                    StreamingCaptureMode = Windows.Media.Capture.StreamingCaptureMode.Audio
                });
                if (_filesFolder == null) { Log("MIC ERR _filesFolder is null!"); return; }
                string fname = "voice_" + DateTimeOffset.Now.ToUnixTimeSeconds() + ".m4a";
                _recordingFile = await _filesFolder.CreateFileAsync(fname, Windows.Storage.CreationCollisionOption.ReplaceExisting);
                var profile = Windows.Media.MediaProperties.MediaEncodingProfile.CreateM4a(
                    Windows.Media.MediaProperties.AudioEncodingQuality.Medium);
                await _mediaCapture.StartRecordToStorageFileAsync(profile, _recordingFile);
                _isRecording = true;
                MicButton.Background = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 220, 50, 50));
            } catch (Exception ex) {
                _mediaCapture?.Dispose();
                _mediaCapture = null;
            }
        }

        private async void MicButton_PointerReleased(object sender, Windows.UI.Xaml.Input.PointerRoutedEventArgs e) {
            if (!_isRecording || _mediaCapture == null) return;
            try {
                await _mediaCapture.StopRecordAsync();
                _isRecording = false;
                MicButton.Background = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.Transparent);
                _mediaCapture.Dispose();
                _mediaCapture = null;
                var props = await _recordingFile.Properties.GetMusicPropertiesAsync();
                int durationSec = (int)props.Duration.TotalSeconds;
                string voicePath = _recordingFile.Path.Replace("\\", "/");
                string voiceReq = "{\"@type\":\"sendMessage\",\"chat_id\":" + _currentChatId +
                    ",\"input_message_content\":{\"@type\":\"inputMessageVoiceNote\"" +
                    ",\"voice_note\":{\"@type\":\"inputVoiceNote\"" +
                    ",\"voice_note\":{\"@type\":\"inputFileLocal\",\"path\":\"" + voicePath.Replace("\"","\\\"") + "\"}" +
                    ",\"duration\":" + durationSec +
                    ",\"waveform\":\"\"}" +
                    ",\"caption\":{\"@type\":\"formattedText\",\"text\":\"\"}}}";
                TdJson.SendUtf8(_client, voiceReq);
            } catch (Exception ex) {
                _isRecording = false;
                MicButton.Background = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.Transparent);
            }
        }

        private bool _isRecordingVideoNote = false;

        private async void VideoNoteButton_PointerPressed(object sender, Windows.UI.Xaml.Input.PointerRoutedEventArgs e) {
            if (_currentChatId == 0 || _isRecordingVideoNote) return;
            try {
                _isRecordingVideoNote = true;
                _videoCaptureCapture = new Windows.Media.Capture.MediaCapture();
                await _videoCaptureCapture.InitializeAsync(new Windows.Media.Capture.MediaCaptureInitializationSettings {
                    StreamingCaptureMode = Windows.Media.Capture.StreamingCaptureMode.AudioAndVideo,
                    VideoDeviceId = await GetFrontCameraId()
                });
                // Поворачиваем на 90° для портретной ориентации
                var props = _videoCaptureCapture.VideoDeviceController.GetMediaStreamProperties(
                    Windows.Media.Capture.MediaStreamType.VideoRecord) as Windows.Media.MediaProperties.VideoEncodingProperties;
                if (props != null) {
                    System.Guid rotGuid = new System.Guid("C380465D-2271-428C-9B83-ECEA3B4A85C1");
                    props.Properties.Add(rotGuid, 270);
                    await _videoCaptureCapture.VideoDeviceController.SetMediaStreamPropertiesAsync(
                        Windows.Media.Capture.MediaStreamType.VideoRecord, props);
                }
                VideoNotePreview.Source = _videoCaptureCapture;
                await _videoCaptureCapture.StartPreviewAsync();
                VideoNoteOverlay.Visibility = Visibility.Visible;
                // Создаём файл
                string fname = "vidnote_" + Environment.TickCount + ".mp4";
                _videoNoteFile = await _filesFolder.CreateFileAsync(fname, Windows.Storage.CreationCollisionOption.ReplaceExisting);
                var profile = Windows.Media.MediaProperties.MediaEncodingProfile.CreateMp4(
                    Windows.Media.MediaProperties.VideoEncodingQuality.Auto);
                await _videoCaptureCapture.StartRecordToStorageFileAsync(profile, _videoNoteFile);
                // Таймер
                _videoNoteSeconds = 0;
                VideoNoteTimer.Text = "0:00";
                _videoNoteTimer = new Windows.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _videoNoteTimer.Tick += (ts, te) => {
                    _videoNoteSeconds++;
                    VideoNoteTimer.Text = _videoNoteSeconds / 60 + ":" + (_videoNoteSeconds % 60).ToString("D2");
                    if (_videoNoteSeconds >= MaxVideoNoteSeconds)
                        VideoNoteButton_PointerReleased(null, null);
                };
                _videoNoteTimer.Start();
            } catch (Exception ex) {
                _isRecordingVideoNote = false;
                VideoNoteOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private async void VideoNoteButton_PointerReleased(object sender, Windows.UI.Xaml.Input.PointerRoutedEventArgs e) {
            if (!_isRecordingVideoNote || _videoCaptureCapture == null) return;
            try {
                _videoNoteTimer?.Stop();
                _videoNoteTimer = null;
                await _videoCaptureCapture.StopRecordAsync();
                await _videoCaptureCapture.StopPreviewAsync();
                _isRecordingVideoNote = false;
                VideoNotePreview.Source = null;
                VideoNoteOverlay.Visibility = Visibility.Collapsed;
                _videoCaptureCapture.Dispose();
                _videoCaptureCapture = null;
                if (_videoNoteSeconds < 1) return; // слишком короткое
                string path = _videoNoteFile.Path.Replace("\\", "/");
                string req = "{\"@type\":\"sendMessage\",\"chat_id\":" + _currentChatId +
                    ",\"input_message_content\":{\"@type\":\"inputMessageVideoNote\"" +
                    ",\"video_note\":{\"@type\":\"inputVideoNote\"" +
                    ",\"video_note\":{\"@type\":\"inputFileLocal\",\"path\":\"" + path.Replace("\"","\\\"") + "\"}" +
                    ",\"duration\":" + _videoNoteSeconds +
                    ",\"length\":240}}}";
                TdJson.SendUtf8(_client, req);
            } catch (Exception ex) {
                _isRecordingVideoNote = false;
                VideoNoteOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private async System.Threading.Tasks.Task<string> GetFrontCameraId() {
            var devices = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(
                Windows.Devices.Enumeration.DeviceClass.VideoCapture);
            foreach (var d in devices)
                if (d.EnclosureLocation?.Panel == Windows.Devices.Enumeration.Panel.Front)
                    return d.Id;
            return devices.Count > 0 ? devices[0].Id : "";
        }

        private void ChatItem_Holding(object sender, Windows.UI.Xaml.Input.HoldingRoutedEventArgs e) {
            if (e.HoldingState != Windows.UI.Input.HoldingState.Started) return;
            var grid = sender as Grid;
            if (grid == null) return;
            var chat = grid.DataContext as ChatItem;
            if (chat == null) return;
            _pendingDeleteChatId = chat.Id;
            // Меняем текст пунктов меню по состоянию чата
            var flyout = FlyoutBase.GetAttachedFlyout(grid) as MenuFlyout;
            if (flyout != null) {
                bool isInArchive = _archiveChatIds.Contains(chat.Id);
                bool isPinned = chat.IsPinned;
                foreach (var fi in flyout.Items.OfType<MenuFlyoutItem>()) {
                    if (fi.Name == "MenuArchiveChat")
                        fi.Text = isInArchive ? "📤 Переместить из архива" : "📁 Переместить в архив";
                    if (fi.Name == "MenuPinChat")
                        fi.Text = isPinned ? "📌 Открепить" : "📌 Закрепить";
                    if (fi.Name == "MenuMarkUnread")
                        fi.Text = chat.IsMarkedUnread ? "✅ Отметить прочитанным" : "🔵 Отметить непрочитанным";
                }
            }
            Windows.UI.Xaml.Controls.Primitives.FlyoutBase.ShowAttachedFlyout(grid);
        }

        private void MarkUnread_Click(object sender, RoutedEventArgs e) {
            if (_pendingDeleteChatId == 0) return;
            long chatId = _pendingDeleteChatId;
            _pendingDeleteChatId = 0;
            if (!_chatsDict.ContainsKey(chatId)) return;
            bool newMarked = !_chatsDict[chatId].IsMarkedUnread;
            var req = "{\"@type\":\"toggleChatIsMarkedAsUnread\",\"chat_id\":" + chatId + ",\"is_marked_as_unread\":" + (newMarked ? "true" : "false") + "}";
            TdJson.SendUtf8(_client, req);
        }

        private void PinChat_Click(object sender, RoutedEventArgs e) {
            if (_pendingDeleteChatId == 0) return;
            long chatId = _pendingDeleteChatId;
            _pendingDeleteChatId = 0;
            if (!_chatsDict.ContainsKey(chatId)) return;
            bool newPinned = !_chatsDict[chatId].IsPinned;
            string listType = _archiveChatIds.Contains(chatId) ? "chatListArchive" : "chatListMain";
            var req = new JObject {
                ["@type"] = "toggleChatIsPinned",
                ["chat_list"] = new JObject { ["@type"] = listType },
                ["chat_id"] = chatId,
                ["is_pinned"] = newPinned
            };
            string reqStr = req.ToString(Newtonsoft.Json.Formatting.None);
            TdJson.SendUtf8(_client, reqStr);
        }

        private void ArchiveChat_Click(object sender, RoutedEventArgs e) {
            if (_pendingDeleteChatId == 0) return;
            long chatId = _pendingDeleteChatId;
            _pendingDeleteChatId = 0;
            bool isInArchive = _archiveChatIds.Contains(chatId);
            string targetList = isInArchive ? "chatListMain" : "chatListArchive";
            var req = "{\"@type\":\"addChatToList\",\"chat_id\":" + chatId + ",\"chat_list\":{\"@type\":\"" + targetList + "\"}}";
            TdJson.SendUtf8(_client, req);
        }

        private async void DeleteChat_Click(object sender, RoutedEventArgs e) {
            var item = sender as MenuFlyoutItem;
            // Ищем Tag через визуальное дерево — идём вверх от MenuFlyoutItem
            // Tag был установлен на Grid в ChatItem_Holding
            // Ищем чат через _chatsDict по совпадению с открытым flyout
            // Надёжнее хранить pending id отдельно
            if (_pendingDeleteChatId == 0) return;
            long chatId = _pendingDeleteChatId;
            _pendingDeleteChatId = 0;
            // Показываем диалог подтверждения
            var dialog = new Windows.UI.Popups.MessageDialog("Удалить переписку? Это действие нельзя отменить.", "Удалить переписку");
            dialog.Commands.Add(new Windows.UI.Popups.UICommand("Удалить", async cmd => {
                var req = Newtonsoft.Json.Linq.JObject.FromObject(new {
                    type = "deleteChatHistory",
                    chat_id = chatId,
                    remove_from_chat_list = true,
                    revoke = false
                });
                req["@type"] = req["type"]; req.Remove("type");
                TdJson.SendUtf8(_client, req.ToString(Newtonsoft.Json.Formatting.None));
                // Убираем из списка
                await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                    var toRemove = _chatListItems.FirstOrDefault(c => c.Id == chatId);
                    if (toRemove != null) _chatListItems.Remove(toRemove);
                    _allChatItems.RemoveAll(c => c.Id == chatId);
                    if (_chatsDict.ContainsKey(chatId)) _chatsDict.Remove(chatId);
                    if (_pendingPinnedPositions.ContainsKey(chatId)) _pendingPinnedPositions.Remove(chatId);
                    // Удаляем из папок
                    foreach (var fl in _folderChatIds.Values) fl.Remove(chatId);
                });
            }));
            dialog.Commands.Add(new Windows.UI.Popups.UICommand("Отмена"));
            await dialog.ShowAsync();
        }

        private void ApplySavedProxy() {
            var s = Windows.Storage.ApplicationData.Current.LocalSettings;
            switch (_proxyMode) {
                case ProxyMode.None:
                    TdJson.SendUtf8(_client, "{\"@type\":\"disableProxy\"}");
                    break;
                case ProxyMode.Auto:
                    var t = FetchAndApplyProxyAsync();
                    break;
                case ProxyMode.Mtproto: {
                    string host   = s.Values.ContainsKey("proxy_mtp_host")   ? (string)s.Values["proxy_mtp_host"]   : "";
                    string port   = s.Values.ContainsKey("proxy_mtp_port")   ? (string)s.Values["proxy_mtp_port"]   : "";
                    string secret = s.Values.ContainsKey("proxy_mtp_secret") ? (string)s.Values["proxy_mtp_secret"] : "";
                    if (!string.IsNullOrEmpty(host) && !string.IsNullOrEmpty(port) && !string.IsNullOrEmpty(secret) && int.TryParse(port, out int p)) {
                        var t2 = ApplyProxyAsync(host, p, secret);
                    }
                    break;
                }
                case ProxyMode.Http: {
                    string host = s.Values.ContainsKey("proxy_http_host") ? (string)s.Values["proxy_http_host"] : "";
                    string port = s.Values.ContainsKey("proxy_http_port") ? (string)s.Values["proxy_http_port"] : "";
                    if (!string.IsNullOrEmpty(host) && !string.IsNullOrEmpty(port) && int.TryParse(port, out int p)) {
                        ClearAllProxies();
                        string req = "{\"@type\":\"addProxy\",\"proxy\":{\"@type\":\"proxy\",\"server\":\"" + host +
                                     "\",\"port\":" + p + ",\"type\":{\"@type\":\"proxyTypeHttp\",\"username\":\"\",\"password\":\"\",\"http_only\":false}},\"enable\":true}";
                        TdJson.SendUtf8(_client, req);
                        ProxyStatusText.Text = "[..] " + host + ":" + p;
                        ProxyStatusText.Visibility = Visibility.Visible;
                    }
                    break;
                }
                case ProxyMode.Socks: {
                    string host = s.Values.ContainsKey("proxy_socks_host") ? (string)s.Values["proxy_socks_host"] : "";
                    string port = s.Values.ContainsKey("proxy_socks_port") ? (string)s.Values["proxy_socks_port"] : "";
                    string user = s.Values.ContainsKey("proxy_socks_user") ? (string)s.Values["proxy_socks_user"] : "";
                    string pass = s.Values.ContainsKey("proxy_socks_pass") ? (string)s.Values["proxy_socks_pass"] : "";
                    if (!string.IsNullOrEmpty(host) && !string.IsNullOrEmpty(port) && int.TryParse(port, out int p)) {
                        ClearAllProxies();
                        string req = "{\"@type\":\"addProxy\",\"proxy\":{\"@type\":\"proxy\",\"server\":\"" + host +
                                     "\",\"port\":" + p + ",\"type\":{\"@type\":\"proxyTypeSocks5\",\"username\":\"" + user + "\",\"password\":\"" + pass + "\"}},\"enable\":true}";
                        TdJson.SendUtf8(_client, req);
                        ProxyStatusText.Text = "[..] " + host + ":" + p;
                        ProxyStatusText.Visibility = Visibility.Visible;
                    }
                    break;
                }
            }
        }

        private void SaveProxySettings() {
            try {
                var s = Windows.Storage.ApplicationData.Current.LocalSettings;
                s.Values["proxy_mode"] = (int)_proxyMode;
                s.Values["proxy_mtp_host"]   = MtpHost.Text.Trim();
                s.Values["proxy_mtp_port"]   = MtpPort.Text.Trim();
                s.Values["proxy_mtp_secret"] = MtpSecret.Text.Trim();
                s.Values["proxy_http_host"]  = HttpHost.Text.Trim();
                s.Values["proxy_http_port"]  = HttpPort.Text.Trim();
                s.Values["proxy_socks_host"] = SocksHost.Text.Trim();
                s.Values["proxy_socks_port"] = SocksPort.Text.Trim();
                s.Values["proxy_socks_user"] = SocksUser.Text.Trim();
                s.Values["proxy_socks_pass"] = SocksPass.Password;
            } catch (Exception ex) {
            }
        }

        private void LoadProxySettings() {
            var s = Windows.Storage.ApplicationData.Current.LocalSettings;
            if (s.Values.ContainsKey("proxy_mode"))
                _proxyMode = (ProxyMode)(int)s.Values["proxy_mode"];
        }

        private void LoadProxySettingsToUI() {
            // Вызывается только при открытии попапа — UI элементы гарантированно существуют
            var s = Windows.Storage.ApplicationData.Current.LocalSettings;
            if (s.Values.ContainsKey("proxy_mtp_host"))   MtpHost.Text   = (string)s.Values["proxy_mtp_host"];
            if (s.Values.ContainsKey("proxy_mtp_port"))   MtpPort.Text   = (string)s.Values["proxy_mtp_port"];
            if (s.Values.ContainsKey("proxy_mtp_secret")) MtpSecret.Text = (string)s.Values["proxy_mtp_secret"];
            if (s.Values.ContainsKey("proxy_http_host"))  HttpHost.Text  = (string)s.Values["proxy_http_host"];
            if (s.Values.ContainsKey("proxy_http_port"))  HttpPort.Text  = (string)s.Values["proxy_http_port"];
            if (s.Values.ContainsKey("proxy_socks_host")) SocksHost.Text  = (string)s.Values["proxy_socks_host"];
            if (s.Values.ContainsKey("proxy_socks_port")) SocksPort.Text  = (string)s.Values["proxy_socks_port"];
            if (s.Values.ContainsKey("proxy_socks_user")) SocksUser.Text  = (string)s.Values["proxy_socks_user"];
            if (s.Values.ContainsKey("proxy_socks_pass")) SocksPass.Password = (string)s.Values["proxy_socks_pass"];
        }

        private void ProxySettingsButton_Click(object sender, RoutedEventArgs e) {
            // Загружаем поля из LocalSettings
            LoadProxySettingsToUI();
            // Выставляем текущий режим в UI
            ProxyModeNone.IsChecked     = _proxyMode == ProxyMode.None;
            ProxyModeAuto.IsChecked     = _proxyMode == ProxyMode.Auto;
            ProxyModeMtproto.IsChecked  = _proxyMode == ProxyMode.Mtproto;
            ProxyModeHttp.IsChecked     = _proxyMode == ProxyMode.Http;
            ProxyModeSocks.IsChecked    = _proxyMode == ProxyMode.Socks;
            UpdateProxyFields();
            // Центрируем popup
            ProxyPopup.HorizontalOffset = (ActualWidth - 320) / 2;
            ProxyPopup.VerticalOffset   = (ActualHeight - 400) / 2;
            ProxyPopup.IsOpen = true;
        }

        private void ProxyMode_Checked(object sender, RoutedEventArgs e) {
            UpdateProxyFields();
        }

        private void UpdateProxyFields() {
            if (MtprotoFields == null) return;
            MtprotoFields.Visibility = (ProxyModeMtproto?.IsChecked == true) ? Visibility.Visible : Visibility.Collapsed;
            HttpFields.Visibility    = (ProxyModeHttp?.IsChecked    == true) ? Visibility.Visible : Visibility.Collapsed;
            SocksFields.Visibility   = (ProxyModeSocks?.IsChecked   == true) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ProxyCancel_Click(object sender, RoutedEventArgs e) {
            ProxyPopup.IsOpen = false;
        }

        private void ProxyApply_Click(object sender, RoutedEventArgs e) {
            ProxyPopup.IsOpen = false;
            // Сначала обновляем _proxyMode, потом сохраняем
            if (ProxyModeNone.IsChecked == true)         _proxyMode = ProxyMode.None;
            else if (ProxyModeAuto.IsChecked == true)    _proxyMode = ProxyMode.Auto;
            else if (ProxyModeMtproto.IsChecked == true) _proxyMode = ProxyMode.Mtproto;
            else if (ProxyModeHttp.IsChecked == true)    _proxyMode = ProxyMode.Http;
            else if (ProxyModeSocks.IsChecked == true)   _proxyMode = ProxyMode.Socks;
            SaveProxySettings();

            if (_proxyMode == ProxyMode.None) {
                _proxyApplied = true;
                TdJson.SendUtf8(_client, "{\"@type\":\"disableProxy\"}");
                ProxyStatusText.Text = "Без прокси";
                ProxyStatusText.Visibility = Visibility.Visible;
            } else if (_proxyMode == ProxyMode.Auto) {
                _proxyApplied = false;
                _proxyList.Clear();
                _proxyIndex = 0;
                var t = FetchAndApplyProxyAsync();
            } else if (_proxyMode == ProxyMode.Mtproto) {
                string host = MtpHost.Text.Trim();
                string portStr = MtpPort.Text.Trim();
                string secret = MtpSecret.Text.Trim();
                if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(portStr) || string.IsNullOrEmpty(secret)) {
                    LoginStatus.Text = "Заполните все поля MTProto";
                    return;
                }
                if (!int.TryParse(portStr, out int port)) {
                    LoginStatus.Text = "Неверный порт";
                    return;
                }
                _proxyApplied = true;
                var t = ApplyProxyAsync(host, port, secret);
            } else if (_proxyMode == ProxyMode.Http) {
                string host = HttpHost.Text.Trim();
                string portStr = HttpPort.Text.Trim();
                if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(portStr)) {
                    LoginStatus.Text = "Заполните все поля HTTP";
                    return;
                }
                if (!int.TryParse(portStr, out int port)) {
                    LoginStatus.Text = "Неверный порт";
                    return;
                }
                _proxyApplied = true;
                ClearAllProxies();
                string req = "{\"@type\":\"addProxy\",\"proxy\":{\"@type\":\"proxy\",\"server\":\"" + host +
                             "\",\"port\":" + port + ",\"type\":{\"@type\":\"proxyTypeHttp\",\"username\":\"\",\"password\":\"\",\"http_only\":false}},\"enable\":true}";
                TdJson.SendUtf8(_client, req);
                ProxyStatusText.Text = "[..] " + host + ":" + port;
                ProxyStatusText.Visibility = Visibility.Visible;
            } else if (_proxyMode == ProxyMode.Socks) {
                string host = SocksHost.Text.Trim();
                string portStr = SocksPort.Text.Trim();
                string user = SocksUser.Text.Trim();
                string pass = SocksPass.Password;
                if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(portStr)) {
                    LoginStatus.Text = "Заполните все поля SOCKS5";
                    return;
                }
                if (!int.TryParse(portStr, out int port)) {
                    LoginStatus.Text = "Неверный порт";
                    return;
                }
                _proxyApplied = true;
                ClearAllProxies();
                string req = "{\"@type\":\"addProxy\",\"proxy\":{\"@type\":\"proxy\",\"server\":\"" + host +
                             "\",\"port\":" + port + ",\"type\":{\"@type\":\"proxyTypeSocks5\",\"username\":\"" + user + "\",\"password\":\"" + pass + "\"}},\"enable\":true}";
                TdJson.SendUtf8(_client, req);
                ProxyStatusText.Text = "[..] " + host + ":" + port;
                ProxyStatusText.Visibility = Visibility.Visible;
            }
        }

        private void ThemeToggle_Click(object sender, RoutedEventArgs e) {
            _isLightTheme = !_isLightTheme;
            Windows.Storage.ApplicationData.Current.LocalSettings.Values["light_theme"] = _isLightTheme;
            ApplyTheme();
        }

        private void ApplyTheme() {
            if (_isLightTheme) ApplyLightTheme();
            else ApplyDarkTheme();
            // ListView.Header рендерится асинхронно — применяем ещё раз через 200мс
            var t = new Windows.UI.Xaml.DispatcherTimer();
            t.Interval = TimeSpan.FromMilliseconds(200);
            t.Tick += (s2, e2) => {
                t.Stop();
                if (_isLightTheme) ApplyLightTheme();
                else ApplyDarkTheme();
            };
            t.Start();
        }

        private static Windows.UI.Xaml.Media.SolidColorBrush CB(string hex) {
            hex = hex.TrimStart('#');
            byte a = 255, r, g, b;
            if (hex.Length == 8) { a = Convert.ToByte(hex.Substring(0,2),16); hex = hex.Substring(2); }
            r = Convert.ToByte(hex.Substring(0,2),16);
            g = Convert.ToByte(hex.Substring(2,2),16);
            b = Convert.ToByte(hex.Substring(4,2),16);
            return new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(a,r,g,b));
        }

        private void ApplyDarkTheme() {
            ThemeToggleButton.Content = "☀";
            BubbleColorOut = "#2B5278";
            BubbleColorIn  = "#182533";
            ChatItem.ThemeTitleColor    = "#FFFFFF";
            ChatItem.ThemeSubtitleColor = "#888888";
            ChatItem.ThemeTimeColor     = "#888888";
            ChatItem.ThemeStatusColor   = "#0088cc";
            StartPanel.Background          = CB("#111111");
            MessagesPanel.Background       = CB("#111111");
            ChatHeader.Background          = CB("#1F3A52");
            BackButton.Foreground          = CB("#FFFFFF");
            CurrentChatTitle.Foreground    = CB("#FFFFFF");
            CurrentChatStatus.Foreground   = CB("#CCE8FF");
            if (ArchiveBackButton != null) ArchiveBackButton.Foreground = CB("#FFFFFF");
            if (ArchiveTitleText  != null) ArchiveTitleText.Foreground  = CB("#FFFFFF");
            if (ArchiveRowTitle   != null) ArchiveRowTitle.Foreground   = CB("#FFFFFF");
            ArchiveSubtitleText.Foreground = CB("#888888");
            InputPanel.Background          = CB("#1A1A1A");
            InputBorder.Background         = CB("#222222");
            if (MessageInputBorder != null) {
                MessageInputBorder.Background    = CB("#2A2A2A");
                MessageInputBorder.BorderBrush   = CB("#444444");
                MessageInputBorder.BorderThickness = new Windows.UI.Xaml.Thickness(1);
            }
            MessageInput.Foreground            = CB("#FFFFFF");
            
            var hdr = ChatListView.Header as Windows.UI.Xaml.Controls.StackPanel;
            if (hdr != null) hdr.Background = CB("#1A1A1A");
            ArchiveRow.Background          = CB("#222222");
            UnogramTitle.Foreground        = CB("#FFFFFF");
            ChatCountText.Foreground       = CB("#888888");
            ArchiveChatCountText.Foreground = CB("#888888");
            ThemeToggleButton.Foreground   = CB("#888888");
            ProxyStatusText.Foreground     = CB("#555555");
            ProxySettingsButton.Background  = CB("#AA333333");
            ProxySettingsButton.Foreground  = CB("#AAAAAA");
            LogoutButton.Background        = CB("#AA222222");
            LogoutButton.Foreground        = CB("#FF4444");
            // Поле поиска — тёмная тема
            if (SearchPanel != null) SearchPanel.Background = CB("#1C1C1E");
            if (SearchBorder != null) SearchBorder.Background = CB("#2A2A2E");
            if (SearchBox != null) SearchBox.Foreground = CB("#FFFFFF");
            if (FolderTabsScroll != null) FolderTabsScroll.Background = CB("#1C1C1E");
            UpdateFolderTabStyles();
            // Закреплённое — тёмная тема
            if (PinnedMessageBar != null) {
                PinnedMessageBar.Background = CB("#CC1F3A52");
                PinnedMessageText.Foreground = CB("#FFFFFF");
                PinnedLabel.Foreground = CB("#2AABEE");
                PinnedAccentLine.Fill = CB("#2AABEE");
            }
            NotifyAllChatTheme();
            UpdateBubbleColors();
        }

        private void ApplyLightTheme() {
            ThemeToggleButton.Content = "🌙";
            BubbleColorOut = "#EFFDDE";
            BubbleColorIn  = "#FFFFFF";
            // Статические цвета для DataTemplate чатов
            ChatItem.ThemeTitleColor    = "#000000";
            ChatItem.ThemeSubtitleColor = "#707070";
            ChatItem.ThemeTimeColor     = "#707070";
            ChatItem.ThemeStatusColor   = "#4CAF50";
            // Фон
            StartPanel.Background          = CB("#EFEFF3");
            MessagesPanel.Background       = CB("#B2CDB0");
            // Шапка чата — белая
            ChatHeader.Background          = CB("#FFFFFF");
            BackButton.Foreground          = CB("#2AABEE");  // синяя стрелка назад
            CurrentChatTitle.Foreground    = CB("#000000");  // чёрный ник
            CurrentChatStatus.Foreground   = CB("#000000");  // тёмно-серый статус
            // Архив
            if (ArchiveBackButton != null) ArchiveBackButton.Foreground = CB("#2AABEE");
            if (ArchiveTitleText  != null) ArchiveTitleText.Foreground  = CB("#000000");
            if (ArchiveRowTitle   != null) ArchiveRowTitle.Foreground   = CB("#000000");
            ArchiveSubtitleText.Foreground = CB("#707070");
            // Панель ввода — светло-серая
            InputPanel.Background          = CB("#F4F4F5");
            InputBorder.Background         = CB("#F4F4F5");
            if (MessageInputBorder != null) {
                MessageInputBorder.Background    = CB("#F0F2F5");
                MessageInputBorder.BorderBrush   = CB("#D8DCE0");
                MessageInputBorder.BorderThickness = new Windows.UI.Xaml.Thickness(1);
            }
            MessageInput.Foreground            = CB("#000000");
            
            // Шапка чатлиста
            var hdr = ChatListView.Header as Windows.UI.Xaml.Controls.StackPanel;
            if (hdr != null) hdr.Background = CB("#FFFFFF");
            ArchiveRow.Background          = CB("#F0F0F0");
            UnogramTitle.Foreground        = CB("#000000");
            ChatCountText.Foreground       = CB("#707070");
            ArchiveChatCountText.Foreground = CB("#707070");
            ThemeToggleButton.Foreground   = CB("#707070");
            ProxyStatusText.Foreground     = CB("#707070");
            ProxySettingsButton.Background  = CB("#E5E5E5");
            ProxySettingsButton.Foreground  = CB("#555555");
            LogoutButton.Background        = CB("#FFE5E5");
            LogoutButton.Foreground        = CB("#CC0000");
            // Поле поиска — светлая тема
            if (SearchPanel != null) SearchPanel.Background = CB("#EFEFF3");
            if (SearchBorder != null) SearchBorder.Background = CB("#E0E0E5");
            if (SearchBox != null) SearchBox.Foreground = CB("#000000");
            // Вкладки папок — светлый фон
            if (FolderTabsScroll != null) FolderTabsScroll.Background = CB("#FFFFFF");
            UpdateFolderTabStyles();
            // Закреплённое — светлая тема как в оригинальном Telegram
            if (PinnedMessageBar != null) {
                PinnedMessageBar.Background = CB("#FFFFFF");
                PinnedMessageText.Foreground = CB("#222222");
                PinnedLabel.Foreground = CB("#2AABEE");
                PinnedAccentLine.Fill = CB("#2AABEE");
            }
            NotifyAllChatTheme();
            UpdateBubbleColors();
        }

        private void BuildFolderTabs(Newtonsoft.Json.Linq.JArray folders) {
            FolderTabs.Children.Clear();
            if (folders == null || folders.Count == 0) {
                FolderTabsScroll.Visibility = Visibility.Collapsed;
                return;
            }
            // Вкладка "Все"
            FolderTabs.Children.Add(MakeFolderTab("Все", -1));
            foreach (var f in folders) {
                int fid = f["id"]?.ToObject<int>() ?? 0;
                var titleToken = f["name"];
                // chatFolderInfo.name = chatFolderName { text: formattedText { text: string } }
                string fname = titleToken?["text"]?["text"]?.ToString()  // chatFolderName.text.text
                            ?? titleToken?["text"]?.ToString()            // chatFolderName.text если строка
                            ?? titleToken?.ToString()                     // fallback
                            ?? "Папка";
                FolderTabs.Children.Add(MakeFolderTab(fname, fid));
                // Запрашиваем чаты папки по одной за раз через очередь
                _folderLoadQueue.Enqueue(fid);
            }
            FolderTabsScroll.Visibility = Visibility.Visible;
            UpdateFolderTabStyles();
            // Запускаем загрузку папок только если основной список уже загружен
            if (_mainListLoaded) LoadNextFolder();
        }

        private Button MakeFolderTab(string title, int folderId) {
            var btn = new Button {
                Content = title,
                Tag = folderId,
                Background = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.Transparent),
                Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.White),
                FontSize = 14,
                Padding = new Thickness(12, 8, 12, 8),
                BorderThickness = new Thickness(0)
            };
            btn.Click += (s, e) => SwitchFolder((int)(btn.Tag));
            return btn;
        }

        private void SwitchFolder(int folderId) {
            _currentFolderId = folderId;
            UpdateFolderTabStyles();
            if (ArchiveRow != null)
                ArchiveRow.Visibility = folderId == -1 ? Visibility.Visible : Visibility.Collapsed;
            if (folderId == -1) {
                _chatListItems.Clear();
                foreach (var c in _allChatItems)
                    _chatListItems.Add(c);
            } else {
                _chatListItems.Clear();
                if (_folderChatIds.ContainsKey(folderId)) {
                    foreach (var id in _folderChatIds[folderId]) {
                        if (_chatsDict.ContainsKey(id))
                            _chatListItems.Add(_chatsDict[id]);
                    }
                }
            }
            ChatCountText.Text = _chatListItems.Count.ToString();
        }

        private void UpdateFolderTabStyles() {
            bool light = _isLightTheme;
            var inactiveColor = light
                ? Windows.UI.Color.FromArgb(255, 100, 100, 100)  // тёмно-серый для светлой
                : Windows.UI.Colors.White;
            foreach (var child in FolderTabs.Children) {
                var btn = child as Button;
                if (btn == null) continue;
                bool isActive = (int)(btn.Tag) == _currentFolderId;
                btn.Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(
                    isActive ? Windows.UI.Color.FromArgb(255, 42, 171, 238) : inactiveColor);
                btn.BorderBrush = new Windows.UI.Xaml.Media.SolidColorBrush(
                    isActive ? Windows.UI.Color.FromArgb(255, 42, 171, 238) : Windows.UI.Colors.Transparent);
                btn.BorderThickness = new Thickness(0, 0, 0, isActive ? 2 : 0);
                if (light)
                    btn.Background = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.Transparent);
            }
        }

        private void UpdateAppBadge(int count) {
            try {
                var badgeXml = Windows.UI.Notifications.BadgeUpdateManager.GetTemplateContent(
                    Windows.UI.Notifications.BadgeTemplateType.BadgeNumber);
                var badgeNode = badgeXml.SelectSingleNode("/badge");
                if (badgeNode?.Attributes != null) {
                    var attr = badgeXml.CreateAttribute("value");
                    attr.NodeValue = count > 0 ? count.ToString() : "0";
                    badgeNode.Attributes.SetNamedItem(attr);
                }
                var badge = new Windows.UI.Notifications.BadgeNotification(badgeXml);
                Windows.UI.Notifications.BadgeUpdateManager.CreateBadgeUpdaterForApplication().Update(badge);
            } catch { }
        }

        private void NotifyAllChatTheme() {
            foreach (var c in _chatsDict.Values) c.NotifyThemeChanged();
        }

        private void UpdateBubbleColors() {
            // Перекрашиваем уже загруженные сообщения
            foreach (var m in _messageItems)
                if (!m.IsSeparator)
                    m.Background = m.IsOutgoing ? BubbleColorOut : BubbleColorIn;
        }

        // ======= СТИКЕРЫ =======

        // ======= КОНТАКТЫ =======

        private async Task HandleContactsLoaded(JArray userIds) {
            var contacts = new List<ContactItem>();
            foreach (var uid2 in userIds) {
                long cid2 = uid2.ToObject<long>();
                if (_usersDict.ContainsKey(cid2)) {
                    var u2 = _usersDict[cid2];
                    contacts.Add(new ContactItem {
                        UserId   = cid2,
                        FullName = cid2 == _myUserId ? "⭐ Избранное" : (u2["first_name"]?.ToString() + " " + u2["last_name"]?.ToString()).Trim(),
                        Username = cid2 == _myUserId ? "" : (u2["username"]?.ToString() ?? u2["usernames"]?["editable_username"]?.ToString() ?? ""),
                        LastSeen = cid2 == _myUserId ? "" : GetLastSeenText(u2["status"])
                    });
                } else {
                    // Нет данных — добавляем заглушку и запрашиваем
                    contacts.Add(new ContactItem { UserId = cid2, FullName = "..." });
                    TdJson.SendUtf8(_client, "{\"@type\":\"getUser\",\"user_id\":" + cid2 + "}");
                }
            }
            // Убираем себя из обычного списка — добавим как "Избранное" первым
            foreach (var cx in contacts)
            contacts = contacts.Where(c => c.UserId != _myUserId).OrderBy(c => c.FullName).ToList();
            if (_myUserId != 0) {
                var selfItem = new ContactItem { UserId = _myUserId, FullName = "⭐ Избранное" };
                contacts.Insert(0, selfItem);
                if (_usersDict.ContainsKey(_myUserId)) {
                    var t = LoadContactAvatarFromUser(selfItem, _usersDict[_myUserId]);
                }
            } else {
            }
            _contactItems = contacts;
            if (_myUserId == 0) _contactsPendingMyId = true;
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                ContactsLoadingText.Visibility = Visibility.Collapsed;
                ContactsListView.ItemsSource   = _contactItems;
                foreach (var contact in _contactItems)
                    if (_usersDict.ContainsKey(contact.UserId))
                        { var t = LoadContactAvatarFromUser(contact, _usersDict[contact.UserId]); }
            });
        }

        private string GetLastSeenText(JToken status) {
            if (status == null) return "";
            string stype = status["@type"]?.ToString() ?? "";
            switch (stype) {
                case "userStatusOnline": return "в сети";
                case "userStatusOffline":
                    int wasOnline = status["was_online"]?.ToObject<int>() ?? 0;
                    if (wasOnline == 0) return "давно не был";
                    var dt = DateTimeOffset.FromUnixTimeSeconds(wasOnline).LocalDateTime;
                    var now = DateTime.Now;
                    if (dt.Date == now.Date) return "был(а) сегодня в " + dt.ToString("HH:mm");
                    if (dt.Date == now.Date.AddDays(-1)) return "был(а) вчера в " + dt.ToString("HH:mm");
                    if ((now - dt).TotalDays < 7) return "был(а) " + dt.ToString("dddd в HH:mm");
                    return "был(а) " + dt.ToString("dd.MM.yyyy");
                case "userStatusRecently": return "недавно";
                case "userStatusLastWeek": return "на этой неделе";
                case "userStatusLastMonth": return "в этом месяце";
                default: return "";
            }
        }

        private async Task LoadContactAvatarFromUser(ContactItem contact, JToken user) {
            var ph = user["profile_photo"]?["small"] as JObject;
            if (ph == null) return;
            long pfid = ph["id"]?.ToObject<long>() ?? 0;
            string pPath = ph["local"]?["path"]?.ToString();
            if (!string.IsNullOrEmpty(pPath))
                await LoadContactAvatar(contact, pPath);
            else if (pfid > 0)
                TdJson.SendUtf8(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + pfid + ",\"priority\":1,\"synchronous\":false}");
        }

        private void ShowToastNotification(string title, string body) {
            try {
                // Строим XML вручную — полный контроль над звуком
                string xml = $@"<toast duration=""short"">
  <visual>
    <binding template=""ToastGeneric"">
      <text>{EscapeXml(title)}</text>
      <text>{EscapeXml(body)}</text>
    </binding>
  </visual>
  <audio src=""ms-winsoundevent:Notification.IM"" loop=""false""/>
</toast>";
                var toastXml = new Windows.Data.Xml.Dom.XmlDocument();
                toastXml.LoadXml(xml);
                var toast = new Windows.UI.Notifications.ToastNotification(toastXml);
                // Без ExpirationTime — уведомление живёт стандартное время
                // Показываем из любого потока через Dispatcher
                var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                    try {
                        Windows.UI.Notifications.ToastNotificationManager
                            .CreateToastNotifier().Show(toast);
                    } catch (Exception ex2) { Log("Toast show ERR: " + ex2.Message); }
                });
            } catch (Exception ex) { Log("Toast ERR: " + ex.Message); }
        }

        private static string EscapeXml(string s) {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        private void Favorites_Click(object sender, RoutedEventArgs e) {
            // Открываем чат с самим собой (Избранное)
            if (_myUserId == 0) return;
            // Ищем чат с собой — его ID совпадает с _myUserId
            if (_chatsDict.ContainsKey(_myUserId))
                OpenChat(_chatsDict[_myUserId], 0);
            else
                TdJson.SendUtf8(_client, "{\"@type\":\"createPrivateChat\",\"user_id\":" + _myUserId + ",\"force\":true}");
        }

        private void SoundToggle_Click(object sender, RoutedEventArgs e) {
            _soundEnabled = !_soundEnabled;
            Windows.Storage.ApplicationData.Current.LocalSettings.Values["sound_enabled"] = _soundEnabled;
            SoundToggleItem.Text = _soundEnabled ? "🔔 Звук: Вкл" : "🔕 Звук: Выкл";
        }

        private async void ClearCache_Click(object sender, RoutedEventArgs e) {
            var dialog = new Windows.UI.Popups.MessageDialog(
                "Удалить все скачанные фото, видео и аудио из кэша?", "Очистить кэш");
            dialog.Commands.Add(new Windows.UI.Popups.UICommand("Очистить", async cmd => {
                try {
                    // TDLib API — очищаем кэш файлов
                    TdJson.SendUtf8(_client, "{\"@type\":\"optimizeStorage\",\"size\":0,\"ttl\":0,\"count\":0,\"immunity_delay\":0" +
                        ",\"file_types\":[{\"@type\":\"fileTypePhoto\"},{\"@type\":\"fileTypeVideo\"},{\"@type\":\"fileTypeAudio\"}" +
                        ",{\"@type\":\"fileTypeAnimation\"},{\"@type\":\"fileTypeDocument\"}]" +
                        ",\"chat_ids\":[],\"exclude_chat_ids\":[],\"return_deleted_file_statistics\":true,\"chat_limit\":0}");
                    var confirmDialog = new Windows.UI.Popups.MessageDialog("Кэш очищен.", "Готово");
                    await confirmDialog.ShowAsync();
                } catch (Exception ex) {
                }
            }));
            dialog.Commands.Add(new Windows.UI.Popups.UICommand("Отмена"));
            await dialog.ShowAsync();
        }

        private ObservableCollection<ChatItem> _searchResults = new ObservableCollection<ChatItem>();
        private ObservableCollection<SearchResultItem> _searchAllResults = new ObservableCollection<SearchResultItem>();
        private ObservableCollection<SearchMessageItem> _searchMessageResults = new ObservableCollection<SearchMessageItem>();
        private string _searchQuery = "";

        private int _searchToken = 0;
        private void SearchBox_TextChanged(object sender, Windows.UI.Xaml.Controls.TextChangedEventArgs e) {
            _searchQuery = SearchBox.Text ?? "";
            SearchClearButton.Visibility = string.IsNullOrEmpty(_searchQuery) ? Visibility.Collapsed : Visibility.Visible;
            if (string.IsNullOrEmpty(_searchQuery)) {
                SearchResultsView.Visibility = Visibility.Collapsed;
                ChatListView.Visibility = Visibility.Visible;
                if (FolderTabsScroll != null) FolderTabsScroll.Visibility = _folderChatIds.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            } else {
                ChatListView.Visibility = Visibility.Collapsed;
                if (FolderTabsScroll != null) FolderTabsScroll.Visibility = Visibility.Collapsed;
                SearchResultsView.Visibility = Visibility.Visible;
                _searchAllResults.Clear();
                if (SearchResultsView.ItemsSource == null) SearchResultsView.ItemsSource = _searchAllResults;
                _searchToken++;
                int myToken = _searchToken;
                // Локальный поиск по чатам
                string q = _searchQuery.ToLower();
                bool anyChats = false;
                foreach (var c in _allChatItems) {
                    if (c.Title?.ToLower().Contains(q) == true) {
                        if (!anyChats) {
                            _searchAllResults.Add(new SearchResultItem { Type = SearchResultItem.ResultType.Header, Title = "Чаты" });
                            anyChats = true;
                        }
                        _searchAllResults.Add(new SearchResultItem {
                            Type = SearchResultItem.ResultType.Chat,
                            ChatId = c.Id, Title = c.Title,
                            Subtitle = c.LastMessage, Photo = c.Photo
                        });
                    }
                }
                // TDLib поиск
                TdJson.SendUtf8(_client, "{\"@type\":\"searchChats\",\"query\":\"" + _searchQuery.Replace("\"","\\\"") + "\",\"limit\":50}");
                TdJson.SendUtf8(_client, "{\"@type\":\"searchChatsOnServer\",\"query\":\"" + _searchQuery.Replace("\"","\\\"") + "\",\"limit\":50}");
                TdJson.SendUtf8(_client, "{\"@type\":\"searchPublicChats\",\"query\":\"" + _searchQuery.Replace("\"","\\\"") + "\"}");
                TdJson.SendUtf8(_client, "{\"@type\":\"searchMessages\",\"chat_list\":{\"@type\":\"chatListMain\"},\"query\":\"" + _searchQuery.Replace("\"","\\\"") + "\",\"limit\":20,\"offset\":\"\"}");
            }
        }

        private void SearchClear_Click(object sender, RoutedEventArgs e) {
            SearchBox.Text = "";
            _searchQuery = "";
            SearchClearButton.Visibility = Visibility.Collapsed;
            SearchResultsView.Visibility = Visibility.Collapsed;
            ChatListView.Visibility = Visibility.Visible;
            if (FolderTabsScroll != null) FolderTabsScroll.Visibility = _folderChatIds.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SearchResult_ItemClick(object sender, ItemClickEventArgs e) {
            var item = e.ClickedItem as SearchResultItem;
            if (item == null || item.IsHeader) return;
            SearchBox.Text = "";
            _searchQuery = "";
            SearchClearButton.Visibility = Visibility.Collapsed;
            SearchResultsView.Visibility = Visibility.Collapsed;
            ChatListView.Visibility = Visibility.Visible;
            if (FolderTabsScroll != null) FolderTabsScroll.Visibility = _folderChatIds.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (item.Type == SearchResultItem.ResultType.Message)
                _pendingScrollToMsgId = item.MessageId;
            if (_chatsDict.ContainsKey(item.ChatId))
                OpenChat(_chatsDict[item.ChatId], 0);
        }

        private void SearchMessage_ItemClick(object sender, ItemClickEventArgs e) { }

        private void ApplySearch() { }

        private void ContactsButton_Click(object sender, RoutedEventArgs e) {
            ContactsOverlay.Visibility = Visibility.Visible;
            ContactsListView.ItemsSource = null;
            ContactsLoadingText.Visibility = Visibility.Visible;
            if (_myUserId == 0) {
                _waitingForMe = true;
                TdJson.SendUtf8(_client, "{\"@type\":\"getMe\"}");
            }
            TdJson.SendUtf8(_client, "{\"@type\":\"getContacts\"}");
        }

        private void ContactsOverlay_Close(object sender, RoutedEventArgs e) {
            ContactsOverlay.Visibility = Visibility.Collapsed;
        }

        private void ContactItem_Click(object sender, Windows.UI.Xaml.Controls.ItemClickEventArgs e) {
            var contact = e.ClickedItem as ContactItem;
            if (contact == null) return;
            ContactsOverlay.Visibility = Visibility.Collapsed;
            // createPrivateChat вернёт существующий чат или создаст новый
            _pendingContactUserId = contact.UserId;
            TdJson.SendUtf8(_client, "{\"@type\":\"createPrivateChat\",\"user_id\":" + contact.UserId + ",\"force\":true}");
        }

        private async Task LoadContactAvatar(ContactItem contact, string path) {
            try {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
                var bmp = new BitmapImage();
                using (var stream = await file.OpenReadAsync())
                    await bmp.SetSourceAsync(stream);
                await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                    contact.Photo = bmp;
                    contact.OnPropertyChanged("Photo");
                    contact.OnPropertyChanged("NoPhotoVisibility");
                });
            } catch { }
        }

        private void StickerButton_Click(object sender, RoutedEventArgs e) {
            if (_stickerPanelOpen) {
                StickerPanel.Visibility = Visibility.Collapsed;
                _stickerPanelOpen = false;
                return;
            }
            StickerPanel.Visibility = Visibility.Visible;
            _stickerPanelOpen = true;
            if (_loadedStickerSetIds.Count == 0) {
                StickerGrid.ItemsSource = null;
                StickerLoadingText.Text = "Загрузка...";
                StickerProgressText.Text = "";
                StickerLoadingPanel.Visibility = Visibility.Visible;
                StickerPackTabs.Children.Clear();
                TdJson.SendUtf8(_client, "{\"@type\":\"getInstalledStickerSets\",\"sticker_type\":{\"@type\":\"stickerTypeRegular\"}}");
            }
        }

        private void StickerGrid_ItemClick(object sender, Windows.UI.Xaml.Controls.ItemClickEventArgs e) {
            var item = e.ClickedItem as StickerItem;
            if (item == null) return;
            StickerPanel.Visibility = Visibility.Collapsed;
            _stickerPanelOpen = false;

            if (!string.IsNullOrEmpty(item.RemoteFileId)) {
                string sReq = "{\"@type\":\"sendMessage\",\"chat_id\":" + _currentChatId +
                    (_threadMessageId != 0 ? ",\"message_thread_id\":" + _threadMessageId : "") +
                    ",\"input_message_content\":{\"@type\":\"inputMessageSticker\"" +
                    ",\"sticker\":{\"@type\":\"inputSticker\"" +
                    ",\"sticker\":{\"@type\":\"inputFileRemote\",\"id\":\"" + item.RemoteFileId + "\"}" +
                    ",\"width\":512,\"height\":512}}}";
                TdJson.SendUtf8(_client, sReq);
            } else {
                // Нет remote id — скачиваем и отправляем по file_id
                _pendingStickerFileId = item.FileId;
                _pendingStickerChatId = _currentChatId;
                TdJson.SendUtf8(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + item.FileId + ",\"priority\":32,\"synchronous\":false}");
            }
        }

        private void LoadStickerSet(long setId) {
            if (_loadedStickerSetIds.Contains(setId)) return;
            _loadedStickerSetIds.Add(setId);
            TdJson.SendUtf8(_client, "{\"@type\":\"getStickerSet\",\"set_id\":" + setId + "}");
        }

        private void HandleStickerSets(Newtonsoft.Json.Linq.JToken update) {
            var sets = update["sets"] as Newtonsoft.Json.Linq.JArray;
            if (sets == null) return;
            // Добавляем вкладки и загружаем первый пак
            StickerPackTabs.Children.Clear();
            bool first = true;
            foreach (var s in sets) {
                long sid = s["id"]?.ToObject<long>() ?? 0;
                if (sid == 0) continue;
                string name = s["title"]?.ToString() ?? "?";
                var btn = new Windows.UI.Xaml.Controls.Button {
                    Content = name.Length > 6 ? name.Substring(0, 6) : name,
                    FontSize = 11,
                    Padding = new Windows.UI.Xaml.Thickness(8, 4, 8, 4),
                    Background = first ? CB("#0088cc") : CB("#333333"),
                    Foreground = CB("#FFFFFF"),
                    Tag = sid
                };
                long capturedSid = sid;
                btn.Click += (s2, e2) => {
                    foreach (var child in StickerPackTabs.Children)
                        if (child is Windows.UI.Xaml.Controls.Button b)
                            b.Background = CB("#333333");
                    ((Windows.UI.Xaml.Controls.Button)s2).Background = CB("#0088cc");
                    ShowStickerSet(capturedSid);
                };
                StickerPackTabs.Children.Add(btn);
                if (first) {
                    _currentStickerSetId = sid; // первый пак — устанавливаем сразу
                    LoadStickerSet(sid);
                    first = false;
                }
            }
        }

        private long _currentStickerSetId = 0;

        private void ShowStickerSet(long setId) {
            _currentStickerSetId = setId;
            var existing = _currentStickerItems.Where(s => s.SetId == setId).ToList();
            if (existing.Count > 0) {
                StickerGrid.ItemsSource = existing;
                StickerLoadingPanel.Visibility = Visibility.Collapsed;
                UpdateStickerProgress(setId);
            } else {
                StickerGrid.ItemsSource = null;
                StickerLoadingText.Text = "Загрузка...";
                StickerProgressText.Text = "";
                StickerLoadingPanel.Visibility = Visibility.Visible;
                LoadStickerSet(setId);
            }
        }

        private async void HandleStickerSet(Newtonsoft.Json.Linq.JToken update) {
            long setId = update["id"]?.ToObject<long>() ?? 0;
            var stickers = update["stickers"] as Newtonsoft.Json.Linq.JArray;
            if (stickers == null || setId == 0) return;
            int total = stickers.Count;

            var items = new List<StickerItem>();
            int downloadCount = 0;

            foreach (var st in stickers) {
                var stickerFile = st["sticker"] as JObject;
                if (stickerFile == null) continue;
                long fid = stickerFile["id"]?.ToObject<long>() ?? 0;
                string remoteId = stickerFile["remote"]?["id"]?.ToString() ?? "";
                var item = new StickerItem { SetId = setId, FileId = fid, RemoteFileId = remoteId };
                items.Add(item);

                var thumb = st["thumbnail"];
                var thumbFile = thumb?["file"] as JObject;
                if (thumbFile != null) {
                    long tfid = thumbFile["id"]?.ToObject<long>() ?? 0;
                    item.ThumbFileId = tfid;
                    string tPath = thumbFile["local"]?["path"]?.ToString();
                    if (!string.IsNullOrEmpty(tPath) &&
                        (tPath.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ||
                         tPath.EndsWith(".jpg",  StringComparison.OrdinalIgnoreCase))) {
                        var capturedItem = item;
                        var capturedSetId = setId;
                        _ = LoadStickerThumbAsync(tPath).ContinueWith(t2 => {
                            if (t2.Result != null) {
                                capturedItem.Thumb = t2.Result;
                                var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, () => {
                                    UpdateStickerProgress(capturedSetId);
                                });
                            }
                        }, TaskScheduler.Default);
                    } else if (tfid > 0) {
                        _stickerThumbToItem[tfid] = fid;
                        if (downloadCount < 20) {
                            TdJson.SendUtf8(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + tfid + ",\"priority\":3,\"synchronous\":false}");
                            downloadCount++;
                        }
                    }
                }
            }

            _currentStickerItems.RemoveAll(s => s.SetId == setId);
            _currentStickerItems.AddRange(items);

            // Показываем сразу все ячейки (с пустыми thumb), скрываем "Загрузка..."
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                if (StickerPanel.Visibility == Visibility.Visible && _currentStickerSetId == setId) {
                    StickerGrid.ItemsSource = _currentStickerItems.Where(s => s.SetId == setId).ToList();
                    StickerLoadingPanel.Visibility = Visibility.Collapsed;
                    UpdateStickerProgress(setId);
                }
            });
        }

        // Обновляем счётчик загруженных thumbnail
        private void UpdateStickerProgress(long setId) {
            if (_currentStickerSetId != setId || StickerPanel.Visibility != Visibility.Visible) return;
            var setItems = _currentStickerItems.Where(s => s.SetId == setId).ToList();
            int loaded = setItems.Count(s => s.Thumb != null);
            int total  = setItems.Count;
            if (total == 0) return;
            if (loaded < total) {
                StickerProgressText.Text = loaded + " / " + total;
                StickerProgressText.Visibility = Visibility.Visible;
            } else {
                StickerProgressText.Visibility = Visibility.Collapsed;
            }
        }

        private async Task<BitmapImage> LoadStickerThumbAsync(string path) {
            try {
                if (path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)) {
                    var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
                    byte[] data;
                    using (var stream = await file.OpenReadAsync())
                    using (var reader = new Windows.Storage.Streams.DataReader(stream)) {
                        await reader.LoadAsync((uint)stream.Size);
                        data = new byte[stream.Size];
                        reader.ReadBytes(data);
                    }
                    var wb = await WebPDecoder.DecodeAsync(data);
                    // Конвертируем WriteableBitmap → BitmapImage через InMemoryRandomAccessStream
                    var bmp = new BitmapImage();
                    using (var ras = new Windows.Storage.Streams.InMemoryRandomAccessStream()) {
                        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                            Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, ras);
                        // Читаем пиксели из PixelBuffer через Stream
                        byte[] px = new byte[wb.PixelBuffer.Capacity];
                        using (var pixStream = wb.PixelBuffer.AsStream())
                            await pixStream.ReadAsync(px, 0, px.Length);
                        encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                            Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                            (uint)wb.PixelWidth, (uint)wb.PixelHeight, 96, 96, px);
                        await encoder.FlushAsync();
                        ras.Seek(0);
                        await bmp.SetSourceAsync(ras);
                    }
                    return bmp;
                } else {
                    var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
                    var bmp = new BitmapImage();
                    using (var stream = await file.OpenReadAsync())
                        await bmp.SetSourceAsync(stream);
                    return bmp;
                }
            } catch { return null; }
        }

        private async void HandleStickerThumbDownloaded(long fileId, string path) {
            if (!_stickerThumbToItem.ContainsKey(fileId)) return;
            long stickerFid = _stickerThumbToItem[fileId];
            var bmp = await LoadStickerThumbAsync(path);
            if (bmp == null) return;
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                var item = _currentStickerItems.FirstOrDefault(s3 => s3.FileId == stickerFid);
                if (item != null) {
                    item.Thumb = bmp;
                    UpdateStickerProgress(item.SetId);
                }
            });
        }

        private async void LogoutButton_Click(object sender, RoutedEventArgs e) {
            var dialog = new Windows.UI.Popups.MessageDialog("Выйти из аккаунта?", "Выход");
            dialog.Commands.Add(new Windows.UI.Popups.UICommand("Выйти", cmd => {
                TdJson.SendUtf8(_client, "{\"@type\":\"logOut\"}");
            }));
            dialog.Commands.Add(new Windows.UI.Popups.UICommand("Отмена"));
            dialog.DefaultCommandIndex = 1;
            await dialog.ShowAsync();
        }

        private void BackButton_Click(object sender, RoutedEventArgs e) {
            ProfileOverlay.Visibility = Visibility.Collapsed;
            if (_threadMessageId != 0) {
                long channelChatId = _threadChatId;
                _threadMessageId = 0;
                _threadChatId = 0;
                OpenChatById(channelChatId);
                return;
            }
            if (_currentChatId != 0)
                TdJson.SendUtf8(_client, "{\"@type\":\"closeChat\",\"chat_id\":" + _currentChatId + "}");
            _currentChatId = 0;
            _pendingHistoryChatId = 0;
            LoadingIndicator.Visibility = Visibility.Collapsed;
            MessagesListView.Visibility = Visibility.Visible;
            MessagesPanel.Visibility = Visibility.Collapsed;
            StartPanel.Visibility = Visibility.Visible;
        }

        private void ArchiveRow_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e) {
            OpenArchive();
        }

        private void OpenArchive() {
            _inArchive = true;
            ChatListView.ItemsSource = _archiveChatItems;
            MainListHeader.Visibility = Visibility.Collapsed;
            ArchiveListHeader.Visibility = Visibility.Visible;
            ArchiveRow.Visibility = Visibility.Collapsed;

            if (!_archiveLoaded) {
                _archiveLoaded = true;
                _loadingArchive = true;
                TdJson.SendUtf8(_client, "{\"@type\":\"getChats\",\"chat_list\":{\"@type\":\"chatListArchive\"},\"limit\":200}");
            } else {
                ArchiveChatCountText.Text = "чатов: " + _archiveChatItems.Count;
            }
        }

        private void ArchiveBack_Click(object sender, RoutedEventArgs e) {
            _inArchive = false;
            ChatListView.ItemsSource = _chatListItems;
            MainListHeader.Visibility = Visibility.Visible;
            ArchiveListHeader.Visibility = Visibility.Collapsed;
            ArchiveRow.Visibility = Visibility.Visible;
        }

        private void UpdateArchiveUnreadBadge() {
            int total = _archiveChatItems.Sum(c => c.UnreadCount);
            if (total > 0) {
                ArchiveUnreadText.Text = total > 99 ? "99+" : total.ToString();
                ArchiveUnreadBadge.Visibility = Visibility.Visible;
                ArchiveArrow.Visibility = Visibility.Collapsed;
            } else {
                ArchiveUnreadBadge.Visibility = Visibility.Collapsed;
                ArchiveArrow.Visibility = Visibility.Visible;
            }
        }

        private void SendPhone_Click(object sender, RoutedEventArgs e) {
            if (string.IsNullOrWhiteSpace(PhoneInput.Text)) return;
            PhoneButton.IsEnabled = false;
            LoginStatus.Text = "Отправка номера...";
            TdJson.SendUtf8(_client, "{\"@type\":\"setAuthenticationPhoneNumber\",\"phone_number\":\"" + PhoneInput.Text.Trim() + "\"}");
        }

        private void SendCode_Click(object sender, RoutedEventArgs e) {
            if (string.IsNullOrWhiteSpace(CodeInput.Text)) return;
            CodeButton.IsEnabled = false;
            LoginStatus.Text = "Проверка кода...";
            TdJson.SendUtf8(_client, "{\"@type\":\"checkAuthenticationCode\",\"code\":\"" + CodeInput.Text.Trim() + "\"}");
        }

        private MessageItem _selectedMessageForCopy = null;
        private MessageItem _pendingContextMsg = null; // сообщение для Reply/Forward

        private void MessageBubble_Holding(object sender, Windows.UI.Xaml.Input.HoldingRoutedEventArgs e) {
            if (e.HoldingState != Windows.UI.Input.HoldingState.Started) return;
            var border = sender as Border;
            if (border == null) return;
            _selectedMessageForCopy = border.DataContext as MessageItem;
            _pendingContextMsg = _selectedMessageForCopy;

            // Показываем/скрываем пункты редактирования и удаления в зависимости от типа сообщения
            var flyout = FlyoutBase.GetAttachedFlyout(border) as MenuFlyout;
            if (flyout != null) {
                bool canEdit = _selectedMessageForCopy?.IsOutgoing == true && !string.IsNullOrEmpty(_selectedMessageForCopy?.Text);
                bool canDelete = true;
                // Собираем упоминания из текущего сообщения
                var mentions = _selectedMessageForCopy?.Entities?
                    .Where(en => en.Mention != null).ToList();
                foreach (var item in flyout.Items) {
                    if (item is MenuFlyoutItem mfi) {
                        if (mfi.Name == "MenuEdit") mfi.Visibility = canEdit ? Visibility.Visible : Visibility.Collapsed;
                        if (mfi.Name == "MenuDeleteSelf" || mfi.Name == "MenuDeleteAll")
                            mfi.Visibility = canDelete ? Visibility.Visible : Visibility.Collapsed;
                        if (mfi.Name == "MenuPin") {
                            bool isPinned = _selectedMessageForCopy?.Id == _pinnedMessageId && _pinnedMessageId != 0;
                            mfi.Text = isPinned ? "📌 Открепить" : "📌 Закрепить";
                        }
                        if (mfi.Name == "MenuMention") {
                            if (mentions != null && mentions.Count > 0) {
                                mfi.Visibility = Visibility.Visible;
                                mfi.Text = mentions.Count == 1
                                    ? "👤 Открыть " + mentions[0].Mention
                                    : "👤 Открыть упоминание (" + mentions.Count + ")";
                            } else {
                                mfi.Visibility = Visibility.Collapsed;
                            }
                        }
                    }
                }
            }

            FlyoutBase.ShowAttachedFlyout(border);
        }

        private async void InlineButton_Click(object sender, RoutedEventArgs e) {
            var btn = (sender as Windows.UI.Xaml.Controls.Button)?.Tag as InlineButton;
            if (btn == null) return;

            if (!string.IsNullOrEmpty(btn.Url)) {
                await Windows.System.Launcher.LaunchUriAsync(new Uri(btn.Url));
                return;
            }
            if (!string.IsNullOrEmpty(btn.CallbackData)) {
                // Найти msgId через Tag кнопки — он хранится в Tag как long через parent
                var button = sender as Windows.UI.Xaml.Controls.Button;
                // Идём вверх по визуальному дереву до Border с DataContext = MessageItem
                DependencyObject el = button;
                MessageItem msgItem = null;
                while (el != null) {
                    if (el is FrameworkElement fe && fe.DataContext is MessageItem mi) { msgItem = mi; break; }
                    el = Windows.UI.Xaml.Media.VisualTreeHelper.GetParent(el);
                }
                if (msgItem == null) return;
                string payload = "{\"@type\":\"getCallbackQueryAnswer\","
                    + "\"chat_id\":" + _currentChatId + ","
                    + "\"message_id\":" + msgItem.Id + ","
                    + "\"payload\":{\"@type\":\"callbackQueryPayloadData\","
                    + "\"data\":\"" + btn.CallbackData + "\"}}";
                TdJson.SendUtf8(_client, payload);
            }
        }

        private void CopyMessage_Click(object sender, RoutedEventArgs e) {
            if (_selectedMessageForCopy == null) return;
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(_selectedMessageForCopy.Text ?? "");
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            _selectedMessageForCopy = null;
        }

        private void DeleteMessageSelf_Click(object sender, RoutedEventArgs e) {
            if (_selectedMessageForCopy == null) return;
            DeleteMessages(new[] { _selectedMessageForCopy.Id }, revoke: false);
            _selectedMessageForCopy = null;
        }

        private void DeleteMessageAll_Click(object sender, RoutedEventArgs e) {
            if (_selectedMessageForCopy == null) return;
            DeleteMessages(new[] { _selectedMessageForCopy.Id }, revoke: true);
            _selectedMessageForCopy = null;
        }

        private void DeleteMessages(long[] messageIds, bool revoke) {
            var req = new JObject {
                ["@type"] = "deleteMessages",
                ["chat_id"] = _currentChatId,
                ["message_ids"] = new JArray(messageIds),
                ["revoke"] = revoke
            };
            TdJson.SendUtf8(_client, req.ToString(Newtonsoft.Json.Formatting.None));
            // Убираем из UI сразу
            var _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                foreach (var id in messageIds) {
                    var item = _messageItems.FirstOrDefault(m => m.Id == id);
                    if (item != null) _messageItems.Remove(item);
                    if (_messagesDict.ContainsKey(id)) _messagesDict.Remove(id);
                }
            });
        }

        private void EditMessage_Click(object sender, RoutedEventArgs e) {
            if (_selectedMessageForCopy == null) return;
            var msg = _selectedMessageForCopy;
            _selectedMessageForCopy = null;
            if (string.IsNullOrEmpty(msg.Text)) return;
            if (!msg.IsOutgoing) return; // редактировать можно только свои сообщения
            MessageInput.Text = msg.Text;
            MessageInput.SelectionStart = msg.Text.Length;
            _editingMessageId = msg.Id;
            SendButton.Content = "✓";
        }

        private long _editingMessageId = 0;
        private long _replyToMessageId = 0; // id сообщения на которое отвечаем

        private void MessageInput_TextChanged(object sender, Windows.UI.Xaml.Controls.TextChangedEventArgs e) {
            if (_currentChatId == 0 || string.IsNullOrEmpty(MessageInput.Text)) return;
            // Отправляем chatActionTyping и перезапускаем таймер сброса
            TdJson.SendUtf8(_client, "{\"@type\":\"sendChatAction\",\"chat_id\":" + _currentChatId +
                ",\"action\":{\"@type\":\"chatActionTyping\"}}");
            _typingTimer.Stop();
            _typingTimer.Start();
        }

        private void MessageInput_Holding(object sender, Windows.UI.Xaml.Input.HoldingRoutedEventArgs e) {
            if (e.HoldingState != Windows.UI.Input.HoldingState.Started) return;
            FlyoutBase.ShowAttachedFlyout(MessageInput);
        }

        private async void PasteToInput_Click(object sender, RoutedEventArgs e) {
            var dp = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (dp.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text)) {
                string text = await dp.GetTextAsync();
                int pos = MessageInput.SelectionStart;
                MessageInput.Text = MessageInput.Text.Insert(pos, text);
                MessageInput.SelectionStart = pos + text.Length;
            }
        }

        private void SendPassword_Click(object sender, RoutedEventArgs e) {
            if (string.IsNullOrWhiteSpace(PasswordInput.Password)) return;
            PasswordButton.IsEnabled = false;
            LoginStatus.Text = "Проверка пароля...";
            var pwd = PasswordInput.Password.Replace("\\", "\\\\").Replace("\"", "\\\"");
            TdJson.SendUtf8(_client, "{\"@type\":\"checkAuthenticationPassword\",\"password\":\"" + pwd + "\"}");
        }
    }
}
