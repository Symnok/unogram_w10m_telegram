using System;
using System.Runtime.InteropServices;

namespace TelegramWP10
{
    /// <summary>
    /// Тонкая P/Invoke-обёртка над BASS (bass.dll + аддон bassopus.dll) —
    /// используется только для голосовых заметок Telegram (.oga/.ogg, Opus в
    /// Ogg-контейнере), которые Media Foundation на Windows 10 Mobile нативно
    /// не декодирует. Обычные аудио/видео файлы по-прежнему идут через штатный
    /// Windows.Media.Playback.MediaPlayer — эта обёртка его не заменяет.
    ///
    /// ВАЖНО: сами файлы bass.dll/bassopus.dll в репозитории не лежат — их
    /// нужно вручную скачать с un4seen.com (ARM-сборка под UWP/Windows Store)
    /// и положить рядом с .csproj. Если библиотека отсутствует или не грузится —
    /// все методы тут просто возвращают false/пустое значение, ничего не падает.
    ///
    /// Класс сознательно не потокобезопасен и не поддерживает несколько
    /// одновременных потоков — ровно как и остальной плеер в приложении,
    /// который тоже держит только один активный трек одновременно.
    /// </summary>
    public static class BassPlayer
    {
        // BASS_ChannelIsActive
        private const int BASS_ACTIVE_STOPPED = 0;
        private const int BASS_ACTIVE_PLAYING = 1;
        private const int BASS_ACTIVE_PAUSED = 3;

        private const uint BASS_UNICODE = 0x80000000;
        private const uint BASS_POS_BYTE = 0;

