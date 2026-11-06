using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("QuickCapture")]
[assembly: System.Reflection.AssemblyDescription("Windows screen capture and recording")]
[assembly: System.Reflection.AssemblyVersion("2.0.2.0")]
[assembly: System.Reflection.AssemblyFileVersion("2.0.2.0")]

namespace QuickCapture
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            bool trayOnly = args.Contains("--tray");
            bool firstInstance;
            using (var instance = new Mutex(true, @"Local\QuickCapture.Codex.2", out firstInstance))
            {
                if (!firstInstance)
                {
                    if (!trayOnly) HotkeyHook.OpenExisting();
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var app = new CaptureApp())
                {
                    if (!trayOnly) app.OpenEditor(args.FirstOrDefault(File.Exists));
                    Application.Run(app);
                }
            }
        }
    }

    internal sealed class CaptureApp : ApplicationContext
    {
        private readonly QuickSettings settings = QuickSettings.Load();
        private readonly TrayWindow window;
        private readonly HotkeyHook hook;
        private readonly NotifyIcon tray;
        private readonly ContextMenuStrip menu;
        private readonly ClipboardImage clipboard;
        private readonly List<EditorForm> editors = new List<EditorForm>();
        private CaptureMode lastMode = CaptureMode.Rectangle;
        private Bitmap pending;
        private bool selecting, quitting, disposed;
        private int saves, lastCopy;

        public CaptureApp()
        {
            window = new TrayWindow(Shortcut);
            clipboard = new ClipboardImage(window.Handle);
            hook = new HotkeyHook(window.Handle) { CapturePrintScreen = settings.PrintScreen };
            menu = new ContextMenuStrip();
            menu.Items.Add("QuickCapture 열기", null, delegate { OpenEditor(); });
            menu.Items.Add("스크린샷   Win+Shift+S", null, delegate { Shortcut(CaptureAction.Image); });
            menu.Items.Add("화면 녹화   Win+Shift+R", null, delegate { Shortcut(CaptureAction.Video); });
            menu.Items.Add("텍스트 추출   Win+Shift+T", null, delegate { Shortcut(CaptureAction.Text); });
            menu.Items.Add("스크린샷 저장 폴더", null, delegate { OpenFolder(settings.Pictures); });
            menu.Items.Add("녹화 저장 폴더", null, delegate { OpenFolder(settings.Videos); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("종료", null, delegate { RequestExit(); });
            tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "QuickCapture | Win + Shift + S",
                ContextMenuStrip = menu, Visible = true };
            tray.DoubleClick += delegate { OpenEditor(); };
            tray.BalloonTipClicked += delegate { OpenEditor(); };
            if (!hook.Installed) Warn("단축키를 등록하지 못했습니다. 트레이 메뉴에서 캡처할 수 있습니다.");
        }

        private EditorForm CreateEditor()
        {
            var form = new EditorForm(this, settings);
            editors.Add(form);
            return form;
        }

        public void OpenEditor(string file = null)
        {
            if (selecting || quitting) return;
            EditorForm form = editors.LastOrDefault();
            if (pending != null && settings.MultipleWindows) form = null;
            form = form ?? CreateEditor();
            Show(form);
            if (file != null) { form.OpenPath(file); return; }
            if (pending != null && form.CanReplace())
            {
                form.ShowImage(pending);
                pending = null;
            }
        }

        private static void Show(Form form)
        {
            if (!form.Visible) form.Show();
            if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
            form.Activate();
        }

        private void Shortcut(CaptureAction action)
        {
            if (action == CaptureAction.Open) OpenEditor();
            else Capture(action == CaptureAction.Text ? CaptureMode.Text :
                action == CaptureAction.Video ? CaptureMode.Rectangle : lastMode, action == CaptureAction.Video, null, 0, false);
        }

        public void FromEditor(EditorForm source, CaptureMode? overrideMode = null, bool actions = false)
        {
            if (!settings.MultipleWindows && !source.CanReplace()) return;
            Capture(overrideMode ?? source.Mode, !overrideMode.HasValue && source.Recording, source,
                !overrideMode.HasValue && !source.Recording ? source.Delay : 0, actions);
        }

        public async void ClickToDo(EditorForm source)
        {
            try
            {
                if (await WindowsAsync.Wait(Windows.System.Launcher.LaunchUriAsync(new Uri("ms-clicktodo://"))))
                    return;
            }
            catch (Exception) { }
            FromEditor(source, CaptureMode.Text, true);
        }

        private async void Capture(CaptureMode mode, bool record, EditorForm source, int delay, bool actions)
        {
            if (selecting || quitting) return;
            selecting = true;
            if (source != null) source.Hide();
            try
            {
                if (delay > 0 || source != null) await Task.Delay(delay > 0 ? delay * 1000 : 120);
                Rectangle desktop = SystemInformation.VirtualScreen;
                Rectangle monitor = Screen.FromPoint(Cursor.Position).Bounds;
                Bitmap result = null;
                Rectangle area = Rectangle.Empty;
                string color = null;
                bool savePng = settings.AutoSave;
                bool forceCopy = false;
                using (Bitmap snapshot = DesktopImage.Capture(desktop, Screen.AllScreens))
                {
                    if (settings.ToneMap && !record)
                    {
                        try { await Task.Run(() => HdrCapture.Correct(snapshot, desktop)); }
                        catch (SharpDX.SharpDXException) { }
                    }
                    if (mode == CaptureMode.Fullscreen && !record && source != null && !settings.AdjustAfterSelection)
                        result = snapshot.Clone(new Rectangle(Point.Empty, snapshot.Size), PixelFormat.Format24bppRgb);
                    else
                    using (var selection = new SelectionOverlay(snapshot, desktop, monitor, mode, record,
                        settings.AutoSave, settings.AdjustAfterSelection))
                    {
                        Action updateOverlay = delegate
                        {
                            hook.SetVisibleOverlay(selection.Visible ? selection.Handle : IntPtr.Zero, selection.CanCopyAndClose);
                        };
                        selection.VisibleChanged += delegate { updateOverlay(); };
                        selection.CopyAvailabilityChanged += updateOverlay;
                        try
                        {
                            if (selection.ShowDialog() != DialogResult.OK) return;
                        }
                        finally { hook.SetVisibleOverlay(IntPtr.Zero, false); }
                        record = selection.Recording;
                        mode = selection.Mode;
                        area = selection.ScreenSelection;
                        savePng = selection.SavePng;
                        forceCopy = selection.CopyAndCloseRequested;
                        if (!record)
                        {
                            if (mode == CaptureMode.Color) color = selection.PickedColor;
                            else result = selection.MakeImage();
                        }
                    }
                }

                if (record)
                {
                    if (!MediaTools.Available) { Warn("녹화 엔진이 없습니다. ZIP 전체를 풀고 실행하세요."); return; }
                    string destination = settings.SaveRecordings ? settings.Videos : MediaTools.TempFolder;
                    using (var controls = new RecordingControls(area, destination, settings))
                        if (controls.ShowDialog() == DialogResult.OK)
                        {
                            EditorForm preview = source == null || settings.MultipleWindows ? CreateEditor() : source;
                            preview.ShowVideo(controls.Output);
                            Show(preview);
                        }
                    return;
                }
                if (color != null) { CopyText(color); return; }
                if (result == null) return;
                if (mode == CaptureMode.Text)
                {
                    TextScan text;
                    using (result) text = await Task.Run(() => LocalText.Read(result, actions));
                    if (actions) ShowActions(text);
                    else if (text.All.Length == 0) Warn("선택한 영역에서 텍스트를 찾지 못했습니다.");
                    else if (settings.TextAutoCopy) CopyText(FormatExtracted(text.All));
                    else ShowExtractedText(text);
                    return;
                }

                lastMode = mode;
                if (settings.Border) AddBorder(result);
                if (source != null)
                {
                    Persist((Bitmap)result.Clone(), savePng, false, forceCopy);
                    EditorForm preview = settings.MultipleWindows ? CreateEditor() : source;
                    preview.ShowImage(result);
                    Show(preview);
                }
                else
                {
                    if (pending != null) pending.Dispose();
                    pending = result;
                    Persist((Bitmap)result.Clone(), savePng, true, forceCopy);
                }
            }
            catch (Exception error) { Warn(error.Message); }
            finally
            {
                selecting = false;
                if (source != null && !source.IsDisposed && !source.Visible && !quitting) Show(source);
            }
        }

        private void AddBorder(Bitmap image)
        {
            int size = Math.Max(1, Math.Min(settings.BorderWidth, Math.Min(image.Width, image.Height) / 2));
            using (Graphics graphics = Graphics.FromImage(image))
            using (var pen = new Pen(Color.FromArgb(settings.BorderArgb), size))
                graphics.DrawRectangle(pen, size / 2, size / 2, Math.Max(1, image.Width - size), Math.Max(1, image.Height - size));
        }

        private async void Persist(Bitmap image, bool save, bool notify, bool forceCopy)
        {
            using (image)
            {
                bool copy = forceCopy || settings.AutoCopy;
                if (!save && !copy)
                {
                    if (notify) CaptureReady("스크린샷을 캡처했습니다.");
                    return;
                }
                int number = Interlocked.Increment(ref lastCopy);
                uint sequence = ClipboardImage.SequenceNumber();
                string destination = settings.Pictures;
                byte[] png = null;
                bool saved = false;
                saves++;
                try
                {
                    png = await Task.Run(() => CaptureFiles.EncodePng(image));
                    if (save)
                    {
                        await Task.Run(() => CaptureFiles.SavePng(destination, png));
                        saved = true;
                    }
                }
                catch (Exception error) { Warn("PNG 저장 실패: " + error.Message); }
                finally { saves--; if (quitting && saves == 0) ExitThread(); }
                if (quitting || png == null) return;
                bool copied = copy && await CheckCopy(() => clipboard.TryCopy(image, png, sequence,
                    () => Volatile.Read(ref lastCopy) == number), () => Volatile.Read(ref lastCopy) == number, saved);
                if (notify && !quitting && Volatile.Read(ref lastCopy) == number)
                {
                    if (copied) CaptureReady(save && !saved ? "클립보드에 복사했습니다. PNG 저장에 실패했습니다." : "클립보드에 복사했습니다.");
                    else if (!copy && saved) CaptureReady("PNG를 저장했습니다.");
                }
            }
        }

        private void CaptureReady(string status)
        {
            tray.ShowBalloonTip(3500, "QuickCapture", status + " 클릭하여 편집하세요.", ToolTipIcon.Info);
        }

        public async void CopyImage(Bitmap image, bool quiet)
        {
            using (image)
            {
                int number = Interlocked.Increment(ref lastCopy);
                uint sequence = ClipboardImage.SequenceNumber();
                await CheckCopy(() => clipboard.TryCopy(image, CaptureFiles.EncodePng(image), sequence,
                    () => Volatile.Read(ref lastCopy) == number), () => !quiet && Volatile.Read(ref lastCopy) == number, false);
            }
        }

        public async void CopyText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            int number = Interlocked.Increment(ref lastCopy);
            uint sequence = ClipboardImage.SequenceNumber();
            Func<bool> latest = () => Volatile.Read(ref lastCopy) == number;
            await CheckCopy(() => clipboard.TryCopyText(text, sequence, latest), latest, false);
        }

        public async void CopyFile(string path)
        {
            int number = Interlocked.Increment(ref lastCopy);
            uint sequence = ClipboardImage.SequenceNumber();
            Func<bool> latest = () => Volatile.Read(ref lastCopy) == number;
            await CheckCopy(() => clipboard.TryCopyFile(path, sequence, latest), latest, false);
        }

        private async Task<bool> CheckCopy(Func<bool> publish, Func<bool> warn, bool saved)
        {
            bool copied;
            try { copied = await Task.Run(publish); }
            catch (Exception) { copied = false; }
            if (!quitting && !copied && warn())
                Warn(saved ? "클립보드에 복사하지 못했습니다. PNG는 저장 폴더에 있습니다." : "클립보드에 복사하지 못했습니다.");
            return copied;
        }

        private void ShowActions(TextScan text)
        {
            using (var form = new Form { Text = "QuickCapture - 화면의 텍스트로 수행", ClientSize = new Size(515, 270),
                StartPosition = FormStartPosition.CenterScreen, TopMost = true })
            {
                var body = new TextBox { Text = text.All, Multiline = true, ScrollBars = ScrollBars.Vertical, ReadOnly = true, Dock = DockStyle.Fill };
                var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 37 };
                AddAction(buttons, "텍스트 복사", () => CopyText(body.SelectionLength > 0 ? body.SelectedText : body.Text));
                AddAction(buttons, "웹에서 검색", () => Process.Start(new ProcessStartInfo(
                    "https://www.bing.com/search?q=" + Uri.EscapeDataString(body.SelectionLength > 0 ? body.SelectedText : body.Text))
                    { UseShellExecute = true }));
                if (!string.IsNullOrEmpty(text.Qr)) AddAction(buttons, "QR 내용 복사", () => CopyText(text.Qr));
                form.Controls.Add(body); form.Controls.Add(buttons);
                form.ShowDialog();
            }
        }

        private string FormatExtracted(string text)
        {
            return settings.TextSingleLine ? text.Replace(Environment.NewLine, " ") : text;
        }

        private void ShowExtractedText(TextScan text)
        {
            using (var form = new Form { Text = "QuickCapture - 텍스트 추출기", ClientSize = new Size(545, 280),
                StartPosition = FormStartPosition.CenterScreen, TopMost = true, KeyPreview = true })
            {
                var body = new TextBox { Text = text.All, Multiline = true, ScrollBars = ScrollBars.Vertical, ReadOnly = true,
                    Dock = DockStyle.Fill, Font = new Font("맑은 고딕", 11) };
                var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38 };
                AddAction(buttons, "모든 텍스트 복사", () => { CopyText(FormatExtracted(text.All)); form.Close(); });
                AddAction(buttons, "선택한 텍스트 복사", () =>
                { if (body.SelectionLength > 0) { CopyText(FormatExtracted(body.SelectedText)); form.Close(); } });
                var single = new CheckBox { Text = "줄바꿈 제거", Checked = settings.TextSingleLine, AutoSize = true };
                var auto = new CheckBox { Text = "자동 복사", Checked = settings.TextAutoCopy, AutoSize = true };
                buttons.Controls.Add(single); buttons.Controls.Add(auto);
                single.CheckedChanged += delegate { settings.TextSingleLine = single.Checked; };
                auto.CheckedChanged += delegate { settings.TextAutoCopy = auto.Checked; };
                form.KeyDown += delegate(object sender, KeyEventArgs args)
                {
                    if (args.KeyCode == Keys.Escape) form.Close();
                    if (args.Control && args.KeyCode == Keys.C)
                    {
                        args.SuppressKeyPress = true;
                        if (body.SelectionLength > 0) { CopyText(FormatExtracted(body.SelectedText)); form.Close(); }
                    }
                };
                form.Controls.Add(body); form.Controls.Add(buttons);
                form.ShowDialog();
                try { settings.Save(); }
                catch (Exception error) { Warn(error.Message); }
            }
        }

        private static void AddAction(FlowLayoutPanel parent, string text, Action action)
        {
            var button = new Button { Text = text, AutoSize = true };
            button.Click += delegate { action(); };
            parent.Controls.Add(button);
        }

        public void Configure(Form owner)
        {
            using (var form = new SettingsForm(settings))
                form.ShowDialog(owner);
            hook.CapturePrintScreen = settings.PrintScreen;
            foreach (EditorForm editor in editors) editor.ApplyTheme();
            try { settings.Save(); }
            catch (Exception error) { Warn(error.Message); }
        }

        private void OpenFolder(string folder)
        {
            try { Directory.CreateDirectory(folder); Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true }); }
            catch (Exception error) { Warn(error.Message); }
        }

        private void Warn(string message)
        {
            if (!disposed && !quitting)
                tray.ShowBalloonTip(4500, "QuickCapture", message.Length > 220 ? message.Substring(0, 220) : message, ToolTipIcon.Warning);
        }

        public void RequestExit()
        {
            if (selecting || editors.Any(editor => !editor.CanReplace())) return;
            quitting = true;
            Interlocked.Increment(ref lastCopy);
            if (saves == 0) ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed)
            {
                disposed = true;
                hook.Dispose();
                tray.Dispose(); menu.Dispose();
                foreach (EditorForm form in editors) form.CloseForExit();
                if (pending != null) pending.Dispose();
                window.DestroyHandle();
            }
            base.Dispose(disposing);
        }

        private sealed class TrayWindow : NativeWindow
        {
            private readonly Action<CaptureAction> capture;
            public TrayWindow(Action<CaptureAction> capture)
            {
                this.capture = capture;
                CreateHandle(new CreateParams { Caption = "QuickCapture.Tray.2", Parent = new IntPtr(-3) });
            }
            protected override void WndProc(ref Message message)
            {
                if (message.Msg == HotkeyHook.CaptureMessage)
                {
                    int value = message.WParam.ToInt32();
                    if (value >= 1 && value <= 4) capture((CaptureAction)value);
                }
                else base.WndProc(ref message);
            }
        }
    }
}
