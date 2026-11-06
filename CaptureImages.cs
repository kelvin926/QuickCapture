using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace QuickCapture
{
    internal static class DesktopImage
    {
        public static Bitmap Capture(Rectangle bounds, Screen[] screens)
        {
            var image = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
            try
            {
                using (Graphics graphics = Graphics.FromImage(image))
                {
                    graphics.Clear(Color.Black);
                    IntPtr source = GetDC(IntPtr.Zero);
                    if (source == IntPtr.Zero)
                        throw new InvalidOperationException("화면에 접근할 수 없습니다.");

                    try
                    {
                        IntPtr target = graphics.GetHdc();
                        try
                        {
                            const uint sourceCopyWithLayeredWindows = 0x40cc0020;
                            foreach (Screen screen in screens)
                            {
                                Rectangle area = screen.Bounds;
                                if (!BitBlt(target, area.X - bounds.X, area.Y - bounds.Y, area.Width,
                                    area.Height, source, area.X, area.Y, sourceCopyWithLayeredWindows))
                                    throw new Win32Exception();
                            }
                        }
                        finally { graphics.ReleaseHdc(target); }
                    }
                    finally { ReleaseDC(IntPtr.Zero, source); }
                }
                return image;
            }
            catch
            {
                image.Dispose();
                throw;
            }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr window);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr window, IntPtr dc);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height,
            IntPtr source, int sourceX, int sourceY, uint operation);
    }

    internal static class CaptureFiles
    {
        public static byte[] EncodePng(Bitmap image)
        {
            using (var stream = new MemoryStream())
            {
                image.Save(stream, ImageFormat.Png);
                return stream.ToArray();
            }
        }

        public static void SavePng(string folder, byte[] png)
        {
            Directory.CreateDirectory(folder);
            string name = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff",
                System.Globalization.CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string destination = Path.Combine(folder, name + ".png");
            string temporary = destination + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, png);
                File.Move(temporary, destination);
            }
            finally
            {
                File.Delete(temporary);
            }
        }
    }
}
