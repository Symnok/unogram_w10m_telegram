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
        private static extern uint BASS_StreamCreateFile([MarshalAs(UnmanagedType.Bool)] bool mem,
            [MarshalAs(UnmanagedType.LPWStr)] string file, long offset, long length, uint flags);

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
        private static extern uint BASS_PluginLoad([MarshalAs(UnmanagedType.LPWStr)] string file, uint flags);

        private static bool _initialized = false;
        private static bool _initFailed = false;
        private static bool _opusPluginLoaded = false;
        private static uint _currentHandle = 0;

        /// <summary>Инициализирует BASS и грузит аддон Opus — один раз за сессию приложения.</summary>
        private static bool EnsureInit() {
            if (_initialized) return true;
            if (_initFailed) return false;
            try {
                // device=-1 — устройство по умолчанию; окно (win) для UWP не нужно.
                if (!BASS_Init(-1, 44100, 0, IntPtr.Zero, IntPtr.Zero)) {
                    _initFailed = true;
                    return false;
                }
                // Без этого аддона основной bass.dll не раскодирует Opus-контент
                // (сам по себе умеет только Ogg/Vorbis) — voice-note файлы Telegram
                // именно Opus. Проверяем результат явно — если аддона нет рядом,
                // дальше даже не пытаемся звать StreamCreateFile для .oga/.ogg.
                try {
                    uint pluginHandle = BASS_PluginLoad("bassopus.dll", 0);
                    _opusPluginLoaded = pluginHandle != 0;
                } catch { _opusPluginLoaded = false; }
                _initialized = true;
                return true;
            } catch {
                _initFailed = true;
                return false;
            }
        }

        /// <summary>Открывает и сразу начинает проигрывать файл. false — если BASS/аддон Opus/файл недоступны.</summary>
        public static bool Play(string path) {
            if (!EnsureInit()) return false;
            if (!_opusPluginLoaded) return false; // без аддона .oga/.ogg (Opus) всё равно не откроется
            try {
                Stop();
                uint h = BASS_StreamCreateFile(false, path, 0, 0, BASS_UNICODE);
                if (h == 0) return false;
                _currentHandle = h;
                return BASS_ChannelPlay(h, true);
            } catch {
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
