using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.ApplicationModel.Background;
using Windows.ApplicationModel.ExtendedExecution;
using Windows.UI.Notifications;
using Newtonsoft.Json.Linq;

namespace TelegramWP10
{
    /// <summary>
    /// Фоновая работа в рамках того, что реально разрешает Windows 10 Mobile.
    ///
    /// Постоянно живущего процесса на Mobile получить нельзя:
    ///   * ControlChannelTrigger и SocketActivityTrigger требуют StreamSocket,
    ///     а сокет TDLib живёт внутри нативной tdjson.dll и наружу не отдаётся;
    ///   * extendedExecutionUnconstrained на Mobile не поддерживается;
    ///   * PushNotificationTrigger требует регистрации пакета в Store (WNS).
    ///
    /// Остаётся два разрешённых механизма, оба реализованы здесь:
    ///   1. ExtendedExecutionSession — короткая отсрочка приостановки, чтобы
    ///      соединение не рвалось при быстром сворачивании.
    ///   2. TimeTrigger раз в 15 минут (single-process) — догрузка и toast.
    /// </summary>
    public sealed class BackgroundService
    {
        public const string CatchUpTaskName = "UnogramCatchUp";
        private const uint CatchUpIntervalMinutes = 15;

        /// <summary>Сколько держим процесс живым в задаче догрузки.</summary>
        private const int CatchUpDrainSeconds = 20;

        private static BackgroundService _instance;
        public static BackgroundService Instance
        {
            get { return _instance ?? (_instance = new BackgroundService()); }
        }

        private BackgroundService() { }

        private ExtendedExecutionSession _session;

        /// <summary>Приложение сейчас на экране. Ставится из App.</summary>
        public static bool IsInForeground = true;

        // ------------------------------------------------------------------
        // 1. Отсрочка приостановки
        // ------------------------------------------------------------------

        /// <summary>
        /// Запрашивается из App.OnSuspending внутри deferral. Окно короткое и
        /// системой не гарантируется — это защита от разрыва соединения при
        /// быстром переключении приложений, а не фоновый режим.
        /// </summary>
        public async Task<bool> RequestGraceWindowAsync()
        {
            ClearSession();

            if (await TryRequestAsync(ExtendedExecutionReason.Unspecified)) return true;
            // Некоторые сборки Mobile отдают Unspecified только в foreground —
            // при отказе пробуем причину, предназначенную для приостановки.
            return await TryRequestAsync(ExtendedExecutionReason.SavingData);
        }

        private async Task<bool> TryRequestAsync(ExtendedExecutionReason reason)
        {
            var session = new ExtendedExecutionSession();
            session.Reason = reason;
            session.Description = "Дочитываем обновления Telegram";
            session.Revoked += OnSessionRevoked;

            try
            {
                var result = await session.RequestExtensionAsync();
                if (result == ExtendedExecutionResult.Allowed)
                {
                    _session = session;
                    Diag("ExtendedExecution ALLOWED, reason=" + reason);
                    return true;
                }
                Diag("ExtendedExecution DENIED, reason=" + reason);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[BG] RequestExtensionAsync failed (" + reason + "): " + ex.Message);
            }

            try { session.Revoked -= OnSessionRevoked; session.Dispose(); } catch { }
            return false;
        }

        /// <summary>Вызывается при возобновлении — окно больше не нужно.</summary>
        public void ReleaseGraceWindow()
        {
            ClearSession();
        }

        private void ClearSession()
        {
            if (_session == null) return;
            try
            {
                _session.Revoked -= OnSessionRevoked;
                _session.Dispose();
            }
            catch { }
            _session = null;
        }

        private void OnSessionRevoked(object sender, ExtendedExecutionRevokedEventArgs args)
        {
            // Reason == SystemPolicy означает, что система забрала окно раньше срока.
            Diag("ExtendedExecution REVOKED: " + args.Reason);
            ClearSession();
        }

        // ------------------------------------------------------------------
        // 2. Периодическая догрузка
        // ------------------------------------------------------------------

