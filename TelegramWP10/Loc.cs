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
            { "diag_registered", Row("Task registered: ", "Задача зарегистрирована: ", "Завдання зареєстровано: ", "משימה רשומה: ") },
            { "diag_yes", Row("yes", "да", "так", "כן") },
            { "diag_no", Row("no", "нет", "ні", "לא") },
            { "diag_events", Row("Events: ", "Событий: ", "Подій: ", "אירועים: ") },
            { "diag_last", Row("Last: ", "Последнее: ", "Останнє: ", "אחרון: ") },
            { "diag_none", Row("no entries", "нет записей", "немає записів", "אין רשומות") },
            { "diag_unavailable", Row("Diagnostics unavailable: ", "Диагностика недоступна: ", "Діагностика недоступна: ", "אבחון לא זמין: ") },

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
            { "msgmenu_unpin", Row("📌 Unpin", "📌 Открепить", "📌 Відкріпити", "📌 בטל נעיצה") },
            { "msgmenu_delete", Row("🗑 Delete message", "🗑 Удалить сообщение", "🗑 Видалити повідомлення", "🗑 מחק הודעה") },
            { "msgmenu_deleteAll", Row("🗑 Delete for everyone", "🗑 Удалить у всех", "🗑 Видалити в усіх", "🗑 מחק אצל כולם") },
            { "msgmenu_mention", Row("👤 Open mention", "👤 Открыть упоминание", "👤 Відкрити згадку", "👤 פתח אזכור") },
            { "msgmenu_save", Row("💾 Save", "💾 Сохранить", "💾 Зберегти", "💾 שמור") },
            { "toast_saved", Row("Saved", "Сохранено", "Збережено", "נשמר") },
            { "toast_save_failed", Row("Save failed", "Не удалось сохранить", "Не вдалося зберегти", "השמירה נכשלה") },
            { "chatmenu_archive", Row("📁 Move to archive", "📁 Переместить в архив", "📁 Перемістити в архів", "📁 העבר לארכיון") },
            { "chatmenu_unarchive", Row("📤 Move from archive", "📤 Переместить из архива", "📤 Перемістити з архіву", "📤 העבר מהארכיון") },
            { "chatmenu_unread", Row("🔵 Mark as unread", "🔵 Отметить непрочитанным", "🔵 Позначити непрочитаним", "🔵 סמן כלא נקרא") },
            { "chatmenu_read", Row("✅ Mark as read", "✅ Отметить прочитанным", "✅ Позначити прочитаним", "✅ סמן כנקרא") },
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

            // ---- login status ----
            { "login_enterPhone", Row("Enter phone number", "Введите номер телефона", "Введіть номер телефону", "הזן מספר טלפון") },
            { "login_codeSent", Row("Code sent. Check Telegram or SMS.", "Код отправлен. Проверьте Telegram или SMS.", "Код надіслано. Перевірте Telegram або SMS.", "הקוד נשלח. בדוק ב-Telegram או ב-SMS.") },
            { "login_enter2fa", Row("Enter 2FA password", "Введите пароль 2FA", "Введіть пароль 2FA", "הזן סיסמת אימות דו-שלבי") },
            { "login_errorPrefix", Row("Error: ", "Ошибка: ", "Помилка: ", "שגיאה: ") },
            { "login_sendingPhone", Row("Sending number...", "Отправка номера...", "Надсилання номера...", "שולח מספר...") },
            { "login_checkingCode", Row("Checking code...", "Проверка кода...", "Перевірка коду...", "בודק קוד...") },
            { "login_checkingPassword", Row("Checking password...", "Проверка пароля...", "Перевірка пароля...", "בודק סיסמה...") },
            { "login_fillMtproto", Row("Fill in all MTProto fields", "Заполните все поля MTProto", "Заповніть усі поля MTProto", "מלא את כל שדות MTProto") },
            { "login_fillHttp", Row("Fill in all HTTP fields", "Заполните все поля HTTP", "Заповніть усі поля HTTP", "מלא את כל שדות HTTP") },
            { "login_fillSocks", Row("Fill in all SOCKS5 fields", "Заполните все поля SOCKS5", "Заповніть усі поля SOCKS5", "מלא את כל שדות SOCKS5") },
            { "login_wrongPort", Row("Invalid port", "Неверный порт", "Невірний порт", "פורט לא תקין") },

            // ---- dialogs ----
            { "err_storage", Row("Storage error:\n", "Ошибка хранилища:\n", "Помилка сховища:\n", "שגיאת אחסון:\n") },
            { "dlg_deleteChat_body", Row("Delete conversation? This can't be undone.", "Удалить переписку? Это действие нельзя отменить.", "Видалити листування? Цю дію не можна скасувати.", "למחוק את השיחה? לא ניתן לבטל פעולה זו.") },
            { "dlg_deleteChat_title", Row("Delete conversation", "Удалить переписку", "Видалити листування", "מחיקת שיחה") },
            { "btn_delete", Row("Delete", "Удалить", "Видалити", "מחק") },
            { "dlg_clearCache_body", Row("Delete all downloaded photos, videos and audio from cache?", "Удалить все скачанные фото, видео и аудио из кэша?", "Видалити всі завантажені фото, відео та аудіо з кешу?", "למחוק את כל התמונות, הסרטונים והשמע שהורדו מהמטמון?") },
            { "dlg_clearCache_title", Row("Clear cache", "Очистить кэш", "Очистити кеш", "נקה מטמון") },
            { "btn_clear", Row("Clear", "Очистить", "Очистити", "נקה") },
            { "dlg_cacheCleared_body", Row("Cache cleared.", "Кэш очищен.", "Кеш очищено.", "המטמון נוקה.") },
            { "dlg_done_title", Row("Done", "Готово", "Готово", "בוצע") },
            { "dlg_logout_body", Row("Log out of your account?", "Выйти из аккаунта?", "Вийти з облікового запису?", "להתנתק מהחשבון?") },
            { "dlg_logout_title", Row("Log out", "Выход", "Вихід", "התנתקות") },
            { "btn_logout", Row("Log out", "Выйти", "Вийти", "התנתק") },

            // ---- shared media type labels (bare words — emoji prefixed in code) ----
            { "media_photo", Row("Photo", "Фото", "Фото", "תמונה") },
            { "media_video", Row("Video", "Видео", "Відео", "וידאו") },
            { "media_document", Row("Document", "Документ", "Документ", "מסמך") },
            { "media_file", Row("File", "Файл", "Файл", "קובץ") },
            { "media_audio", Row("Audio", "Аудио", "Аудіо", "שמע") },
            { "media_voice", Row("Voice", "Голосовое", "Голосове", "קול") },
            { "media_voiceMessage", Row("Voice message", "Голосовое сообщение", "Голосове повідомлення", "הודעה קולית") },
            { "media_videoMessage", Row("Video message", "Видеосообщение", "Відеоповідомлення", "הודעת וידאו") },
            { "media_sticker", Row("Sticker", "Стикер", "Стікер", "מדבקה") },
            { "media_poll", Row("Poll", "Опрос", "Опитування", "סקר") },
            { "media_call", Row("Call", "Звонок", "Дзвінок", "שיחה") },
            { "media_message", Row("Message", "Сообщение", "Повідомлення", "הודעה") },
            { "poll_quiz", Row("Quiz", "Викторина", "Вікторина", "חידון") },
            { "poll_anonymous", Row("Anonymous poll", "Анонимный опрос", "Анонімне опитування", "סקר אנונימי") },

            // ---- service / event messages in chat-list preview ----
            { "svc_pinnedMessageEvent", Row("Pinned a message", "Закреплено сообщение", "Закріплено повідомлення", "הודעה ננעצה") },
            { "svc_pinnedBySuffix", Row("pinned a message", "закрепил(а) сообщение", "закріпив(ла) повідомлення", "נעץ/ה הודעה") },
            { "svc_memberAdded", Row("Member added", "Добавлен участник", "Учасника додано", "חבר נוסף") },
            { "svc_addedSuffix", Row("added", "добавил(а)", "додав(ла)", "הוסיף/ה") },
            { "svc_joinedByLink", Row("Joined via link", "Присоединился по ссылке", "Приєднався за посиланням", "הצטרף בקישור") },
            { "svc_memberLeft", Row("Member left", "Участник вышел", "Учасник вийшов", "חבר עזב") },
            { "svc_titleChanged", Row("Title changed", "Название изменено", "Назву змінено", "הכותרת שונתה") },
            { "svc_photoChanged", Row("Photo changed", "Фото изменено", "Фото змінено", "התמונה שונתה") },
            { "svc_contactRegistered", Row("Joined Telegram", "Зарегистрировался в Telegram", "Зареєструвався в Telegram", "הצטרף ל-Telegram") },
            { "svc_location", Row("Location", "Геолокация", "Геолокація", "מיקום") },
            { "svc_contact", Row("Contact", "Контакт", "Контакт", "איש קשר") },
            { "label_unknownUser", Row("User", "Пользователь", "Користувач", "משתמש") },
            { "label_hiddenUser", Row("Hidden user", "Скрытый пользователь", "Прихований користувач", "משתמש מוסתר") },
            { "label_chat", Row("Chat", "Чат", "Чат", "צ'אט") },
            { "label_channel", Row("Channel", "Канал", "Канал", "ערוץ") },
            { "label_you", Row("⭐ You", "⭐ Вы", "⭐ Ви", "⭐ אתה") },
            { "label_comments", Row("Comments", "Комментарии", "Коментарі", "תגובות") },

            // ---- call log ----
            { "call_missed", Row("Missed call", "Пропущенный звонок", "Пропущений дзвінок", "שיחה שלא נענתה") },
            { "call_declined", Row("Declined call", "Отклонённый звонок", "Відхилений дзвінок", "שיחה שנדחתה") },
            { "call_outgoing", Row("Outgoing", "Исходящий", "Вихідний", "יוצאת") },
            { "call_incoming", Row("Incoming", "Входящий", "Вхідний", "נכנסת") },

            // ---- connection state ----
            { "conn_connecting", Row("connecting...", "подключение...", "з'єднання...", "מתחבר...") },
            { "conn_connectingProxy", Row("connecting to proxy...", "подключение к прокси...", "з'єднання з проксі...", "מתחבר לפרוקסי...") },
            { "conn_updating", Row("updating...", "обновление...", "оновлення...", "מעדכן...") },
            { "conn_noNetwork", Row("· no network", "· нет сети", "· немає мережі", "· אין רשת") },

            // ---- chat header / typing / members ----
            { "status_typing", Row("typing...", "печатает...", "друкує...", "מקליד...") },
            { "label_members", Row(" members", " участников", " учасників", " חברים") },
            { "label_subscribers", Row(" subscribers", " подписчиков", " підписників", " מנויים") },
            { "status_loading", Row("loading...", "загрузка...", "завантаження...", "טוען...") },

            // ---- online / last-seen status ----
            { "hdr_online", Row("online", "в сети", "в мережі", "מחובר") },
            { "hdr_offline", Row("offline", "не в сети", "не в мережі", "לא מחובר") },
            { "hdr_wasSeenPrefix", Row("last seen ", "был(а) ", "був(ла) ", "נראה לאחרונה ") },
            { "hdr_recently", Row("recently", "недавно", "нещодавно", "לאחרונה") },
            { "hdr_lastWeek", Row("within a week", "на этой неделе", "цього тижня", "השבוע") },
            { "hdr_lastMonth", Row("within a month", "в этом месяце", "цього місяця", "החודש") },
            { "ls_longAgo", Row("a long time ago", "давно не был(а)", "давно не був(ла)", "מזמן לא נראה") },
            { "ls_todayAt", Row("today at ", "был(а) сегодня в ", "був(ла) сьогодні о ", "נראה לאחרונה היום ב-") },
            { "ls_yesterdayAt", Row("yesterday at ", "был(а) вчера в ", "був(ла) вчора о ", "נראה לאחרונה אתמול ב-") },
            { "lastseen_today", Row("today at ", "сегодня в ", "сьогодні о ", "היום ב-") },
            { "lastseen_yesterday", Row("yesterday at ", "вчера в ", "вчора о ", "אתמול ב-") },
            { "just_now", Row("just now", "только что", "щойно", "הרגע") },
            { "minutes_ago", Row(" min. ago", " мин. назад", " хв. тому", " דק' לפני") },

            // ---- date separators ----
            { "date_today", Row("Today", "Сегодня", "Сьогодні", "היום") },
            { "date_yesterday", Row("Yesterday", "Вчера", "Вчора", "אתמול") },
            { "date_dayBeforeYesterday", Row("The day before yesterday", "Позавчера", "Позавчора", "שלשום") },
            { "chat_newMessages", Row("New messages", "Новые сообщения", "Нові повідомлення", "הודעות חדשות") },

            // ---- search headers / folders / archive ----
            { "search_chats", Row("Chats", "Чаты", "Чати", "צ'אטים") },
            { "search_messages", Row("Messages", "Сообщения", "Повідомлення", "הודעות") },
            { "folder_all", Row("All", "Все", "Усі", "הכל") },
            { "archive_empty", Row("archive is empty", "архив пуст", "архів порожній", "הארכיון ריק") },
            { "archive_count", Row("chats: ", "чатов: ", "чатів: ", "צ'אטים: ") },

            // ---- misc status text ----
            { "status_open", Row("📂 Open", "📂 Открыть", "📂 Відкрити", "📂 פתח") },
            { "status_loadingEllipsis", Row("⏳ Loading...", "⏳ Загрузка...", "⏳ Завантаження...", "⏳ טוען...") },
            { "status_loadingFullSize", Row("Loading full size...", "Загрузка полного размера...", "Завантаження повного розміру...", "טוען בגודל מלא...") },
            { "unit_bytes", Row("B", "Б", "Б", "בייט") },
            { "unit_kb", Row("KB", "КБ", "КБ", "ק\"ב") },
            { "unit_mb", Row("MB", "МБ", "МБ", "מ\"ב") },
            { "unit_gb", Row("GB", "ГБ", "ГБ", "ג\"ב") },
            { "unit_sec", Row("sec", "сек", "сек", "שנ'") },
            { "proxy_status_none", Row("No proxy", "Без прокси", "Без проксі", "ללא פרוקסי") },
            { "label_folder", Row("Folder", "Папка", "Папка", "תיקייה") },
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
