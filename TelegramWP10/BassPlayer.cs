using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Streams;

namespace TelegramWP10
{
    /// <summary>
    /// P/Invoke-обёртка над BASS (bass.dll + bassopus.dll) — используется
    /// только для голосовых заметок Telegram (.oga/.ogg, Opus в Ogg-контейнере),
    /// которые Media Foundation на Windows 10 Mobile нативно не декодирует.
    ///
    /// Прошлая попытка (передача BASS пути к файлу) падала жёстко — крэш был
    /// не внутри самого bass.dll, а на границе P/Invoke, и независимое
    /// подтверждение через LoadPackagedLibrary показало, что нативный код
    /// внутри UWP-песочницы не может сам открыть произвольный файл по пути.
    ///
    /// Решение: файл читается штатным, песочнице-совместимым API
    /// (FileIO.ReadBufferAsync), результат копируется в byte[], массив
    /// закрепляется в памяти (GCHandle, Pinned — гарантирует, что GC его не
    /// переместит и не соберёт), и BASS получает не путь, а готовый указатель
    /// на эту закреплённую память (mem=true). Файловая система при
    /// воспроизведении вообще не задействована — только один явный, штатный
    /// UWP-вызов на чтение в самом начале.
    ///
    /// Указатель обязан оставаться закреплённым весь срок жизни потока (BASS
    /// декодирует из буфера постепенно, а не копирует его целиком при
    /// открытии) — поэтому GCHandle освобождается только в Stop(), после
    /// BASS_StreamFree.
    /// </summary>
    public static class BassPlayer
    {
        private const int BASS_ACTIVE_STOPPED = 0;
        private const int BASS_ACTIVE_PLAYING = 1;

        private const uint BASS_POS_BYTE = 0;

        // BASS на Windows собирается с __stdcall, не Cdecl.
        [DllImport("bass.dll", CallingConvention = CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BASS_Init(int device, uint freq, uint flags, IntPtr win, IntPtr clsid);

        [DllImport("bass.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern int BASS_ErrorGetCode();

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

        // Прямой вызов в сам bassopus.dll, в обход системы плагинов BASS
        // (BASS_PluginLoad) — она тоже упиралась в файловый доступ из
        // нативного кода и падала с FILEOPEN. file теперь IntPtr — указатель
        // на закреплённый буфер, а не путь.
        [DllImport("bassopus.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern uint BASS_OPUS_StreamCreateFile([MarshalAs(UnmanagedType.Bool)] bool mem,
            IntPtr file, long offset, long length, uint flags);

        private static bool _initialized = false;
        private static bool _initFailed = false;
        private static uint _currentHandle = 0;

        // Закреплённый буфер текущего трека — должен жить весь срок
        // воспроизведения, освобождается только в Stop().
        private static byte[] _pinnedBuffer = null;
        private static GCHandle _pinnedHandle;
        private static bool _pinnedValid = false;

        private static bool EnsureInit() {
            if (_initialized) return true;
            if (_initFailed) return false;
            try {
                if (!BASS_Init(-1, 44100, 0, IntPtr.Zero, IntPtr.Zero)) {
                    _initFailed = true;
                    return false;
                }
                _initialized = true;
                return true;
            } catch {
                _initFailed = true;
                return false;
            }
        }

        /// <summary>
        /// Читает файл в память (штатным UWP API), закрепляет буфер и
        /// открывает/запускает поток из памяти. false — если BASS/файл
        /// недоступны или чтение не удалось.
        /// </summary>
        public static async Task<bool> PlayAsync(string path) {
            if (!EnsureInit()) return false;
            try {
                Stop(); // освобождает и предыдущий канал, и предыдущий закреплённый буфер

                var file = await StorageFile.GetFileFromPathAsync(path);
                IBuffer buffer = await FileIO.ReadBufferAsync(file);
                byte[] bytes = new byte[buffer.Length];
                using (var reader = DataReader.FromBuffer(buffer)) {
                    reader.ReadBytes(bytes);
                }
                if (bytes.Length == 0) return false;

                _pinnedHandle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                _pinnedValid = true;
                _pinnedBuffer = bytes;

                IntPtr ptr = _pinnedHandle.AddrOfPinnedObject();
                uint h = BASS_OPUS_StreamCreateFile(true, ptr, 0, bytes.Length, 0);
                if (h == 0) {
                    FreePinned();
                    return false;
                }
                _currentHandle = h;
                bool ok = BASS_ChannelPlay(h, true);
                if (!ok) {
                    try { BASS_StreamFree(h); } catch { }
                    _currentHandle = 0;
                    FreePinned();
                }
                return ok;
            } catch {
                FreePinned();
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

        /// <summary>Останавливает и полностью освобождает текущий канал и закреплённый буфер.</summary>
        public static void Stop() {
            try {
                if (_currentHandle != 0) {
                    BASS_ChannelStop(_currentHandle);
                    BASS_StreamFree(_currentHandle);
                }
            } catch { } finally {
                _currentHandle = 0;
                FreePinned();
            }
        }

        private static void FreePinned() {
            try { if (_pinnedValid) _pinnedHandle.Free(); } catch { }
            _pinnedValid = false;
            _pinnedBuffer = null;
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
