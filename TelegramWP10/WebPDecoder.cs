using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace TelegramWP10
{
    public static class WebPDecoder
    {
        // libwebp.dll — декодирование WebP в RGBA
        [DllImport("libwebp.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr WebPDecodeRGBA(
            IntPtr data, UIntPtr dataSize,
            out int width, out int height);

        // libwebp.dll — освобождение памяти
        [DllImport("libwebp.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void WebPFree(IntPtr ptr);

        /// <summary>
        /// Декодирует WebP из байтов в WriteableBitmap
        /// </summary>
        public static async Task<WriteableBitmap> DecodeAsync(byte[] webpData)
        {
            return await Task.Run(() =>
            {
                IntPtr dataPtr = IntPtr.Zero;
                IntPtr rgbaPtr = IntPtr.Zero;
                try
                {
                    // Копируем данные в неуправляемую память
                    dataPtr = Marshal.AllocHGlobal(webpData.Length);
                    Marshal.Copy(webpData, 0, dataPtr, webpData.Length);

                    // Декодируем WebP → RGBA
                    rgbaPtr = WebPDecodeRGBA(dataPtr, (UIntPtr)webpData.Length,
                                             out int width, out int height);
                    if (rgbaPtr == IntPtr.Zero)
                        throw new Exception("WebPDecodeRGBA returned null");

                    int pixelCount = width * height;
                    int byteCount  = pixelCount * 4; // RGBA

                    // Конвертируем RGBA → BGRA (формат WriteableBitmap)
                    byte[] bgra = new byte[byteCount];
                    for (int i = 0; i < pixelCount; i++)
                    {
                        int src = i * 4;
                        bgra[src + 0] = Marshal.ReadByte(rgbaPtr, src + 2); // B
                        bgra[src + 1] = Marshal.ReadByte(rgbaPtr, src + 1); // G
                        bgra[src + 2] = Marshal.ReadByte(rgbaPtr, src + 0); // R
                        bgra[src + 3] = Marshal.ReadByte(rgbaPtr, src + 3); // A
                    }

                    return new _BitmapData { Width = width, Height = height, Bgra = bgra };
                }
                finally
                {
                    if (dataPtr != IntPtr.Zero) Marshal.FreeHGlobal(dataPtr);
                    if (rgbaPtr != IntPtr.Zero) WebPFree(rgbaPtr);
                }
            }).ContinueWith(async task =>
            {
                var bd = task.Result;
                // WriteableBitmap должен создаваться на UI потоке
                var wb = new WriteableBitmap(bd.Width, bd.Height);
                using (var stream = wb.PixelBuffer.AsStream())
                    await stream.WriteAsync(bd.Bgra, 0, bd.Bgra.Length);
                wb.Invalidate();
                return wb;
            }, TaskScheduler.FromCurrentSynchronizationContext()).Unwrap();
        }

        private class _BitmapData
        {
            public int Width, Height;
            public byte[] Bgra;
        }
    }
}
