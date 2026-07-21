using System;
using System.Threading.Tasks;
using Windows.ApplicationModel.Background;
using Windows.UI.Notifications;
using Windows.Data.Xml.Dom;

namespace TelegramWP10
{
    public sealed class NotificationBackgroundTask : IBackgroundTask
    {
        // Статический метод для ручного запуска из UI
        public static async Task RunManual() {
            var instance = new NotificationBackgroundTask();
            await instance.RunAsync();
        }
        private BackgroundTaskDeferral _deferral;
        private IntPtr _client;
        private bool _authorized = false;
        private int _maxWaitMs = 20000;

        public async void Run(IBackgroundTaskInstance taskInstance)
        {
            _deferral = taskInstance.GetDeferral();
            taskInstance.Canceled += OnCanceled;

            try {
                BgLog("BG START trigger=" + taskInstance.TriggerDetails?.GetType().Name
                    + " time=" + DateTime.Now.ToString("HH:mm:ss"));
                await RunAsync();
                BgLog("BG DONE time=" + DateTime.Now.ToString("HH:mm:ss"));
            } catch (Exception ex) {
                BgLog("BG CRASH: " + ex.Message);
            }
            finally {
                _deferral.Complete();
            }
        }

        // Лог пишем в LocalSettings — гарантированно работает без файловой системы
        private void BgLog(string msg) {
            try {
                var settings = Windows.Storage.ApplicationData.Current.LocalSettings;
                string existing = settings.Values["bg_log"] as string ?? "";
                string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg + "\n";
                string updated = existing + line;
                // Храним последние 50 строк
                var lines = updated.Split('\n');
                if (lines.Length > 50)
                    updated = string.Join("\n", lines, lines.Length - 50, 50);
                settings.Values["bg_log"] = updated;
            } catch { }
        }

        private void OnCanceled(IBackgroundTaskInstance sender, BackgroundTaskCancellationReason reason) {
            BgLog("BG CANCELED reason=" + reason.ToString());
            _deferral?.Complete();
        }

        private async Task RunAsync() {
            var settings = Windows.Storage.ApplicationData.Current.LocalSettings;
            string dbPath = settings.Values["bg_db_path"] as string;
            string filesPath = settings.Values["bg_files_path"] as string;

            BgLog("BG db_path=" + (dbPath ?? "NULL"));

            if (string.IsNullOrEmpty(dbPath)) {
                BgLog("BG ABORT: db_path empty");
                return;
            }

            _client = TdJson.td_json_client_create();
            BgLog("BG client=" + _client);

            if (_client == IntPtr.Zero) {
                BgLog("BG ABORT: client zero");
                return;
            }

            TdJson.SendUtf8(_client, "{\"@type\":\"setTdlibParameters\"" +
                ",\"use_test_dc\":false" +
                ",\"database_directory\":\"" + EscapeJson(dbPath) + "\"" +
                ",\"files_directory\":\"" + EscapeJson(filesPath ?? dbPath + "_files") + "\"" +
                ",\"use_file_database\":true" +
                ",\"use_chat_info_database\":true" +
                ",\"use_message_database\":true" +
                ",\"use_secret_chats\":false" +
                ",\"api_id\":26688287" +
                ",\"api_hash\":\"5f4afe72bc71dc6ec40f7dcb0c9a822b\"" +
                ",\"system_language_code\":\"ru\"" +
                ",\"device_model\":\"Windows Phone\"" +
                ",\"application_version\":\"1.0\"" +
                ",\"enable_storage_optimizer\":true}");

            BgLog("BG params sent");

            int elapsed = 0;
            int updateCount = 0;
            int newMsgCount = 0;
            // Сначала быстро ждём авторизации (до 10 сек)
            int authWait = 0;
            while (!_authorized && authWait < 10000) {
                IntPtr ptr = TdJson.td_json_client_receive(_client, 0.1);
                if (ptr != IntPtr.Zero) {
                    string json = TdJson.IntPtrToStringUtf8(ptr);
                    if (!string.IsNullOrEmpty(json)) {
                        updateCount++;
                        BgLog("AUTH_UPD #" + updateCount + ": " + json.Substring(0, Math.Min(json.Length, 120)));
                        ProcessUpdate(json);
                    }
                }
                authWait += 100;
                if (_maxWaitMs == 0) break;
            }
            BgLog("BG auth_wait=" + authWait + " authorized=" + _authorized);
            if (!_authorized) {
                BgLog("BG ABORT: not authorized after " + authWait + "ms");
                return;
            }
            // Теперь ждём новые сообщения (оставшееся время)
            while (elapsed < _maxWaitMs) {
                IntPtr ptr = TdJson.td_json_client_receive(_client, 0.5);
                if (ptr != IntPtr.Zero) {
                    string json = TdJson.IntPtrToStringUtf8(ptr);
                    if (!string.IsNullOrEmpty(json)) {
                        updateCount++;
                        BgLog("UPD #" + updateCount + ": " + json.Substring(0, Math.Min(json.Length, 120)));
                        bool wasMsg = ProcessUpdate(json);
                        if (wasMsg) newMsgCount++;
                    }
                }
                await Task.Delay(200);
                elapsed += 200;
                if (_maxWaitMs == 0) break;
            }

            BgLog("BG END elapsed=" + elapsed + " updates=" + updateCount + " msgs=" + newMsgCount);

            try {
                TdJson.SendUtf8(_client, "{\"@type\":\"close\"}");
                await Task.Delay(500);
            } catch { }
        }

