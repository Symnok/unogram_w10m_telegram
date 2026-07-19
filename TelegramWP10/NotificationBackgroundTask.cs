using System;
using System.Threading.Tasks;
using Windows.ApplicationModel.Background;
using Windows.UI.Notifications;
using Windows.Data.Xml.Dom;
using Windows.Storage;

namespace TelegramWP10
{
    public sealed class NotificationBackgroundTask : IBackgroundTask
    {
        private BackgroundTaskDeferral _deferral;
        private IntPtr _client;
        private bool _authorized = false;
        private int _maxWaitMs = 20000;
        private StorageFile _logFile;

        public async void Run(IBackgroundTaskInstance taskInstance)
        {
            _deferral = taskInstance.GetDeferral();
            taskInstance.Canceled += OnCanceled;

            try {
                await InitLog();
                await Log("BG START trigger=" + taskInstance.TriggerDetails?.GetType().Name);
                await RunAsync();
                await Log("BG DONE");
            } catch (Exception ex) {
                await Log("BG CRASH: " + ex.Message);
            }
            finally {
                _deferral.Complete();
            }
        }

        private async Task InitLog() {
            try {
                var folder = Windows.Storage.ApplicationData.Current.LocalFolder;
                var appFolder = await folder.GetFolderAsync("Unogram");
                string name = "bg_log_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt";
                _logFile = await appFolder.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);
            } catch { }
        }

        private async Task Log(string msg) {
            try {
                string line = "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + msg + "\r\n";
                await FileIO.AppendTextAsync(_logFile, line);
            } catch { }
        }

        private void OnCanceled(IBackgroundTaskInstance sender, BackgroundTaskCancellationReason reason) {
            var t = Log("BG CANCELED reason=" + reason.ToString());
            try {
                if (_client != IntPtr.Zero) // TdJson destroy not available
            } catch { }
            _deferral?.Complete();
        }

        private async Task RunAsync() {
            var settings = Windows.Storage.ApplicationData.Current.LocalSettings;
            string dbPath = settings.Values["bg_db_path"] as string;
            string filesPath = settings.Values["bg_files_path"] as string;

            await Log("BG db_path=" + (dbPath ?? "NULL"));
            await Log("BG files_path=" + (filesPath ?? "NULL"));

            if (string.IsNullOrEmpty(dbPath)) {
                await Log("BG ABORT: db_path is empty");
                return;
            }

            _client = TdJson.td_json_client_create();
            await Log("BG TDLib client created ptr=" + _client);

            if (_client == IntPtr.Zero) {
                await Log("BG ABORT: TDLib client is zero");
                return;
            }

            string initJson = "{\"@type\":\"setTdlibParameters\"" +
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
                ",\"enable_storage_optimizer\":true}";

            TdJson.SendUtf8(_client, initJson);
            await Log("BG setTdlibParameters sent");

            int elapsed = 0;
            int pollInterval = 200;
            int updateCount = 0;
            int newMsgCount = 0;

            while (elapsed < _maxWaitMs) {
                IntPtr ptr = TdJson.td_json_client_receive(_client, 0.5);
                if (ptr != IntPtr.Zero) {
                    string json = TdJson.IntPtrToStringUtf8(ptr);
                    if (!string.IsNullOrEmpty(json)) {
                        updateCount++;
                        await Log("BG UPDATE #" + updateCount + ": " + json.Substring(0, Math.Min(json.Length, 200)));
                        bool wasMsg = await ProcessUpdate(json);
                        if (wasMsg) newMsgCount++;
                    }
                }
                await Task.Delay(pollInterval);
                elapsed += pollInterval;
            }

            await Log("BG LOOP END elapsed=" + elapsed + "ms updates=" + updateCount + " newMsgs=" + newMsgCount);

            try {
                TdJson.SendUtf8(_client, "{\"@type\":\"close\"}");
                await Log("BG close sent");
                await Task.Delay(500);
                // TdJson destroy not available
                await Log("BG TDLib destroyed");
            } catch (Exception ex) {
                await Log("BG destroy ERR: " + ex.Message);
            }
        }

        private async Task<bool> ProcessUpdate(string json) {
            try {
                if (json.Contains("\"updateAuthorizationState\"")) {
                    if (json.Contains("authorizationStateReady")) {
                        _authorized = true;
                        await Log("BG AUTH: ready");
                    } else if (json.Contains("authorizationStateClosed")) {
                        await Log("BG AUTH: closed — exiting");
                        _maxWaitMs = 0;
                    } else {
                        int idx = json.IndexOf("\"@type\":", json.IndexOf("authorizationState"));
                        await Log("BG AUTH: state=" + json.Substring(Math.Max(0, idx), Math.Min(50, json.Length - idx)));
                    }
                    return false;
                }

                if (!_authorized) return false;

                if (json.Contains("\"updateNewMessage\"")) {
                    if (json.Contains("\"is_outgoing\":true")) {
                        await Log("BG MSG: outgoing skipped");
                        return false;
                    }
                    string senderName = ExtractSenderName(json);
                    string messageText = ExtractMessageText(json);
                    await Log("BG MSG: from=" + senderName + " text=" + messageText);
                    ShowToast(senderName, messageText);
                    return true;
                }
            } catch (Exception ex) {
                await Log("BG ProcessUpdate ERR: " + ex.Message);
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
                if (idx >= 0) { idx += 8; int end = json.IndexOf("\"", idx); if (end > idx) { string t = json.Substring(idx, end - idx); return t.Length > 100 ? t.Substring(0, 100) + "..." : t; } }
                if (json.Contains("messagePhoto")) return "📷 Фото";
                if (json.Contains("messageVideo")) return "🎥 Видео";
                if (json.Contains("messageVoiceNote")) return "🎤 Голосовое";
                if (json.Contains("messageVideoNote")) return "⏺ Видеосообщение";
                if (json.Contains("messageSticker")) return "Стикер";
                if (json.Contains("messageDocument")) return "📄 Документ";
                if (json.Contains("messageAudio")) return "🎵 Аудио";
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