        /// <summary>
        /// Регистрирует single-process задачу по TimeTrigger. Точка входа не
        /// задаётся: активация приходит в App.OnBackgroundActivated, отдельный
        /// winmd-проект не нужен.
        /// </summary>
        public static async Task<bool> RegisterCatchUpTaskAsync()
        {
            BackgroundAccessStatus access;
            try
            {
                access = await BackgroundExecutionManager.RequestAccessAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[BG] RequestAccessAsync failed: " + ex.Message);
                return false;
            }

            Diag("Background access: " + access);
            if (access == BackgroundAccessStatus.Denied ||
                access == BackgroundAccessStatus.DeniedByUser ||
                access == BackgroundAccessStatus.DeniedBySystemPolicy)
                return false;

            foreach (var t in BackgroundTaskRegistration.AllTasks)
            {
                if (t.Value.Name == CatchUpTaskName)
                {
                    Debug.WriteLine("[BG] Catch-up task already registered");
                    return true;
                }
            }

            try
            {
                var builder = new BackgroundTaskBuilder();
                builder.Name = CatchUpTaskName;
                builder.SetTrigger(new TimeTrigger(CatchUpIntervalMinutes, false));
                builder.Register();
                Diag("Catch-up task registered (" + CatchUpIntervalMinutes + " min)");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[BG] Catch-up task registration failed: " + ex.Message);
                return false;
            }
        }

        public static void UnregisterCatchUpTask()
        {
            foreach (var t in BackgroundTaskRegistration.AllTasks)
            {
                if (t.Value.Name == CatchUpTaskName)
                {
                    try { t.Value.Unregister(true); } catch { }
                    Debug.WriteLine("[BG] Catch-up task unregistered");
                }
            }
        }

        /// <summary>Вызывается из App.OnBackgroundActivated.</summary>
        public static async Task RunCatchUpAsync(IBackgroundTaskInstance taskInstance)
        {
            var deferral = taskInstance.GetDeferral();
            bool cancelled = false;
            taskInstance.Canceled += (s, reason) =>
            {
                cancelled = true;
                Debug.WriteLine("[BG] Catch-up cancelled: " + reason);
            };

            try
            {
                LogMemoryBudget("catch-up start");

                if (MainPage.ActiveClient != IntPtr.Zero)
                {
                    // Процесс жив: LongPolling() продолжает крутиться, TDLib сам
                    // переподключится. Пинаем сетевой слой и даём время добрать
                    // накопившиеся апдейты — они пройдут обычным путём и поднимут toast.
                    Diag("Catch-up FIRED, client alive - draining");
                    TdJson.SendUtf8(MainPage.ActiveClient,
                        "{\"@type\":\"setNetworkType\",\"type\":{\"@type\":\"networkTypeOther\"}}");

                    for (int i = 0; i < CatchUpDrainSeconds && !cancelled; i++)
                        await Task.Delay(1000);
                }
                else
                {
                    Diag("Catch-up FIRED, cold process - starting TDLib session");
                    await RunColdSessionAsync(() => cancelled);
                }

                LogMemoryBudget("catch-up end");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[BG] Catch-up failed: " + ex.Message);
            }
            finally
            {
                deferral.Complete();
            }
        }

