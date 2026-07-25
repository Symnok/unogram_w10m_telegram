// UnogramBackground/CatchUpTask.cs
// Фоновая задача, запускается по TimeTrigger раз в 15 минут.
// Отдельный процесс: XAML-приложение не загружается, поэтому весь бюджет
// памяти фоновой задачи достаётся TDLib, а не оболочке.
// Самодостаточна — ничего не линкуется из основного проекта.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Windows.ApplicationModel.Background;
using Windows.Data.Xml.Dom;
using Windows.Storage;
using Windows.UI.Notifications;

namespace UnogramBackground
{
    public sealed class CatchUpTask : IBackgroundTask
    {
        // ------------------------------------------------------------------
        // TDLib
        // ------------------------------------------------------------------

        [DllImport("tdjson.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr td_json_client_create();

        [DllImport("tdjson.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void td_json_client_send(IntPtr client, IntPtr request);

        [DllImport("tdjson.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr td_json_client_receive(IntPtr client, double timeout);

        [DllImport("tdjson.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void td_json_client_destroy(IntPtr client);

        private static void Send(IntPtr client, string request)
        {
            if (string.IsNullOrEmpty(request)) return;
            byte[] bytes = Encoding.UTF8.GetBytes(request + "\0");
            IntPtr ptr = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, ptr, bytes.Length);
                td_json_client_send(client, ptr);
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }

        private static string ReadUtf8(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero) return null;
            int len = 0;
            while (Marshal.ReadByte(ptr, len) != 0) len++;
            if (len == 0) return string.Empty;
            byte[] buffer = new byte[len];
            Marshal.Copy(ptr, buffer, 0, len);
            return Encoding.UTF8.GetString(buffer);
        }

        // ------------------------------------------------------------------
        // Константы, общие с приложением
        // ------------------------------------------------------------------

        // Приложение держит этот мьютекс, пока открыт его клиент TDLib.
        // Значение продублировано намеренно: компонент самодостаточен, а WinRT
        // не позволяет публичные константы. Должно совпадать с
        // BackgroundService.TdSessionMutexName в основном проекте.
        private const string TdSessionMutexName = "Unogram.TdSession";

        private const string LogFolderName = "Unogram";
        private const string LogFileName = "bglog.txt";
        private const string ToastGroup = "unogram";

        private const int SessionBudgetSeconds = 40;
        private const int MaxMessageAgeSeconds = 3600;

        private BackgroundTaskDeferral _deferral;
        private volatile bool _cancelled;

        // ------------------------------------------------------------------

        public async void Run(IBackgroundTaskInstance taskInstance)
        {
            _deferral = taskInstance.GetDeferral();
            taskInstance.Canceled += (s, reason) =>
            {
                _cancelled = true;
                Diag("Task cancelled: " + reason);
            };

            try
            {
                LogMemory("task start");

                // Приложение открыто — база занята им, отходим в сторону.
                Mutex mutex = null;
                bool held = false;
                try
                {
                    mutex = new Mutex(false, TdSessionMutexName);
                    try { held = mutex.WaitOne(0); }
                    catch (AbandonedMutexException) { held = true; }  // владелец умер

                    if (!held)
                    {
                        Diag("Skipped: app holds the TDLib session");
                        return;
                    }

                    await RunSessionAsync();
                }
                finally
                {
                    if (mutex != null)
                    {
                        if (held) { try { mutex.ReleaseMutex(); } catch { } }
                        mutex.Dispose();
                    }
                }

                LogMemory("task end");
            }
            catch (Exception ex)
            {
                Diag("Task failed: " + ex.Message);
            }
            finally
            {
                _deferral.Complete();
            }
        }

        private async Task RunSessionAsync()
        {
            IntPtr client = IntPtr.Zero;
            int notified = 0;
            bool authorized = false;
            string exitReason = "budget exhausted";

            try
            {
                var appFolder = await ApplicationData.Current.LocalFolder
                    .CreateFolderAsync(LogFolderName, CreationCollisionOption.OpenIfExists);
                string dbPath = appFolder.Path.Replace("\\", "/") + "/td_db";
                var filesFolder = await appFolder.CreateFolderAsync("td_db_files",
                    CreationCollisionOption.OpenIfExists);

                client = td_json_client_create();
                if (client == IntPtr.Zero) { Diag("client create failed"); return; }
                LogMemory("tdjson loaded");

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
                var deadline = DateTime.UtcNow.AddSeconds(SessionBudgetSeconds);
                var titles = new Dictionary<long, string>();
                var seen = new HashSet<long>();
                bool abort = false;

                await Task.Run(() =>
                {
                    while (true)
                    {
                        if (DateTime.UtcNow >= deadline) { exitReason = "budget exhausted"; break; }
                        if (_cancelled) { exitReason = "task cancelled"; break; }
                        if (abort) { exitReason = "not signed in"; break; }

                        IntPtr res = td_json_client_receive(c, 1.0);
                        if (res == IntPtr.Zero) continue;
                        string json = ReadUtf8(res);
                        if (string.IsNullOrEmpty(json)) continue;

                        JObject u;
                        try { u = JObject.Parse(json); } catch { continue; }
                        string type = u["@type"]?.ToString();

                        if (type == "error")
                        {
                            Diag("TDLib error: " + (json.Length > 200 ? json.Substring(0, 200) : json));
                            continue;
                        }

                        if (type == "updateAuthorizationState")
                        {
                            string state = u["authorization_state"]?["@type"]?.ToString();
                            Diag("state: " + state);
                            if (state == "authorizationStateWaitTdlibParameters")
                                Send(c, parameters.ToString(Newtonsoft.Json.Formatting.None));
                            else if (state == "authorizationStateReady")
                            {
                                if (!authorized) LogMemory("tdlib ready");
                                authorized = true;
                            }
                            else if (state != null && state.StartsWith("authorizationStateWait"))
                                abort = true;
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
                                DateTimeOffset.UtcNow.ToUnixTimeSeconds() - sentAt > MaxMessageAgeSeconds)
                                continue;

                            long chatId = m["chat_id"]?.ToObject<long>() ?? 0;
                            if (!IsNewerThanLastNotified(chatId, mid)) continue;

                            string title = titles.ContainsKey(chatId) ? titles[chatId] : "Unogram";
                            ShowToast(title, Describe(m["content"]), chatId);
                            RememberLastNotified(chatId, mid);
                            notified++;
                        }
                    }
                });

                Diag("session ended: reason=" + exitReason
                     + ", authorized=" + authorized + ", toasts=" + notified);

                CloseClient(c);
            }
            catch (Exception ex)
            {
                Diag("session failed: " + ex.Message);
            }
        }

        /// <summary>close -> ждём authorizationStateClosed -> destroy. Иначе база остаётся грязной.</summary>
        private void CloseClient(IntPtr client)
        {
            try
            {
                Send(client, "{\"@type\":\"close\"}");
                var stop = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < stop)
                {
                    IntPtr res = td_json_client_receive(client, 0.5);
                    if (res == IntPtr.Zero) continue;
                    string json = ReadUtf8(res);
                    if (!string.IsNullOrEmpty(json) && json.Contains("authorizationStateClosed")) break;
                }
                td_json_client_destroy(client);
            }
            catch (Exception ex) { Diag("close failed: " + ex.Message); }
        }

        private static string Describe(JToken content)
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

        private static void ShowToast(string title, string body, long chatId)
        {
            try
            {
                var xml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
                var texts = xml.GetElementsByTagName("text");
                texts[0].AppendChild(xml.CreateTextNode(string.IsNullOrEmpty(title) ? "Unogram" : title));
                texts[1].AppendChild(xml.CreateTextNode(body ?? ""));
                var toast = new ToastNotification(xml);
                toast.Tag = "c" + (chatId < 0 ? "n" : "") + Math.Abs(chatId).ToString();
                toast.Group = ToastGroup;
                ToastNotificationManager.CreateToastNotifier().Show(toast);
            }
            catch { }
        }

        private static bool IsNewerThanLastNotified(long chatId, long messageId)
        {
            try
            {
                var v = ApplicationData.Current.LocalSettings.Values;
                string key = "notified_" + chatId;
                if (!v.ContainsKey(key)) return true;
                return messageId > Convert.ToInt64(v[key]);
            }
            catch { return true; }
        }

        private static void RememberLastNotified(long chatId, long messageId)
        {
            try { ApplicationData.Current.LocalSettings.Values["notified_" + chatId] = messageId; }
            catch { }
        }

        private static void LogMemory(string stage)
        {
            try
            {
                ulong limit = Windows.System.MemoryManager.AppMemoryUsageLimit;
                ulong used = Windows.System.MemoryManager.AppMemoryUsage;
                Diag(string.Format("Memory ({0}): limit={1} KB, used={2} KB, free={3} KB",
                    stage, limit / 1024, used / 1024, limit > used ? (limit - used) / 1024 : 0));
            }
            catch { }
        }

        private static readonly SemaphoreSlim DiagLock = new SemaphoreSlim(1, 1);

        private static void Diag(string message)
        {
            System.Diagnostics.Debug.WriteLine("[BGTASK] " + message);
            string line = DateTime.Now.ToString("MM-dd HH:mm:ss") + "  [task] " + message;
            try
            {
                var values = ApplicationData.Current.LocalSettings.Values;
                values["bg_last"] = line;
                int n = values.ContainsKey("bg_count") ? (int)values["bg_count"] : 0;
                values["bg_count"] = n + 1;
            }
            catch { }
            AppendFile(line);
        }

        private static async void AppendFile(string line)
        {
            if (!await DiagLock.WaitAsync(2000)) return;
            try
            {
                var folder = await ApplicationData.Current.LocalFolder
                    .CreateFolderAsync(LogFolderName, CreationCollisionOption.OpenIfExists);
                var file = await folder.CreateFileAsync(LogFileName, CreationCollisionOption.OpenIfExists);
                await FileIO.AppendTextAsync(file, line + "\r\n");
            }
            catch { }
            finally { try { DiagLock.Release(); } catch { } }
        }
    }
}
