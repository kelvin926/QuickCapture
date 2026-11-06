using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using System.Threading;

namespace QuickCapture
{
    internal sealed class ClipboardImage
    {
        private const uint DibFormat = 8;
        private const int BmpFileHeaderSize = 14;
        private const int RetryMilliseconds = 1400;
        private readonly IntPtr owner;
        private readonly uint pngFormat = RegisterClipboardFormat("PNG");
        private readonly object copyGate = new object();

        public ClipboardImage(IntPtr owner)
        {
            this.owner = owner;
        }

        public static uint SequenceNumber()
        {
            return GetClipboardSequenceNumber();
        }

        public bool TryCopy(Bitmap image, byte[] png, uint expectedSequence, Func<bool> isLatest)
        {
            if (!isLatest())
                return false;

            IntPtr dib = IntPtr.Zero;
            IntPtr pngData = IntPtr.Zero;
            try
            {
                using (var bmp = new MemoryStream())
                {
                    if (image.PixelFormat == PixelFormat.Format24bppRgb)
                        image.Save(bmp, ImageFormat.Bmp);
                    else
                    {
                        using (var flat = new Bitmap(image.Width, image.Height, PixelFormat.Format24bppRgb))
                        {
                            using (Graphics g = Graphics.FromImage(flat)) { g.Clear(Color.White); g.DrawImageUnscaled(image, 0, 0); }
                            flat.Save(bmp, ImageFormat.Bmp);
                        }
                    }
                    dib = Allocate(bmp.GetBuffer(), BmpFileHeaderSize, (int)bmp.Length - BmpFileHeaderSize);
                }
                if (pngFormat != 0)
                    pngData = Allocate(png, 0, png.Length);

                return Write(expectedSequence, isLatest, delegate
                {
                    bool bitmapSet = Publish(DibFormat, ref dib);
                    bool pngSet = pngData != IntPtr.Zero && Publish(pngFormat, ref pngData);
                    return bitmapSet || pngSet;
                });
            }
            finally
            {
                if (dib != IntPtr.Zero)
                    GlobalFree(dib);
                if (pngData != IntPtr.Zero)
                    GlobalFree(pngData);
            }
        }

        public bool TryCopyText(string text, uint sequence, Func<bool> isLatest)
        {
            return CopyData(13, Encoding.Unicode.GetBytes(text + "\0"), sequence, isLatest);
        }

        public bool TryCopyFile(string path, uint sequence, Func<bool> isLatest)
        {
            byte[] name = Encoding.Unicode.GetBytes(path + "\0\0");
            byte[] data = new byte[20 + name.Length];
            data[0] = 20;
            data[16] = 1;
            Buffer.BlockCopy(name, 0, data, 20, name.Length);
            if (!string.Equals(Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase))
                return CopyData(15, data, sequence, isLatest);
            byte[] gif = File.ReadAllBytes(path);
            IntPtr files = Allocate(data, 0, data.Length);
            IntPtr animation = IntPtr.Zero;
            try
            {
                uint gifType = RegisterClipboardFormat("GIF");
                if (gifType != 0) animation = Allocate(gif, 0, gif.Length);
                return Write(sequence, isLatest, delegate
                {
                    bool fileSet = Publish(15, ref files);
                    bool gifSet = animation != IntPtr.Zero && Publish(gifType, ref animation);
                    return fileSet || gifSet;
                });
            }
            finally
            {
                if (files != IntPtr.Zero) GlobalFree(files);
                if (animation != IntPtr.Zero) GlobalFree(animation);
            }
        }

        private bool CopyData(uint format, byte[] data, uint sequence, Func<bool> isLatest)
        {
            IntPtr handle = Allocate(data, 0, data.Length);
            try { return Write(sequence, isLatest, () => Publish(format, ref handle)); }
            finally { if (handle != IntPtr.Zero) GlobalFree(handle); }
        }

        private bool Write(uint expectedSequence, Func<bool> isLatest, Func<bool> publish)
        {
                var time = Stopwatch.StartNew();
                if (!Monitor.TryEnter(copyGate, RetryMilliseconds))
                    return false;
                try
                {
                    while (isLatest())
                    {
                        if (OpenClipboard(owner))
                        {
                            try
                            {
                                if (!isLatest() || (SequenceNumber() != expectedSequence && GetClipboardOwner() != owner))
                                    return false;

                                if (!EmptyClipboard())
                                    return false;

                                return publish();
                            }
                            finally { CloseClipboard(); }
                        }

                        if (time.ElapsedMilliseconds >= RetryMilliseconds)
                            break;
                        Thread.Sleep(50);
                    }
                    return false;
                }
                finally { Monitor.Exit(copyGate); }
        }

        private static IntPtr Allocate(byte[] bytes, int start, int length)
        {
            const uint movable = 2;
            IntPtr memory = GlobalAlloc(movable, new UIntPtr((uint)length));
            if (memory == IntPtr.Zero)
                throw new OutOfMemoryException();

            IntPtr pointer = GlobalLock(memory);
            if (pointer == IntPtr.Zero)
            {
                GlobalFree(memory);
                throw new InvalidOperationException("클립보드 메모리에 접근할 수 없습니다.");
            }
            try { Marshal.Copy(bytes, start, pointer, length); }
            finally { GlobalUnlock(memory); }
            return memory;
        }

        private static bool Publish(uint format, ref IntPtr memory)
        {
            if (SetClipboardData(format, memory) == IntPtr.Zero)
                return false;

            // Windows owns successful handles. Failed ones are released by TryCopy.
            memory = IntPtr.Zero;
            return true;
        }

        [DllImport("user32.dll")]
        private static extern uint GetClipboardSequenceNumber();

        [DllImport("user32.dll")]
        private static extern IntPtr GetClipboardOwner();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterClipboardFormat(string name);

        [DllImport("user32.dll")]
        private static extern bool OpenClipboard(IntPtr owner);

        [DllImport("user32.dll")]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll")]
        private static extern bool EmptyClipboard();

        [DllImport("user32.dll")]
        private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalAlloc(uint flags, UIntPtr size);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalLock(IntPtr memory);

        [DllImport("kernel32.dll")]
        private static extern bool GlobalUnlock(IntPtr memory);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalFree(IntPtr memory);
    }
}
