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
    /// Background work, within what Windows 10 Mobile actually permits.
    ///
    /// A permanently running process is not achievable on Mobile:
    ///   * ControlChannelTrigger and SocketActivityTrigger both need a
    ///     StreamSocket, and TDLib's socket lives inside native tdjson.dll;
    ///   * extendedExecutionUnconstrained is not honoured on Mobile;
    ///   * PushNotificationTrigger needs a Store-registered package (WNS).
    ///
    /// Three mechanisms are implemented here:
    ///   1. ExtendedExecutionSession — a short suspension grace window so the
    ///      connection survives a quick minimise.
    ///   2. Location-tracking keep-alive — optional, off by default.
    ///   3. TimeTrigger every 15 minutes (single-process) — catch-up and toast.
    /// </summary>
    public sealed class BackgroundService
    {
        public const string CatchUpTaskName = "UnogramCatchUp";
        /// <summary>Increment whenever the task registration changes.</summary>
        private const int RegistrationVersion = 4;

        /// <summary>Held while this process has a TDLib client open.</summary>
        public const string TdSessionMutexName = "Unogram.TdSession";
        private const uint CatchUpIntervalMinutes = 15;

        /// <summary>How long the catch-up task keeps the process awake.</summary>
        private const int CatchUpDrainSeconds = 20;

        private static BackgroundService _instance;
        public static BackgroundService Instance
        {
            get { return _instance ?? (_instance = new BackgroundService()); }
        }

        private BackgroundService() { }

        private ExtendedExecutionSession _session;

        /// <summary>App is on screen. Set from App.</summary>
        public static bool IsInForeground = true;

        /// <summary>
        /// Catch-up is running. A separate flag rather than something inferred
        /// from IsInForeground: activating the background task un-freezes a
        /// suspended process, which may raise Resuming and set IsInForeground
        /// to true at exactly the moment we are in the background.
        /// </summary>
        public static volatile bool IsCatchUpRunning = false;

        // ------------------------------------------------------------------
        // 1. Suspension grace window
        // ------------------------------------------------------------------

        /// <summary>
        /// Requested from App.OnSuspending inside the deferral. The window is
        /// short and not guaranteed — it protects against losing the connection
        /// when switching apps briefly, and is not a background mode.
        /// </summary>
        public async Task<bool> RequestGraceWindowAsync()
        {
            // The keep-alive session already holds the process; no second extension needed.
            if (_keepAliveSession != null)
            {
                Diag("Suspend: keep-alive session active, no grace window needed");
                return true;
            }

            ClearSession();

            if (await TryRequestAsync(ExtendedExecutionReason.Unspecified)) return true;
            // Some Mobile builds grant Unspecified only in the foreground, so on
            // refusal try the reason intended for suspension.
            return await TryRequestAsync(ExtendedExecutionReason.SavingData);
        }

        private async Task<bool> TryRequestAsync(ExtendedExecutionReason reason)
        {
            var session = new ExtendedExecutionSession();
            session.Reason = reason;
            session.Description = Loc.T("session_grace");
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

        /// <summary>Called on resume — the window is no longer needed.</summary>
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
            // Reason == SystemPolicy means the system reclaimed the window early.
            Diag("ExtendedExecution REVOKED: " + args.Reason);
            ClearSession();
        }

        // ------------------------------------------------------------------
        // 1b. Always-on background mode (location tracking)
        // ------------------------------------------------------------------
        //
        // ExtendedExecutionReason.LocationTracking is the only extended
        // execution mode Microsoft documents as continuing to run with the
        // screen locked on Mobile. The session is premised on genuinely
        // tracking position: without a live Geolocator subscription the system
        // may revoke it.
        //
        // The cost is a location permission prompt and noticeably higher
        // battery use, so the mode is off by default.

        private ExtendedExecutionSession _keepAliveSession;
        private Windows.Devices.Geolocation.Geolocator _geolocator;

        private const string KeepAliveSettingKey = "keepalive_enabled";

        /// <summary>User has enabled always-on background mode.</summary>
        public static bool KeepAliveEnabled
        {
            get
            {
                try
                {
                    var v = Windows.Storage.ApplicationData.Current.LocalSettings.Values;
                    return v.ContainsKey(KeepAliveSettingKey) && (bool)v[KeepAliveSettingKey];
                }
                catch { return false; }
            }
            set
            {
                try
                {
                    Windows.Storage.ApplicationData.Current.LocalSettings.Values[KeepAliveSettingKey] = value;
                }
                catch { }
            }
        }

        /// <summary>Whether an extended execution session is currently held.</summary>
        public bool KeepAliveActive { get { return _keepAliveSession != null; } }

        private const string CatchUpEnabledSettingKey = "catchup_enabled";

        /// <summary>
        /// Пользовательская настройка: получать ли уведомления о новых
        /// сообщениях, пока приложение полностью закрыто (через CatchUpTask).
        /// По умолчанию — true, чтобы не менять поведение для тех, кто уже
        /// пользуется приложением и ничего не трогал в настройках.
        /// </summary>
        public static bool CatchUpEnabled
        {
            get
            {
                try
                {
                    var v = Windows.Storage.ApplicationData.Current.LocalSettings.Values;
                    return !v.ContainsKey(CatchUpEnabledSettingKey) || (bool)v[CatchUpEnabledSettingKey];
                }
                catch { return true; }
            }
            set
            {
                try
                {
                    Windows.Storage.ApplicationData.Current.LocalSettings.Values[CatchUpEnabledSettingKey] = value;
                }
                catch { }
            }
        }

        /// <summary>
        /// Returns false if location access was refused on a build without
        /// coarse fallback, or the system denied the session. The caller is
        /// expected to surface that.
        /// </summary>
        public async Task<bool> StartKeepAliveAsync()
        {
            if (_keepAliveSession != null) return true;

            bool coarseAvailable = false;
            try
            {
                // Lightest possible configuration: network and cell towers
                // instead of GPS, infrequent reports, large movement threshold.
                // We do not want the position — only a live subscription that
                // justifies the session.
                _geolocator = new Windows.Devices.Geolocation.Geolocator();
                _geolocator.DesiredAccuracy = Windows.Devices.Geolocation.PositionAccuracy.Default;
                _geolocator.DesiredAccuracyInMeters = 3000;
                _geolocator.MovementThreshold = 1000;      // metres
                _geolocator.ReportInterval = 600000;       // 10 minutes; a hint only

                // Coarse location: positions are obfuscated to at least a 4 km
                // radius and work even when the app-specific location switch is
                // off. We have no use for a precise position at all.
                // The method arrived in 10.0.14393 and our minimum is 10240,
                // so check for it before calling.
                if (Windows.Foundation.Metadata.ApiInformation.IsMethodPresent(
                        "Windows.Devices.Geolocation.Geolocator",
                        "AllowFallbackToConsentlessPositions"))
                {
                    _geolocator.AllowFallbackToConsentlessPositions();
                    coarseAvailable = true;
                }

                _geolocator.PositionChanged += OnPositionChanged;
                _geolocator.StatusChanged += OnGeolocatorStatusChanged;
            }
            catch (Exception ex)
            {
                Diag("KeepAlive: geolocator setup failed: " + ex.Message);
                return false;
            }

            // Ask for permission anyway — the system is more willing to keep
            // the session when it is granted. A refusal is only fatal where
            // coarse positions are unavailable (builds older than 14393).
            Windows.Devices.Geolocation.GeolocationAccessStatus access =
                Windows.Devices.Geolocation.GeolocationAccessStatus.Unspecified;
            try
            {
                access = await Windows.Devices.Geolocation.Geolocator.RequestAccessAsync();
            }
            catch (Exception ex)
            {
                Diag("KeepAlive: RequestAccessAsync failed: " + ex.Message);
            }

            if (access != Windows.Devices.Geolocation.GeolocationAccessStatus.Allowed)
            {
                Diag("KeepAlive: location access " + access
                     + (coarseAvailable ? ", continuing with coarse positions" : ", giving up"));
                if (!coarseAvailable) { StopGeolocator(); return false; }
            }
            else
            {
                Diag("KeepAlive: location access Allowed"
                     + (coarseAvailable ? " (coarse fallback armed)" : ""));
            }

            var session = new ExtendedExecutionSession();
            session.Reason = ExtendedExecutionReason.LocationTracking;
            session.Description = Loc.T("session_keepAlive");
            session.Revoked += OnKeepAliveRevoked;

            try
            {
                var result = await session.RequestExtensionAsync();
                if (result == ExtendedExecutionResult.Allowed)
                {
                    _keepAliveSession = session;
                    Diag("KeepAlive: ALLOWED (location tracking)");
                    return true;
                }
                Diag("KeepAlive: DENIED");
            }
            catch (Exception ex)
            {
                Diag("KeepAlive: RequestExtensionAsync failed: " + ex.Message);
            }

            try { session.Revoked -= OnKeepAliveRevoked; session.Dispose(); } catch { }
            StopGeolocator();
            return false;
        }

        public void StopKeepAlive()
        {
            if (_keepAliveSession != null)
            {
                try
                {
                    _keepAliveSession.Revoked -= OnKeepAliveRevoked;
                    _keepAliveSession.Dispose();
                }
                catch { }
                _keepAliveSession = null;
                Diag("KeepAlive: stopped");
            }
            StopGeolocator();
        }

        private void StopGeolocator()
        {
            if (_geolocator == null) return;
            try
            {
                _geolocator.PositionChanged -= OnPositionChanged;
                _geolocator.StatusChanged -= OnGeolocatorStatusChanged;
            }
            catch { }
            _geolocator = null;
        }

        private void OnPositionChanged(Windows.Devices.Geolocation.Geolocator sender,
                                       Windows.Devices.Geolocation.PositionChangedEventArgs args)
        {
            // The position is unused and never transmitted. The subscription
            // exists solely to keep the LocationTracking session justified.
        }

        private void OnGeolocatorStatusChanged(Windows.Devices.Geolocation.Geolocator sender,
                                               Windows.Devices.Geolocation.StatusChangedEventArgs args)
        {
            if (args.Status == Windows.Devices.Geolocation.PositionStatus.Disabled ||
                args.Status == Windows.Devices.Geolocation.PositionStatus.NotAvailable)
                Diag("KeepAlive: geolocator status " + args.Status);
        }

        private async void OnKeepAliveRevoked(object sender, ExtendedExecutionRevokedEventArgs args)
        {
            Diag("KeepAlive: REVOKED " + args.Reason);
            StopKeepAlive();

            // SystemPolicy usually means resource pressure. Retry once after a
            // minute; if refused again, the TimeTrigger remains as fallback.
            if (args.Reason == ExtendedExecutionRevokedReason.SystemPolicy && KeepAliveEnabled)
            {
                await Task.Delay(TimeSpan.FromMinutes(1));
                if (KeepAliveEnabled && _keepAliveSession == null)
                    await StartKeepAliveAsync();
            }
        }

        // ------------------------------------------------------------------
        // 2. Periodic catch-up
        // ------------------------------------------------------------------

        /// <summary>
        /// Registers a single-process TimeTrigger task. No entry point is set:
        /// activation arrives in App.OnBackgroundActivated, so no separate winmd
        /// project is required.
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
            if (access == BackgroundAccessStatus.DeniedByUser ||
                access == BackgroundAccessStatus.DeniedBySystemPolicy ||
                access == BackgroundAccessStatus.Unspecified)
                return false;

            // The task entry point has changed before (in-process ->
            // out-of-process), BackgroundTaskRegistration does not expose it,
            // and installing over an existing build does not clear the old
            // registration. Track a registration version and re-create the task
            // when it is stale.
            var settings = Windows.Storage.ApplicationData.Current.LocalSettings.Values;
            int registeredVersion = settings.ContainsKey("bg_reg_version")
                ? Convert.ToInt32(settings["bg_reg_version"]) : 0;

            bool exists = false;
            foreach (var t in BackgroundTaskRegistration.AllTasks)
                if (t.Value.Name == CatchUpTaskName) exists = true;

            if (exists && registeredVersion == RegistrationVersion)
            {
                Diag("Catch-up task already registered (v" + registeredVersion + ")");
                return true;
            }
            if (exists)
            {
                UnregisterCatchUpTask();
                Diag("Re-registering catch-up task: v" + registeredVersion + " -> v" + RegistrationVersion);
            }

            try
            {
                var builder = new BackgroundTaskBuilder();
                builder.Name = CatchUpTaskName;
                // Single-process: no TaskEntryPoint, activation arrives in
                // App.OnBackgroundActivated. The out-of-process variant (a
                // separate winmd component) never activated on this device —
                // not one log line across three trigger windows.
                // No SystemCondition: a condition is not evaluated when the
                // trigger fires, it defers the task until the system agrees the
                // condition holds, which on a sleeping phone can be forever.
                builder.SetTrigger(new TimeTrigger(CatchUpIntervalMinutes, false));
                builder.Register();
                settings["bg_reg_version"] = RegistrationVersion;
                Diag("Catch-up task registered in-process, v" + RegistrationVersion
                     + " (" + CatchUpIntervalMinutes + " min)");
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

        /// <summary>Called from App.OnBackgroundActivated.</summary>
        public static async Task RunCatchUpAsync(IBackgroundTaskInstance taskInstance)
        {
            var deferral = taskInstance.GetDeferral();
            bool cancelled = false;
            taskInstance.Canceled += (s, reason) =>
            {
                cancelled = true;
                Debug.WriteLine("[BG] Catch-up cancelled: " + reason);
            };

            IsCatchUpRunning = true;
            try
            {
                LogMemoryBudget("catch-up start");

                if (MainPage.ActiveClient != IntPtr.Zero)
                {
                    // Process is alive: LongPolling() keeps running and TDLib
                    // reconnects on its own. Nudge the network layer and give it
                    // time to drain queued updates — they take the normal path
                    // and raise toasts.
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
                IsCatchUpRunning = false;
                deferral.Complete();
            }
        }

        /// <summary>
        /// Background task memory budget. Tighter on Mobile than on desktop,
        /// and it decides whether TDLib can be started in a cold process.
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
        // Cold-start TDLib session inside the background task
        // ------------------------------------------------------------------

        /// <summary>Seconds spent pumping updates before forcing a close.</summary>
        private const int ColdSessionBudgetSeconds = 25;

        /// <summary>
        /// One TDLib client per database. A single-process background task
        /// lives in the same process as the UI, so session access is
        /// serialised. TDLib holds a lock file and returns an error rather than
        /// corrupting the database on a second client, but the race is still
        /// better avoided.
        /// </summary>
        private static readonly System.Threading.SemaphoreSlim TdGate =
            new System.Threading.SemaphoreSlim(1, 1);

        private static volatile bool _handoverRequested;

        /// <summary>Asks the background session to close; call when coming to the foreground.</summary>
        public static void RequestForegroundHandover()
        {
            _handoverRequested = true;
        }

        /// <summary>Claims the session for the foreground client. Never released.</summary>
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
                LogMemoryBudget("tdjson loaded");

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
                string exitReason = "budget exhausted";
                var deadline = DateTime.UtcNow.AddSeconds(ColdSessionBudgetSeconds);
                var titles = new System.Collections.Generic.Dictionary<long, string>();
                var seen = new System.Collections.Generic.HashSet<long>();

                await Task.Run(() =>
                {
                    while (true)
                    {
                        if (DateTime.UtcNow >= deadline)  { exitReason = "budget exhausted"; break; }
                        if (cancelled())                  { exitReason = "task cancelled"; break; }
                        if (_handoverRequested)           { exitReason = "foreground handover"; break; }
                        if (abort)                        { exitReason = "not signed in"; break; }

                        IntPtr res = TdJson.td_json_client_receive(c, 1.0);
                        if (res == IntPtr.Zero) continue;
                        string json = TdJson.IntPtrToStringUtf8(res);
                        if (string.IsNullOrEmpty(json)) continue;

                        JObject u;
                        try { u = JObject.Parse(json); } catch { continue; }
                        string type = u["@type"]?.ToString();

                        // Otherwise an error from setTdlibParameters is invisible:
                        // it arrives as error, not updateAuthorizationState.
                        if (type == "error")
                        {
                            Diag("Cold session TDLib error: " +
                                 (json.Length > 200 ? json.Substring(0, 200) : json));
                            continue;
                        }

                        if (type == "updateAuthorizationState")
                        {
                            string state = u["authorization_state"]?["@type"]?.ToString();
                            Diag("Cold session state: " + state);
                            if (state == "authorizationStateWaitTdlibParameters")
                                {
                                Diag("Cold session: sending tdlib parameters, db=" + dbPath);
                                TdJson.SendUtf8(c, parameters.ToString(Newtonsoft.Json.Formatting.None));
                            }
                            else if (state == "authorizationStateReady")
                            {
                                if (!authorized) LogMemoryBudget("tdlib ready");
                                authorized = true;
                            }
                            else if (state != null && state.StartsWith("authorizationStateWait"))
                            {
                                // Not signed in; logging in from the background is not possible.
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
                            // Every task run creates a new TDLib client, so a local
                            // HashSet does not survive between runs. Keep the
                            // watermark in settings instead.
                            if (!IsNewerThanLastNotified(chatId, mid)) continue;
                            string title = titles.ContainsKey(chatId) ? titles[chatId] : "Unogram";
                            ShowMessageToast(title, DescribeContent(m["content"]), chatId);
                            RememberLastNotified(chatId, mid);
                            notified++;
                        }
                    }
                });

                Diag("Cold session ended: reason=" + exitReason
                     + ", authorized=" + authorized
                     + ", toasts=" + notified
                     + ", elapsed=" + (int)(ColdSessionBudgetSeconds -
                         Math.Max(0, (deadline - DateTime.UtcNow).TotalSeconds)) + "s");

                // Clean shutdown: close -> await authorizationStateClosed -> destroy.
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
                case "messagePhoto":     return Loc.T("msg_photo");
                case "messageVideo":     return Loc.T("msg_video");
                case "messageVoiceNote": return Loc.T("msg_voice");
                case "messageVideoNote": return Loc.T("msg_videoNote");
                case "messageSticker":   return Loc.T("msg_sticker");
                case "messageDocument":  return Loc.T("msg_file");
                case "messageAnimation": return "GIF";
                default:                 return Loc.T("msg_new");
            }
        }

        // ------------------------------------------------------------------
        // Diagnostics — отключены. Приложение не пишет логи на диск и не
        // хранит диагностические данные в LocalSettings. Debug.WriteLine
        // никуда не сохраняется (виден только под отладчиком), поэтому
        // оставлен как единственный, полностью безобидный след.
        // ------------------------------------------------------------------

        public static void Diag(string message)
        {
            Debug.WriteLine("[BG] " + message);
        }

        // ------------------------------------------------------------------
        // Notifications
        // ------------------------------------------------------------------

        public const string ToastGroup = "unogram";

        public static string ToastTagForChat(long chatId)
        {
            return "c" + (chatId < 0 ? "n" : "") + Math.Abs(chatId).ToString();
        }

        /// <summary>
        /// Whether this message has already been notified. The per-chat
        /// watermark lives in settings, so it survives process restarts and
        /// repeated catch-up runs.
        /// </summary>
        public static bool ShouldNotify(long chatId, long messageId)
        {
            if (messageId == 0) return true;
            try
            {
                var v = Windows.Storage.ApplicationData.Current.LocalSettings.Values;
                string key = "notified_" + chatId;
                if (v.ContainsKey(key) && messageId <= Convert.ToInt64(v[key])) return false;
                v[key] = messageId;
                return true;
            }
            catch { return true; }
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
