// Loc.cs — UI string localization.
//
// Deliberately a plain dictionary rather than .resw resources: retrofitting
// x:Uid across MainPage.xaml would touch every element, and the language has to
// be switchable at runtime without restarting, which resw does not do well.
//
// Adding a language: add the code to SupportedLanguages, then add its column to
// every entry in Strings. A missing key falls back to English, so a partial
// translation degrades gracefully instead of showing an empty label.

using System;
using System.Collections.Generic;

namespace TelegramWP10
{
    public static class Loc
    {
        public const string SettingKey = "ui_language";

        /// <summary>Language codes in the order the menu shows them.</summary>
        public static readonly string[] SupportedLanguages = { "en", "ru", "uk", "he" };

        public static string DisplayName(string code)
        {
            switch (code)
            {
                case "en": return "English";
                case "ru": return "Русский";
                case "uk": return "Українська";
                case "he": return "עברית";
                default:   return code;
            }
        }

        /// <summary>Hebrew reads right to left; the page FlowDirection follows this.</summary>
        public static bool IsRightToLeft(string code)
        {
            return code == "he";
        }

        private static string _language;

        /// <summary>
        /// Current language. Defaults to the system language when it is one we
        /// support, otherwise English.
        /// </summary>
        public static string Language
        {
            get
            {
                if (_language != null) return _language;
                try
                {
                    var v = Windows.Storage.ApplicationData.Current.LocalSettings.Values;
                    if (v.ContainsKey(SettingKey))
                    {
                        _language = v[SettingKey] as string;
                        if (IsSupported(_language)) return _language;
                    }
                }
                catch { }

                try
                {
                    string sys = Windows.System.UserProfile.GlobalizationPreferences
                        .Languages[0].Substring(0, 2).ToLowerInvariant();
                    if (IsSupported(sys)) { _language = sys; return _language; }
                }
                catch { }

                _language = "en";
                return _language;
            }
            set
            {
                if (!IsSupported(value)) return;
                _language = value;
                try
                {
                    Windows.Storage.ApplicationData.Current.LocalSettings.Values[SettingKey] = value;
                }
                catch { }
                LocProvider.Current.Refresh();   // re-evaluate every XAML binding
            }
        }

        private static bool IsSupported(string code)
        {
            if (string.IsNullOrEmpty(code)) return false;
            foreach (var c in SupportedLanguages) if (c == code) return true;
            return false;
        }

        /// <summary>Translated string for a key, falling back to English then to the key itself.</summary>
        public static string T(string key)
        {
            Dictionary<string, string> row;
            if (!Strings.TryGetValue(key, out row)) return key;

            string value;
            if (row.TryGetValue(Language, out value) && !string.IsNullOrEmpty(value)) return value;
            if (row.TryGetValue("en", out value) && !string.IsNullOrEmpty(value)) return value;
            return key;
        }

        private static Dictionary<string, string> Row(string en, string ru, string uk, string he)
        {
            return new Dictionary<string, string> {
                { "en", en }, { "ru", ru }, { "uk", uk }, { "he", he }
            };
        }