        private bool ProcessUpdate(string json) {
            try {
                if (json.Contains("\"updateAuthorizationState\"")) {
                    if (json.Contains("authorizationStateReady")) {
                        _authorized = true;
                        BgLog("BG AUTH: ready");
                    } else if (json.Contains("authorizationStateClosed")) {
                        BgLog("BG AUTH: closed");
                        _maxWaitMs = 0;
                    }
                    return false;
                }

                if (!_authorized) return false;

                if (json.Contains("\"updateNewMessage\"")) {
                    if (json.Contains("\"is_outgoing\":true")) return false;
                    string senderName = ExtractSenderName(json);
                    string messageText = ExtractMessageText(json);
                    BgLog("BG MSG from=" + senderName + " text=" + messageText);
                    ShowToast(senderName, messageText);
                    return true;
                }
            } catch (Exception ex) {
                BgLog("BG ProcessUpdate ERR: " + ex.Message);
            }
            return false;
        }

        private string ExtractSenderName(string json) {
            try {
                int idx = json.IndexOf("\"first_name\":\"");
                if (idx >= 0) { idx += 14; int end = json.IndexOf("\"", idx); if (end > idx) return json.Substring(idx, end - idx); }
                idx = json.IndexOf("\"title\":\"");
                if (idx >= 0) { idx += 9; int end = json.IndexOf("\"", idx); if (end > idx) return json.Substring(idx, end - idx); }
            } catch { }
            return "Новое сообщение";
        }

        private string ExtractMessageText(string json) {
            try {
                int idx = json.IndexOf("\"text\":\"");
                if (idx >= 0) { idx += 8; int end = json.IndexOf("\"", idx); if (end > idx) { string t = json.Substring(idx, end - idx); return t.Length > 80 ? t.Substring(0, 80) + "..." : t; } }
                if (json.Contains("messagePhoto")) return "📷 Фото";
                if (json.Contains("messageVideo")) return "🎥 Видео";
                if (json.Contains("messageVoiceNote")) return "🎤 Голосовое";
                if (json.Contains("messageVideoNote")) return "⏺ Видеосообщение";
                if (json.Contains("messageSticker")) return "Стикер";
                if (json.Contains("messageDocument")) return "📄 Документ";
            } catch { }
            return "Сообщение";
        }

        private void ShowToast(string title, string message) {
            try {
                string xml = "<toast><visual><binding template='ToastGeneric'>" +
                    "<text>" + EscapeXml(title) + "</text>" +
                    "<text>" + EscapeXml(message) + "</text>" +
                    "</binding></visual></toast>";
                var doc = new XmlDocument();
                doc.LoadXml(xml);
                ToastNotificationManager.CreateToastNotifier().Show(new ToastNotification(doc));
            } catch { }
        }

        private string EscapeJson(string s) => s?.Replace("\\", "\\\\").Replace("\"", "\\\"") ?? "";
        private string EscapeXml(string s) => s?.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;") ?? "";
    }
}
