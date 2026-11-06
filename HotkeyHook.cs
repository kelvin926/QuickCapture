using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace QuickCapture
{
    internal sealed class HotkeyHook : IDisposable
    {
        internal const int CaptureMessage = 0x8001;
        internal const int OverlayKeyMessage = 0x8002;
        private readonly IntPtr target;
        private readonly Thread worker;
        private readonly HookCallback callback;
        private readonly ManualResetEventSlim ready = new ManualResetEventSlim();
        private uint threadId;
        private readonly bool[] down = new bool[9];
        private readonly bool[] suppressed = new bool[9];
        private IntPtr overlay;
        private IntPtr copyOverlay;
        public volatile bool CapturePrintScreen;
        public bool Installed { get; private set; }

        public HotkeyHook(IntPtr target)
        {
            this.target = target;
            callback = HandleKey;
            worker = new Thread(Run) { IsBackground = true, Name = "QuickCapture shortcut" };
            worker.Start();
            ready.Wait();
        }

        private void Run()
        {
            const int keyboardLowLevel = 13;
            threadId = GetCurrentThreadId();
            NativeMessage message;
            PeekMessage(out message, IntPtr.Zero, 0, 0, 0);
            down[1] = IsDown(Keys.S);
            down[2] = IsDown(Keys.R);
            down[3] = IsDown(Keys.T);
            down[4] = IsDown(Keys.PrintScreen);
            down[5] = IsDown(Keys.Escape);
            down[6] = IsDown(Keys.Enter);
            down[7] = IsDown(Keys.E);
            down[8] = IsDown(Keys.C);
            IntPtr hook = SetWindowsHookEx(keyboardLowLevel, callback, GetModuleHandle(null), 0);
            Installed = hook != IntPtr.Zero;
            ready.Set();

            if (!Installed)
                return;

            try
            {
                while (GetMessage(out message, IntPtr.Zero, 0, 0) > 0)
                    DispatchMessage(ref message);
            }
            finally
            {
                UnhookWindowsHookEx(hook);
            }
        }

        private IntPtr HandleKey(int code, IntPtr message, IntPtr data)
        {
            if (code != 0)
                return CallNextHookEx(IntPtr.Zero, code, message, data);

            Keys key = (Keys)Marshal.ReadInt32(data);
            int index;
            switch (key)
            {
                case Keys.S: index = 1; break;
                case Keys.R: index = 2; break;
                case Keys.T: index = 3; break;
                case Keys.PrintScreen: index = 4; break;
                case Keys.Escape: index = 5; break;
                case Keys.Enter: index = 6; break;
                case Keys.E: index = 7; break;
                case Keys.C: index = 8; break;
                default: return CallNextHookEx(IntPtr.Zero, code, message, data);
            }
            int kind = message.ToInt32();
            if (kind == 0x0100 || kind == 0x0104)
            {
                if (!down[index])
                {
                    down[index] = true;
                    bool win = IsDown(Keys.LWin) || IsDown(Keys.RWin);
                    bool shift = IsDown(Keys.ShiftKey);
                    bool control = IsDown(Keys.ControlKey);
                    bool alt = IsDown(Keys.Menu);
                    if (index >= 5)
                        suppressed[index] = !win && !shift && !alt && RouteOverlayKey(key, control);
                    else if ((index == 4 ? CapturePrintScreen && !win && !shift : win && shift) && !control && !alt)
                    {
                        suppressed[index] = PostMessage(target, CaptureMessage, new IntPtr(index == 4 ? 1 : index), IntPtr.Zero);
                        if (suppressed[index] && win)
                            SuppressStartMenu();
                    }
                }
                if (suppressed[index])
                    return new IntPtr(1);
            }
            else if (kind == 0x0101 || kind == 0x0105)
            {
                down[index] = false;
                if (suppressed[index])
                {
                    suppressed[index] = false;
                    return new IntPtr(1);
                }
            }
            return CallNextHookEx(IntPtr.Zero, code, message, data);
        }

        private bool RouteOverlayKey(Keys key, bool control)
        {
            bool requiresControl = key == Keys.E || key == Keys.C;
            IntPtr visibleOverlay = key == Keys.C ? Volatile.Read(ref copyOverlay) : Volatile.Read(ref overlay);
            return visibleOverlay != IntPtr.Zero && GetForegroundWindow() != visibleOverlay && control == requiresControl &&
                PostMessage(visibleOverlay, OverlayKeyMessage,
                    new IntPtr((int)(requiresControl ? Keys.Control | key : key)), IntPtr.Zero);
        }

        private static bool IsDown(Keys key)
        {
            return (GetAsyncKeyState((int)key) & 0x8000) != 0;
        }

        internal void SetVisibleOverlay(IntPtr window, bool canCopy)
        {
            Volatile.Write(ref overlay, window);
            Volatile.Write(ref copyOverlay, canCopy ? window : IntPtr.Zero);
        }

        private static void SuppressStartMenu()
        {
            // PowerToys uses the same dummy key-up to mark the Win key as used.
            var input = new NativeInput
            {
                Type = 1,
                Data = new InputData
                {
                    Keyboard = new KeyboardInput { VirtualKey = 0xff, Flags = 2 }
                }
            };
            SendInput(1, ref input, Marshal.SizeOf(typeof(NativeInput)));
        }

        public void Dispose()
        {
            if (worker.IsAlive)
            {
                const uint quit = 0x0012;
                PostThreadMessage(threadId, quit, IntPtr.Zero, IntPtr.Zero);
                if (!worker.Join(500))
                    return;
            }
            ready.Dispose();
        }

        internal static void OpenExisting()
        {
            IntPtr window = FindWindowEx(new IntPtr(-3), IntPtr.Zero, null, "QuickCapture.Tray.2");
            if (window != IntPtr.Zero)
                PostMessage(window, CaptureMessage, new IntPtr((int)CaptureAction.Open), IntPtr.Zero);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string type, string title);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeMessage
        {
            public IntPtr Window;
            public uint Message;
            public IntPtr WParam;
            public IntPtr LParam;
            public uint Time;
            public int X;
            public int Y;
            public uint Private;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeInput
        {
            public uint Type;
            public InputData Data;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputData
        {
            [FieldOffset(0)] public KeyboardInput Keyboard;
            [FieldOffset(0)] public MouseInput Mouse;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardInput
        {
            public ushort VirtualKey;
            public ushort ScanCode;
            public uint Flags;
            public uint Time;
            public UIntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MouseInput
        {
            public int X, Y;
            public uint Data, Flags, Time;
            public UIntPtr ExtraInfo;
        }

        private delegate IntPtr HookCallback(int code, IntPtr message, IntPtr data);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SetWindowsHookEx(int kind, HookCallback handler, IntPtr module, uint thread);

        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int key);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint SendInput(uint count, ref NativeInput input, int size);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool PostThreadMessage(uint thread, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool PeekMessage(out NativeMessage message, IntPtr window, uint first, uint last, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetMessage(out NativeMessage message, IntPtr window, uint first, uint last);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr DispatchMessage(ref NativeMessage message);
    }
}