        /// <summary>
        /// Бюджет памяти фоновой задачи. На Mobile он жёстче, чем на desktop, и
        /// именно он решает, можно ли поднимать TDLib в холодном процессе.
        /// </summary>
        private static void LogMemoryBudget(string stage)
        {
            try
            {
                ulong limit = Windows.System.MemoryManager.AppMemoryUsageLimit;
                ulong used = Windows.System.MemoryManager.AppMemoryUsage;
                Diag(string.Format(
                    "Memory ({0}): limit={1} KB, used={2} KB, free={3} KB, level={4}",
                    stage, limit / 1024, used / 1024,
                    limit > used ? (limit - used) / 1024 : 0,
                    Windows.System.MemoryManager.AppMemoryUsageLevel));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[BG] MemoryManager unavailable: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------
        // Холодный запуск TDLib внутри фоновой задачи
        // ------------------------------------------------------------------

        /// <summary>Сколько секунд качаем апдейты до принудительного закрытия.</summary>
        private const int ColdSessionBudgetSeconds = 25;

        /// <summary>
        /// На одну базу TDLib должен приходиться один клиент. Фоновая задача
        /// single-process живёт в том же процессе, что и UI, поэтому доступ к
        /// сессии сериализуем. TDLib держит lock-файл и при втором клиенте
        /// вернёт ошибку, а не испортит базу, но гонку лучше не допускать.
        /// </summary>
        private static readonly System.Threading.SemaphoreSlim TdGate =
            new System.Threading.SemaphoreSlim(1, 1);

        private static volatile bool _handoverRequested;

        /// <summary>Просит фоновую сессию закрыться — вызывать при выходе на передний план.</summary>
        public static void RequestForegroundHandover()
        {
            _handoverRequested = true;
        }

        /// <summary>Занимает сессию под клиент переднего плана. Не освобождается.</summary>
        public static bool TryEnterTdSession(int timeoutMs)
        {
            try { return TdGate.Wait(timeoutMs); } catch { return false; }
        }

        private static async Task RunColdSessionAsync(Func<bool> cancelled)
        {
            if (!TdGate.Wait(0))
            {
                Diag("Cold session skipped: TDLib already open in this process");
                return;
            }

            IntPtr client = IntPtr.Zero;
            int notified = 0;

            try
            {
                _handoverRequested = false;

                var localFolder = Windows.Storage.ApplicationData.Current.LocalFolder;
                var appFolder = await localFolder.CreateFolderAsync("Unogram",
                    Windows.Storage.CreationCollisionOption.OpenIfExists);
                string dbPath = appFolder.Path.Replace("\\", "/") + "/td_db";
                var filesFolder = await appFolder.CreateFolderAsync("td_db_files",
                    Windows.Storage.CreationCollisionOption.OpenIfExists);

                client = TdJson.td_json_client_create();
                if (client == IntPtr.Zero) { Diag("Cold session: client create failed"); return; }

                var parameters = new JObject {
                    ["@type"] = "setTdlibParameters",
                    ["use_test_dc"] = false,
                    ["database_directory"] = dbPath,
                    ["files_directory"] = filesFolder.Path.Replace("\\", "/"),
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

                IntPtr c = client;
                bool authorized = false;
                bool abort = false;
                var deadline = DateTime.UtcNow.AddSeconds(ColdSessionBudgetSeconds);
                var titles = new System.Collections.Generic.Dictionary<long, string>();
                var seen = new System.Collections.Generic.HashSet<long>();

                await Task.Run(() =>
                {
                    while (DateTime.UtcNow < deadline && !cancelled() && !_handoverRequested && !abort)
                    {
                        IntPtr res = TdJson.td_json_client_receive(c, 1.0);
                        if (res == IntPtr.Zero) continue;
                        string json = TdJson.IntPtrToStringUtf8(res);
                        if (string.IsNullOrEmpty(json)) continue;

                        JObject u;
                        try { u = JObject.Parse(json); } catch { continue; }
                        string type = u["@type"]?.ToString();

                        if (type == "updateAuthorizationState")
                        {
                            string state = u["authorization_state"]?["@type"]?.ToString();
                            if (state == "authorizationStateWaitTdlibParameters")
                                TdJson.SendUtf8(c, parameters.ToString(Newtonsoft.Json.Formatting.None));
                            else if (state == "authorizationStateReady")
                                authorized = true;
                            else if (state != null && state.StartsWith("authorizationStateWait"))
                            {
                                // Не авторизованы — логиниться в фоне нельзя.
                                Diag("Cold session: not signed in (" + state + ")");
                                abort = true;
                            }
                        }
                        else if (type == "updateNewChat")
                        {
                            long cid = u["chat"]?["id"]?.ToObject<long>() ?? 0;
                            string t = u["chat"]?["title"]?.ToString();
                            if (cid != 0 && !string.IsNullOrEmpty(t)) titles[cid] = t;
                        }
                        else if (type == "updateNewMessage" && authorized)
                        {
                            var m = u["message"];
                            if (m == null) continue;
                            if (m["is_outgoing"]?.ToObject<bool>() ?? false) continue;
                            long mid = m["id"]?.ToObject<long>() ?? 0;
                            if (mid != 0 && !seen.Add(mid)) continue;

                            long sentAt = m["date"]?.ToObject<long>() ?? 0;
                            if (sentAt > 0 &&
                                DateTimeOffset.UtcNow.ToUnixTimeSeconds() - sentAt > 3600) continue;

                            long chatId = m["chat_id"]?.ToObject<long>() ?? 0;
                            // Каждый запуск задачи — новый клиент TDLib, локальный
                            // HashSet между запусками не живёт. Держим планку в настройках.
                            if (!IsNewerThanLastNotified(chatId, mid)) continue;
                            string title = titles.ContainsKey(chatId) ? titles[chatId] : "Unogram";
                            ShowMessageToast(title, DescribeContent(m["content"]), chatId);
                            RememberLastNotified(chatId, mid);
                            notified++;
                        }
                    }
                });

                Diag("Cold session: authorized=" + authorized + ", toasts=" + notified
                     + (_handoverRequested ? ", handover requested" : ""));

                // Корректное закрытие: close -> ждём authorizationStateClosed -> destroy.
                CloseClient(c);
            }
            catch (Exception ex)
            {
                Diag("Cold session failed: " + ex.Message);
            }
            finally
            {
                try { TdGate.Release(); } catch { }
            }
        }

        private static void CloseClient(IntPtr client)
        {
            try
            {
                TdJson.SendUtf8(client, "{\"@type\":\"close\"}");
                var stop = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < stop)
                {
                    IntPtr res = TdJson.td_json_client_receive(client, 0.5);
                    if (res == IntPtr.Zero) continue;
                    string json = TdJson.IntPtrToStringUtf8(res);
                    if (!string.IsNullOrEmpty(json) && json.Contains("authorizationStateClosed")) break;
                }
                TdJson.td_json_client_destroy(client);
            }
            catch (Exception ex) { Diag("Cold session close failed: " + ex.Message); }
        }

        private static bool IsNewerThanLastNotified(long chatId, long messageId)
        {
            try
            {
                var v = Windows.Storage.ApplicationData.Current.LocalSettings.Values;
                string key = "notified_" + chatId;
                if (!v.ContainsKey(key)) return true;
                return messageId > Convert.ToInt64(v[key]);
            }
            catch { return true; }
        }

        private static void RememberLastNotified(long chatId, long messageId)
        {
            try
            {
                Windows.Storage.ApplicationData.Current.LocalSettings.Values["notified_" + chatId] = messageId;
            }
            catch { }
        }

        private static string DescribeContent(JToken content)
        {
            switch (content?["@type"]?.ToString() ?? "")
            {
                case "messageText":      return content["text"]?["text"]?.ToString() ?? "";
                case "messagePhoto":     return "Фото";
                case "messageVideo":     return "Видео";
                case "messageVoiceNote": return "Голосовое сообщение";
                case "messageVideoNote": return "Видеосообщение";
                case "messageSticker":   return "Стикер";
                case "messageDocument":  return "Файл";
                case "messageAnimation": return "GIF";
                default:                 return "Новое сообщение";
            }
        }

        // ------------------------------------------------------------------
        // Диагностика, переживающая смерть процесса
        // ------------------------------------------------------------------

        /// <summary>
        /// Debug.WriteLine виден только под отладчиком, а под отладчиком
        /// приложение не приостанавливается — то есть ровно в интересующем нас
        /// сценарии лога нет. Поэтому дублируем в LocalSettings и в файл.
        /// </summary>
        public static void Diag(string message)
        {
            Debug.WriteLine("[BG] " + message);
            string line = DateTime.Now.ToString("MM-dd HH:mm:ss") + "  " + message;
            try
            {
                var values = Windows.Storage.ApplicationData.Current.LocalSettings.Values;
                values["bg_last"] = line;
                int n = values.ContainsKey("bg_count") ? (int)values["bg_count"] : 0;
                values["bg_count"] = n + 1;
            }
            catch { }
            AppendDiagFile(line);
        }

        private static async void AppendDiagFile(string line)
        {
            try
            {
                var folder = Windows.Storage.ApplicationData.Current.LocalFolder;
                var file = await folder.CreateFileAsync("bglog.txt",
                    Windows.Storage.CreationCollisionOption.OpenIfExists);
                await Windows.Storage.FileIO.AppendTextAsync(file, line + "\r\n");
            }
            catch { }
        }

        /// <summary>Короткая сводка для показа в UI.</summary>
        public static string GetDiagnosticsSummary()
        {
            try
            {
                var values = Windows.Storage.ApplicationData.Current.LocalSettings.Values;
                string last = values.ContainsKey("bg_last") ? values["bg_last"].ToString() : "нет записей";
                int n = values.ContainsKey("bg_count") ? (int)values["bg_count"] : 0;
                bool registered = false;
                foreach (var t in BackgroundTaskRegistration.AllTasks)
                    if (t.Value.Name == CatchUpTaskName) registered = true;
                return "Задача зарегистрирована: " + (registered ? "да" : "нет")
                     + "\nСобытий: " + n
                     + "\nПоследнее: " + last;
            }
            catch (Exception ex) { return "Диагностика недоступна: " + ex.Message; }
        }

        // ------------------------------------------------------------------
        // Уведомления
        // ------------------------------------------------------------------

        public const string ToastGroup = "unogram";

        public static string ToastTagForChat(long chatId)
        {
            return "c" + (chatId < 0 ? "n" : "") + Math.Abs(chatId).ToString();
        }

        public static void ShowMessageToast(string title, string body, long chatId)
        {
            try
            {
                var xml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
                var texts = xml.GetElementsByTagName("text");
                texts[0].AppendChild(xml.CreateTextNode(string.IsNullOrEmpty(title) ? "Unogram" : title));
                texts[1].AppendChild(xml.CreateTextNode(body ?? ""));
                var toast = new ToastNotification(xml);
                toast.Tag = ToastTagForChat(chatId);
                toast.Group = ToastGroup;
                ToastNotificationManager.CreateToastNotifier().Show(toast);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[BG] Toast failed: " + ex.Message);
            }
        }
    }
}
