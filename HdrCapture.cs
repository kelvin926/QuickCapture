using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.DXGI;
using D3D = SharpDX.Direct3D11;
using Rectangle = System.Drawing.Rectangle;

namespace QuickCapture
{
    internal static class HdrCapture
    {
        public static void Correct(Bitmap desktop, Rectangle desktopBounds)
        {
            using (var factory = new Factory1())
            using (Graphics graphics = Graphics.FromImage(desktop))
            {
                foreach (Adapter1 adapter in factory.Adapters1)
                using (adapter)
                {
                    foreach (Output output in adapter.Outputs)
                    using (output)
                    using (var hdr = output.QueryInterfaceOrNull<Output6>())
                    {
                        if (hdr == null) continue;
                        OutputDescription1 description = hdr.Description1;
                        if (!description.AttachedToDesktop || description.ColorSpace != ColorSpaceType.RgbFullG2084NoneP2020)
                            continue;
                        try
                        {
                            using (Bitmap display = Read(adapter, output, WhiteLevel(description.DeviceName)))
                            {
                                if (display == null) continue;
                                var area = description.DesktopCoordinates;
                                graphics.DrawImageUnscaled(display, area.Left - desktopBounds.Left, area.Top - desktopBounds.Top);
                            }
                        }
                        catch (SharpDXException) { }
                    }
                }
            }
        }

        private static Bitmap Read(Adapter1 adapter, Output output, float white)
        {
            Format[] formats = { Format.R16G16B16A16_Float, Format.B8G8R8A8_UNorm };
            using (var device = new D3D.Device(adapter, D3D.DeviceCreationFlags.BgraSupport))
            using (var output5 = output.QueryInterface<Output5>())
            using (OutputDuplication duplicate = output5.DuplicateOutput1(device, 0, formats.Length, formats))
            {
                OutputDuplicateFrameInformation info;
                Resource frame;
                if (duplicate.TryAcquireNextFrame(450, out info, out frame).Failure) return null;
                try
                {
                    using (frame)
                    using (var texture = frame.QueryInterface<D3D.Texture2D>())
                    {
                        var description = texture.Description;
                        if (description.Format != Format.R16G16B16A16_Float) return null;
                        description.Usage = D3D.ResourceUsage.Staging;
                        description.BindFlags = D3D.BindFlags.None;
                        description.CpuAccessFlags = D3D.CpuAccessFlags.Read;
                        description.OptionFlags = D3D.ResourceOptionFlags.None;
                        using (var staging = new D3D.Texture2D(device, description))
                        {
                            device.ImmediateContext.CopyResource(texture, staging);
                            DataBox mapped = device.ImmediateContext.MapSubresource(staging, 0, D3D.MapMode.Read, D3D.MapFlags.None);
                            try
                            {
                                Bitmap bitmap = ConvertPixels(mapped, description.Width, description.Height, white);
                                if (output.Description.Rotation == DisplayModeRotation.Rotate90) bitmap.RotateFlip(RotateFlipType.Rotate90FlipNone);
                                if (output.Description.Rotation == DisplayModeRotation.Rotate180) bitmap.RotateFlip(RotateFlipType.Rotate180FlipNone);
                                if (output.Description.Rotation == DisplayModeRotation.Rotate270) bitmap.RotateFlip(RotateFlipType.Rotate270FlipNone);
                                return bitmap;
                            }
                            finally { device.ImmediateContext.UnmapSubresource(staging, 0); }
                        }
                    }
                }
                finally { duplicate.ReleaseFrame(); }
            }
        }

        private static unsafe Bitmap ConvertPixels(DataBox source, int width, int height, float white)
        {
            var lut = new byte[65536];
            for (int i = 0; i < 0x7c00; i++)
            {
                int exponent = i >> 10, mantissa = i & 1023;
                double value = Math.Pow(2, exponent == 0 ? -14 : exponent - 15) *
                    ((exponent == 0 ? 0d : 1d) + mantissa / 1024d) / white;
                double linear = value <= 0.85 ? value : 0.85 + 0.15 * (1 - Math.Exp(-(value - 0.85) / 0.45));
                double srgb = linear <= 0.0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
                lut[i] = (byte)Math.Max(0, Math.Min(255, Math.Round(255 * srgb)));
            }
            var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            var locked = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, bitmap.PixelFormat);
            try
            {
                for (int y = 0; y < height; y++)
                {
                    ushort* input = (ushort*)((byte*)source.DataPointer + y * source.RowPitch);
                    byte* target = (byte*)locked.Scan0 + y * locked.Stride;
                    for (int x = 0; x < width; x++, input += 4, target += 3)
                    { target[0] = lut[input[2]]; target[1] = lut[input[1]]; target[2] = lut[input[0]]; }
                }
            }
            finally { bitmap.UnlockBits(locked); }
            return bitmap;
        }

        private static float WhiteLevel(string device)
        {
            const int active = 2;
            uint pathCount, modeCount;
            if (GetDisplayConfigBufferSizes(active, out pathCount, out modeCount) != 0) return 2.5f;
            var paths = new PathInfo[pathCount];
            IntPtr modes = Marshal.AllocHGlobal(checked((int)modeCount * 64));
            try
            {
                if (QueryDisplayConfig(active, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0) return 2.5f;
                for (int i = 0; i < pathCount; i++)
                {
                    var name = new SourceName { Header = new Packet { Type = 1, Size = 84, Adapter = paths[i].Source, Id = paths[i].SourceId } };
                    if (DisplayConfigGetDeviceInfo(ref name) != 0 || !string.Equals(name.Name, device, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var white = new White { Header = new Packet { Type = 11, Size = 24, Adapter = paths[i].Target, Id = paths[i].TargetId } };
                    if (DisplayConfigGetDeviceInfo(ref white) == 0 && white.Level >= 1000)
                        return white.Level / 1000f;
                }
            }
            finally { Marshal.FreeHGlobal(modes); }
            return 2.5f;
        }

        [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }
        [StructLayout(LayoutKind.Sequential)] private struct PathInfo
        {
            public Luid Source;
            public uint SourceId, SourceMode, SourceStatus;
            public Luid Target;
            public uint TargetId, TargetMode, Technology, Rotation, Scaling, RateNum, RateDenom, Ordering, Available, TargetStatus, Flags;
        }
        [StructLayout(LayoutKind.Sequential)] private struct Packet { public uint Type, Size; public Luid Adapter; public uint Id; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct SourceName
        {
            public Packet Header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        }
        [StructLayout(LayoutKind.Sequential)] private struct White { public Packet Header; public uint Level; }

        [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
        [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint paths, [Out] PathInfo[] path,
            ref uint modes, IntPtr mode, IntPtr topology);
        [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref SourceName packet);
        [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref White packet);
    }
}