        private static readonly Dictionary<string, Dictionary<string, string>> Strings =
            new Dictionary<string, Dictionary<string, string>>
        {
            // ---- settings menu ----
            { "menu_favorites", Row("⭐ Saved Messages", "⭐ Избранное", "⭐ Збережене", "⭐ הודעות שמורות") },
            { "menu_clearCache", Row("🗑 Clear cache", "🗑 Очистить кэш", "🗑 Очистити кеш", "🗑 נקה מטמון") },
            { "menu_sound_on", Row("🔔 Sound: On", "🔔 Звук: Вкл", "🔔 Звук: Увімк", "🔔 צליל: פועל") },
            { "menu_sound_off", Row("🔕 Sound: Off", "🔕 Звук: Выкл", "🔕 Звук: Вимк", "🔕 צליל: כבוי") },
            { "menu_keepAlive_on", Row("📍 Background mode: On", "📍 Фоновый режим: Вкл", "📍 Фоновий режим: Увімк", "📍 מצב רקע: פועל") },
            { "menu_keepAlive_off", Row("📍 Background mode: Off", "📍 Фоновый режим: Выкл", "📍 Фоновий режим: Вимк", "📍 מצב רקע: כבוי") },
            { "menu_bgDiag", Row("🐞 Background diagnostics", "🐞 Фоновая диагностика", "🐞 Фонова діагностика", "🐞 אבחון רקע") },
            { "menu_language", Row("🌐 Language", "🌐 Язык", "🌐 Мова", "🌐 שפה") },
            { "menu_logout", Row("🚪 Log out", "🚪 Выход", "🚪 Вихід", "🚪 התנתקות") },

            // ---- common buttons ----
            { "btn_cancel", Row("Cancel", "Отмена", "Скасувати", "ביטול") },
            { "btn_close", Row("Close", "Закрыть", "Закрити", "סגור") },
            { "btn_enable", Row("Enable", "Включить", "Увімкнути", "הפעל") },
            { "btn_copy", Row("Copy", "Копировать", "Копіювати", "העתק") },

            // ---- background mode ----
            { "keepAlive_title", Row(
                "Background mode",
                "Фоновый режим",
                "Фоновий режим",
                "מצב רקע") },
            { "keepAlive_body", Row(
                "Messages will arrive immediately instead of waiting for the 15-minute check.\n\n" +
                "Windows 10 Mobile requires location permission for this — it is the only way to stop the system freezing the app. Your position is not read and never leaves the device.\n\n" +
                "Battery use will increase noticeably.",
                "Приложение будет получать сообщения сразу, не дожидаясь проверки раз в 15 минут.\n\n" +
                "Для этого Windows 10 Mobile требует разрешение на доступ к геопозиции — это единственный способ не дать системе заморозить приложение. Позиция не считывается и никуда не передаётся.\n\n" +
                "Расход батареи заметно вырастет.",
                "Застосунок отримуватиме повідомлення одразу, не чекаючи перевірки раз на 15 хвилин.\n\n" +
                "Для цього Windows 10 Mobile вимагає дозвіл на доступ до геопозиції — це єдиний спосіб не дати системі заморозити застосунок. Позиція не зчитується й нікуди не передається.\n\n" +
                "Витрата батареї помітно зросте.",
                "ההודעות יגיעו מיד במקום להמתין לבדיקה כל 15 דקות.\n\n" +
                "לשם כך Windows 10 Mobile דורש הרשאת מיקום — זו הדרך היחידה למנוע מהמערכת להקפיא את היישום. המיקום אינו נקרא ואינו נשלח לשום מקום.\n\n" +
                "צריכת הסוללה תגדל באופן מורגש.") },
            { "keepAlive_failed", Row(
                "Could not enable background mode. Check that location access is allowed in system settings and that the app is on the battery saver exception list.",
                "Не удалось включить фоновый режим. Проверьте, разрешён ли доступ к геопозиции в настройках системы, и что приложение добавлено в исключения экономии заряда.",
                "Не вдалося увімкнути фоновий режим. Перевірте, чи дозволено доступ до геопозиції в налаштуваннях системи та чи додано застосунок до винятків економії заряду.",
                "לא ניתן להפעיל מצב רקע. ודא שגישה למיקום מאושרת בהגדרות המערכת ושהיישום נמצא ברשימת החריגים של חוסך הסוללה.") },

            // ---- diagnostics ----
            { "diag_title", Row("Background diagnostics", "Фоновая диагностика", "Фонова діагностика", "אבחון רקע") },
            { "diag_registered", Row("Task registered: ", "Задача зарегистрирована: ", "Завдання зареєстровано: ", "משימה רשומה: ") },
            { "diag_yes", Row("yes", "да", "так", "כן") },
            { "diag_no", Row("no", "нет", "ні", "לא") },
            { "diag_events", Row("Events: ", "Событий: ", "Подій: ", "אירועים: ") },
            { "diag_last", Row("Last: ", "Последнее: ", "Останнє: ", "אחרון: ") },
            { "diag_none", Row("no entries", "нет записей", "немає записів", "אין רשומות") },
            { "diag_unavailable", Row("Diagnostics unavailable: ", "Диагностика недоступна: ", "Діагностика недоступна: ", "אבחון לא זמין: ") },
            { "diag_noLog", Row("(no bglog.txt yet)", "(bglog.txt пока нет)", "(bglog.txt ще немає)", "(עדיין אין bglog.txt)") },

            // ---- message previews in notifications ----
            { "msg_photo", Row("Photo", "Фото", "Фото", "תמונה") },
            { "msg_video", Row("Video", "Видео", "Відео", "וידאו") },
            { "msg_voice", Row("Voice message", "Голосовое сообщение", "Голосове повідомлення", "הודעה קולית") },
            { "msg_videoNote", Row("Video message", "Видеосообщение", "Відеоповідомлення", "הודעת וידאו") },
            { "msg_sticker", Row("Sticker", "Стикер", "Стікер", "מדבקה") },
            { "msg_file", Row("File", "Файл", "Файл", "קובץ") },
            { "msg_gif", Row("GIF", "GIF", "GIF", "GIF") },
            { "msg_new", Row("New message", "Новое сообщение", "Нове повідомлення", "הודעה חדשה") },


            // ---- main UI ----
            { "ui_loading", Row("Loading...", "Загрузка...", "Завантаження...", "טוען...") },
            { "ui_close", Row("Close", "Закрыть", "Закрити", "סגור") },
            { "ui_cancel", Row("Cancel", "Отмена", "Скасувати", "ביטול") },
            { "ui_next", Row("Next", "Далее", "Далі", "הבא") },
            { "ui_confirm", Row("Confirm", "Подтвердить", "Підтвердити", "אישור") },
            { "ui_apply", Row("Apply", "Применить", "Застосувати", "החל") },
            { "ui_signIn", Row("Sign in", "Войти", "Увійти", "התחבר") },
            { "ui_search", Row("Search", "Поиск", "Пошук", "חיפוש") },
            { "ui_start", Row("▶ Start", "▶ Старт", "▶ Старт", "▶ התחל") },
            { "ui_pasteText", Row("Paste text", "Вставить текст", "Вставити текст", "הדבק טקסט") },
            { "chat_archive", Row("Archive", "Архив", "Архів", "ארכיון") },
            { "chat_archived", Row("archived chats", "архивированные чаты", "архівовані чати", "צ'אטים בארכיון") },
            { "chat_chats", Row("Chats: ", "Чатов: ", "Чатів: ", "צ'אטים: ") },
            { "chat_chat", Row("Chat", "Чат", "Чат", "צ'אט") },
            { "chat_members", Row("Members", "Участники", "Учасники", "משתתפים") },
            { "chat_contacts", Row("Contacts", "Контакты", "Контакти", "אנשי קשר") },
            { "chat_message", Row("Message...", "Сообщение...", "Повідомлення...", "הודעה...") },
            { "chat_forwardTo", Row("Forward to chat", "Переслать в чат", "Переслати в чат", "העבר לצ'אט") },
            { "chat_forwardToDots", Row("Forward to...", "Переслать в...", "Переслати в...", "העבר אל...") },
            { "chat_releaseToSend", Row("Release to send", "Отпустите для отправки", "Відпустіть для надсилання", "שחרר לשליחה") },
            { "chat_pinnedMessage", Row("📌 Pinned message", "📌 Закреплённое сообщение", "📌 Закріплене повідомлення", "📌 הודעה נעוצה") },
            { "msgmenu_reply", Row("↩ Reply", "↩ Ответить", "↩ Відповісти", "↩ השב") },
            { "msgmenu_forward", Row("↪ Forward", "↪ Переслать", "↪ Переслати", "↪ העבר") },
            { "msgmenu_edit", Row("✏ Edit", "✏ Редактировать", "✏ Редагувати", "✏ ערוך") },
            { "msgmenu_copy", Row("📋 Copy text", "📋 Копировать текст", "📋 Копіювати текст", "📋 העתק טקסט") },
            { "msgmenu_pin", Row("📌 Pin", "📌 Закрепить", "📌 Закріпити", "📌 נעץ") },
            { "msgmenu_delete", Row("🗑 Delete message", "🗑 Удалить сообщение", "🗑 Видалити повідомлення", "🗑 מחק הודעה") },
            { "msgmenu_deleteAll", Row("🗑 Delete for everyone", "🗑 Удалить у всех", "🗑 Видалити в усіх", "🗑 מחק אצל כולם") },
            { "msgmenu_mention", Row("👤 Open mention", "👤 Открыть упоминание", "👤 Відкрити згадку", "👤 פתח אזכור") },
            { "chatmenu_archive", Row("📁 Move to archive", "📁 Переместить в архив", "📁 Перемістити в архів", "📁 העבר לארכיון") },
            { "chatmenu_unread", Row("🔵 Mark as unread", "🔵 Отметить непрочитанным", "🔵 Позначити непрочитаним", "🔵 סמן כלא נקרא") },
            { "chatmenu_deleteChat", Row("🗑 Delete conversation", "🗑 Удалить переписку", "🗑 Видалити листування", "🗑 מחק שיחה") },
            { "react_heart", Row("❤ Heart", "❤ Сердце", "❤ Серце", "❤ לב") },
            { "react_like", Row("👍 Like", "👍 Нравится", "👍 Подобається", "👍 אהבתי") },
            { "react_dislike", Row("👎 Dislike", "👎 Не нравится", "👎 Не подобається", "👎 לא אהבתי") },
            { "react_fire", Row("🔥 Fire", "🔥 Огонь", "🔥 Вогонь", "🔥 אש") },
            { "react_laugh", Row("🤣 Laugh", "🤣 Смех", "🤣 Сміх", "🤣 צחוק") },
            { "react_sad", Row("😢 Sad", "😢 Грусть", "😢 Сум", "😢 עצוב") },
            { "attach_file", Row("📎  File", "📎  Файл", "📎  Файл", "📎  קובץ") },
            { "attach_videoNote", Row("⏺  Video message", "⏺  Видеосообщение", "⏺  Відеоповідомлення", "⏺  הודעת וידאו") },
            { "login_phone", Row("Phone", "Телефон", "Телефон", "טלפון") },
            { "login_code", Row("Code from Unogram", "Код из Unogram", "Код з Unogram", "קוד מ-Unogram") },
            { "login_password2fa", Row("2FA password", "Пароль 2FA", "Пароль 2FA", "סיסמת אימות דו-שלבי") },
            { "login_about", Row("About", "О себе", "Про себе", "אודות") },
            { "proxy_title", Row("Proxy", "Прокси", "Проксі", "פרוקסי") },
            { "proxy_settings", Row("Proxy settings", "Настройки прокси", "Налаштування проксі", "הגדרות פרוקסי") },
            { "proxy_none", Row("No proxy (direct connection)", "Без прокси (прямое подключение)", "Без проксі (пряме підключення)", "ללא פרוקסי (חיבור ישיר)") },
            { "proxy_auto", Row("Auto-select proxy from server", "Автовыбор прокси с сервера", "Автовибір проксі із сервера", "בחירת פרוקסי אוטומטית מהשרת") },
            { "proxy_host", Row("Host / IP", "Хост / IP", "Хост / IP", "מארח / IP") },
            { "proxy_port", Row("Port", "Порт", "Порт", "פורט") },
            { "proxy_login", Row("Login (optional)", "Логин (опционально)", "Логін (необов'язково)", "שם משתמש (אופציונלי)") },
            { "proxy_password", Row("Password (optional)", "Пароль (опционально)", "Пароль (необов'язково)", "סיסמה (אופציונלי)") },
            { "proxy_secret", Row("Secret", "Секрет", "Секрет", "סוד") },
            { "err_outOfMemory", Row("⚠ Out of memory", "⚠ Оперативная память закончилась", "⚠ Оперативна пам'ять вичерпана", "⚠ הזיכרון אזל") },
            // ---- extended execution descriptions ----
            { "session_grace", Row("Finishing Telegram updates", "Дочитываем обновления Telegram", "Завершуємо оновлення Telegram", "משלים עדכוני Telegram") },
            { "session_keepAlive", Row("Unogram stays connected", "Unogram остаётся на связи", "Unogram залишається на зв'язку", "Unogram נשאר מחובר") },
        };
    }

    /// <summary>
    /// Exposed to XAML as a StaticResource so markup can bind to a key:
    ///
    ///     Text="{Binding [menu.pin], Source={StaticResource Loc}}"
    ///
    /// An indexer binding is used rather than x:Name plus code-behind because
    /// much of the localized text lives inside DataTemplates, where named
    /// elements are not reachable from the page. Refresh() raises a change for
    /// the indexer, which makes every binding re-read its value.
    /// </summary>
    public class LocProvider : System.ComponentModel.INotifyPropertyChanged
    {
        private static LocProvider _current;
        public static LocProvider Current
        {
            get { return _current ?? (_current = new LocProvider()); }
        }

        public LocProvider() { _current = this; }

        public string this[string key] { get { return Loc.T(key); } }

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;

        public void Refresh()
        {
            var handler = PropertyChanged;
            if (handler != null)
                handler(this, new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
        }
    }
}