        // На Windows BASS собирается с __stdcall — это не Cdecl, как у libwebp.
        [DllImport("bass.dll", CallingConvention = CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BASS_Init(int device, uint freq, uint flags, IntPtr win, IntPtr clsid);

        [DllImport("bass.dll", CallingConvention = CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BASS_ChannelPlay(uint handle, [MarshalAs(UnmanagedType.Bool)] bool restart);

        [DllImport("bass.dll", CallingConvention = CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BASS_ChannelPause(uint handle);

        [DllImport("bass.dll", CallingConvention = CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BASS_ChannelStop(uint handle);

        [DllImport("bass.dll", CallingConvention = CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BASS_StreamFree(uint handle);

        [DllImport("bass.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern long BASS_ChannelGetPosition(uint handle, uint mode);

        [DllImport("bass.dll", CallingConvention = CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BASS_ChannelSetPosition(uint handle, long pos, uint mode);

        [DllImport("bass.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern long BASS_ChannelGetLength(uint handle, uint mode);

        [DllImport("bass.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern double BASS_ChannelBytes2Seconds(uint handle, long pos);

        [DllImport("bass.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern long BASS_ChannelSeconds2Bytes(uint handle, double pos);

        [DllImport("bass.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern int BASS_ChannelIsActive(uint handle);

        [DllImport("bass.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern int BASS_ErrorGetCode();

        // Прямой вызов в сам bassopus.dll, в обход системы плагинов BASS
        // (BASS_PluginLoad) — та внутри пытается открыть файл классическим,
        // не-UWP-осведомлённым способом и падает в песочнице с FILEOPEN,
        // даже если модуль уже загружен в процесс. А вот загрузка самого
        // bassopus.dll через обычный [DllImport] (как и bass.dll) работает
        // нормально — тем же путём, что уже подтверждён логами.
        [DllImport("bassopus.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern uint BASS_OPUS_StreamCreateFile([MarshalAs(UnmanagedType.Bool)] bool mem,
            [MarshalAs(UnmanagedType.LPWStr)] string file, long offset, long length, uint flags);

        private static bool _initialized = false;
        private static bool _initFailed = false;
        private static uint _currentHandle = 0;

        // ================================================================
        // ВРЕМЕННО — диагностика "голосовые не проигрываются". Отдельный,
        // самодостаточный файл (BassPlayer — статический класс без доступа
        // к логгерам MainPage) — убрать после разбора причины.
        // ================================================================
        private static readonly System.Threading.SemaphoreSlim _bassDebugLock = new System.Threading.SemaphoreSlim(1, 1);
        private static async void LogBassDebug(string message) {
            if (!await _bassDebugLock.WaitAsync(2000)) return;
            try {
                var folder = await Windows.Storage.ApplicationData.Current.LocalFolder
                    .CreateFolderAsync("Unogram", Windows.Storage.CreationCollisionOption.OpenIfExists);
                var file = await folder.CreateFileAsync("bassdebug.txt", Windows.Storage.CreationCollisionOption.OpenIfExists);
                await Windows.Storage.FileIO.AppendTextAsync(file, "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + message + "\r\n");
            } catch { } finally {
                try { _bassDebugLock.Release(); } catch { }
            }
        }

        /// <summary>Инициализирует BASS — один раз за сессию приложения.</summary>
        private static bool EnsureInit() {
            if (_initialized) return true;
            if (_initFailed) return false;
            try {
                // device=-1 — устройство по умолчанию; окно (win) для UWP не нужно.
                bool initOk = BASS_Init(-1, 44100, 0, IntPtr.Zero, IntPtr.Zero);
                LogBassDebug("BASS_Init -> " + initOk + (initOk ? "" : " errCode=" + BASS_ErrorGetCode()));
                if (!initOk) {
                    _initFailed = true;
                    return false;
                }
                _initialized = true;
                return true;
            } catch (Exception ex) {
                _initFailed = true;
                LogBassDebug("EnsureInit EXCEPTION: " + ex.GetType().FullName + ": " + ex.Message + " | stack=" + ex.StackTrace);
                return false;
            }
        }

        /// <summary>Открывает и сразу начинает проигрывать файл. false — если BASS/bassopus.dll/файл недоступны.</summary>
        public static bool Play(string path) {
            LogBassDebug("Play ENTER path=" + path);
            if (!EnsureInit()) {
                LogBassDebug("Play FAIL: EnsureInit()=false");
                return false;
            }
            try {
                Stop();
                uint h = BASS_OPUS_StreamCreateFile(false, path, 0, 0, BASS_UNICODE);
                if (h == 0) {
                    LogBassDebug("BASS_OPUS_StreamCreateFile -> 0 (FAIL) errCode=" + BASS_ErrorGetCode());
                    return false;
                }
                LogBassDebug("BASS_OPUS_StreamCreateFile -> handle=" + h);
                _currentHandle = h;
                bool playOk = BASS_ChannelPlay(h, true);
                LogBassDebug("BASS_ChannelPlay -> " + playOk + (playOk ? "" : " errCode=" + BASS_ErrorGetCode()));
                return playOk;
            } catch (Exception ex) {
                LogBassDebug("Play EXCEPTION: " + ex.GetType().FullName + ": " + ex.Message + " | stack=" + ex.StackTrace);
                _currentHandle = 0;
                return false;
            }
        }

        public static void Pause() {
            try { if (_currentHandle != 0) BASS_ChannelPause(_currentHandle); } catch { }
        }

        public static void Resume() {
            try { if (_currentHandle != 0) BASS_ChannelPlay(_currentHandle, false); } catch { }
        }

        /// <summary>Останавливает и полностью освобождает текущий канал (если есть).</summary>
        public static void Stop() {
            try {
                if (_currentHandle != 0) {
                    BASS_ChannelStop(_currentHandle);
                    BASS_StreamFree(_currentHandle);
                }
            } catch { } finally {
                _currentHandle = 0;
            }
        }

        public static TimeSpan GetPosition() {
            try {
                if (_currentHandle == 0) return TimeSpan.Zero;
                long bytes = BASS_ChannelGetPosition(_currentHandle, BASS_POS_BYTE);
                double sec = BASS_ChannelBytes2Seconds(_currentHandle, bytes);
                return sec > 0 ? TimeSpan.FromSeconds(sec) : TimeSpan.Zero;
            } catch { return TimeSpan.Zero; }
        }

        public static TimeSpan GetLength() {
            try {
                if (_currentHandle == 0) return TimeSpan.Zero;
                long bytes = BASS_ChannelGetLength(_currentHandle, BASS_POS_BYTE);
                double sec = BASS_ChannelBytes2Seconds(_currentHandle, bytes);
                return sec > 0 ? TimeSpan.FromSeconds(sec) : TimeSpan.Zero;
            } catch { return TimeSpan.Zero; }
        }

        public static void Seek(TimeSpan pos) {
            try {
                if (_currentHandle == 0) return;
                long bytes = BASS_ChannelSeconds2Bytes(_currentHandle, pos.TotalSeconds);
                BASS_ChannelSetPosition(_currentHandle, bytes, BASS_POS_BYTE);
            } catch { }
        }

        /// <summary>true, пока канал реально проигрывается (не на паузе и не остановлен).</summary>
        public static bool IsPlaying() {
            try {
                return _currentHandle != 0 && BASS_ChannelIsActive(_currentHandle) == BASS_ACTIVE_PLAYING;
            } catch { return false; }
        }

        /// <summary>true, если канал был открыт, но доиграл до конца сам по себе — пора сбросить UI.</summary>
        public static bool HasEnded() {
            try {
                return _currentHandle != 0 && BASS_ChannelIsActive(_currentHandle) == BASS_ACTIVE_STOPPED;
            } catch { return false; }
        }

        public static bool HasActiveStream => _currentHandle != 0;
    }
}
