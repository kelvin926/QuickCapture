using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using Wpf = System.Windows;
using WpfControls = System.Windows.Controls;

namespace QuickCapture
{
    internal sealed class EditorForm : Form
    {
        private readonly CaptureApp app;
        private readonly QuickSettings settings;
        private readonly MarkupCanvas canvas = new MarkupCanvas { Dock = DockStyle.Fill };
        private readonly Label empty = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
        private readonly ToolStrip top = new ToolStrip(), edits = new ToolStrip(), playback = new ToolStrip();
        private readonly ToolStripButton screenshot, recording, newCapture, undo, redo, crop;
        private readonly ToolStripDropDownButton mode, delay, text, more;
        private readonly ToolStripLabel status = new ToolStripLabel();
        private readonly WpfControls.MediaElement player;
        private readonly ElementHost host;
        private readonly TrackBar seek;
        private readonly Timer refresh;
        private bool updatingSeek, playing, dirty, permittedClose;
        private string video;
        private TimeSpan duration;
        private TextScan scan;
        private string selected = "";
        private int revision;

        public CaptureMode Mode = CaptureMode.Rectangle;
        public bool Recording { get { return recording.Checked; } }
        public int Delay { get; private set; }

        public EditorForm(CaptureApp app, QuickSettings settings)
        {
            this.app = app;
            this.settings = settings;
            Text = "QuickCapture";
            Font = new Font("맑은 고딕", 10);
            MinimumSize = new Size(530, 235);
            ClientSize = new Size(600, 256);
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            AllowDrop = true;
            DragEnter += delegate(object sender, DragEventArgs args)
            { if (args.Data.GetDataPresent(DataFormats.FileDrop)) args.Effect = DragDropEffects.Copy; };
            DragDrop += delegate(object sender, DragEventArgs args)
            { OpenPath(((string[])args.Data.GetData(DataFormats.FileDrop)).FirstOrDefault()); };

            foreach (ToolStrip bar in new[] { top, edits, playback })
            {
                bar.GripStyle = ToolStripGripStyle.Hidden;
                bar.Padding = new Padding(8, 4, 8, 4);
                bar.ImageScalingSize = new Size(20, 20);
            }
            newCapture = Button(top, "새 캡처", () => app.FromEditor(this), "\ue710", true);
            top.Items.Add(new ToolStripSeparator());
            screenshot = Button(top, "스크린샷", () => Switch(false), "\ue722");
            recording = Button(top, "녹화", () => Switch(true), "\ue714");
            mode = DropDown(top, "캡처 영역", "\uf7ed");
            delay = DropDown(top, "캡처 지연", "\ue916");
            foreach (int value in new[] { 0, 3, 5, 10 })
            {
                int seconds = value;
                Add(delay, seconds == 0 ? "지연 없음" : seconds + "초", () => { Delay = seconds; RefreshDelay(); });
            }
            Button(top, "텍스트 추출기", () => app.FromEditor(this, CaptureMode.Text), "\ue8fe");
            Button(top, "수행하려면 클릭", () => app.ClickToDo(this), "\ue794");
            more = DropDown(top, "자세히", "\ue712");
            more.Alignment = ToolStripItemAlignment.Right;
            more.DropDownOpening += delegate { PopulateMore(); };

            Button(edits, "펜", () => SetInk(MarkTool.Pen), "\ue70f");
            Button(edits, "형광펜", () => SetInk(MarkTool.Marker), "\uedb0");
            Button(edits, "지우개", () => canvas.SetTool(MarkTool.Eraser), "\ue75c");
            var ink = DropDown(edits, "펜 옵션", "\ue790");
            Add(ink, "색", () => PickColor(false));
            Add(ink, "굵기", () => PickWidth());
            var drawHold = Add(ink, "그리고 유지", () => { });
            drawHold.Checked = canvas.DrawAndHold; drawHold.CheckOnClick = true;
            drawHold.CheckedChanged += delegate { canvas.DrawAndHold = drawHold.Checked; };
            var shapes = DropDown(edits, "도형 및 이모지", "\uf158");
            Add(shapes, "사각형", () => canvas.SetTool(MarkTool.Rectangle));
            Add(shapes, "타원", () => canvas.SetTool(MarkTool.Ellipse));
            Add(shapes, "선", () => canvas.SetTool(MarkTool.Line));
            Add(shapes, "화살표", () => canvas.SetTool(MarkTool.Arrow));
            Add(shapes, "채우기 색", () => PickColor(true));
            Add(shapes, "윤곽선 색", () => PickColor(false));
            Add(shapes, "채우기 없음", () => canvas.Fill = Color.Transparent);
            Add(shapes, "윤곽선 없음", () => canvas.Ink = Color.Transparent);
            Add(shapes, "투명도", () => PickOpacity());
            Add(shapes, "이모지", () => Emoji());
            Add(shapes, "이동 및 크기 조정", () => { canvas.SetTool(MarkTool.Select); SetStatus("드래그로 이동, Shift+드래그로 크기 조정"); });
            crop = Button(edits, "이미지 자르기", () => Crop(), "\ue7a8");
            text = DropDown(edits, "텍스트 작업", "\ue8a5");
            Add(text, "텍스트 분석 중", () => { }).Enabled = false;
            text.DropDownOpening += async delegate { await TextMenu(); };
            undo = Button(edits, "실행 취소", () => canvas.Undo(), "\ue7a7");
            redo = Button(edits, "다시 실행", () => canvas.Redo(), "\ue7a6");
            edits.Items.Add(new ToolStripSeparator());
            Button(edits, "저장", () => Save(), "\ue74e");
            Button(edits, "복사", () => Copy(), "\ue8c8");
            Button(edits, "공유", () => Share(), "\ue72d");

            Button(playback, "재생 / 일시 정지", () => Play(), "\ue768");
            Button(playback, "자르기", () => Trim(), "\ue7a8");
            var gif = DropDown(playback, "GIF 내보내기", "\uf4a9", true);
            Add(gif, "고품질 GIF 저장", () => ExportGif(true, false));
            Add(gif, "저품질 GIF 저장", () => ExportGif(false, false));
            Add(gif, "고품질 GIF 복사", () => ExportGif(true, true));
            Add(gif, "저품질 GIF 복사", () => ExportGif(false, true));
            Button(playback, "Clipchamp", () => Clipchamp(), "\ue714", true);
            Button(playback, "저장", () => Save(), "\ue74e");
            Button(playback, "복사", () => Copy(), "\ue8c8");
            Button(playback, "공유", () => Share(), "\ue72d");
            playback.Items.Add(status);
            seek = new TrackBar { Minimum = 0, Maximum = 1000, TickStyle = TickStyle.None, Dock = DockStyle.Bottom, Height = 24 };
            seek.Scroll += delegate
            { if (!updatingSeek && duration > TimeSpan.Zero) player.Position = TimeSpan.FromMilliseconds(duration.TotalMilliseconds * seek.Value / 1000); };
            player = new WpfControls.MediaElement
            {
                LoadedBehavior = WpfControls.MediaState.Manual, UnloadedBehavior = WpfControls.MediaState.Manual,
                Stretch = Wpf.Media.Stretch.Uniform, ScrubbingEnabled = true
            };
            player.MediaOpened += delegate { if (player.NaturalDuration.HasTimeSpan) duration = player.NaturalDuration.TimeSpan; };
            player.MediaEnded += delegate { playing = false; player.Pause(); player.Position = TimeSpan.Zero; };
            player.MediaFailed += delegate(object sender, Wpf.ExceptionRoutedEventArgs args)
            { SetStatus("동영상 미리보기를 재생하지 못했습니다."); };
            host = new ElementHost { Dock = DockStyle.Fill, Child = player };
            refresh = new Timer { Interval = 200 };
            refresh.Tick += delegate { UpdateSeek(); };
            refresh.Start();

            Controls.Add(canvas); Controls.Add(empty); Controls.Add(host);
            Controls.Add(seek); Controls.Add(playback); Controls.Add(edits); Controls.Add(top);
            canvas.Changed += delegate
            {
                revision++; dirty = true; scan = null; selected = "";
                undo.Enabled = canvas.CanUndo; redo.Enabled = canvas.CanRedo;
                if (settings.CopyEdits) app.CopyImage(canvas.Render(), true);
            };
            canvas.TextSelected += value => { selected = value; if (value.Length > 0) SetStatus("Ctrl+C로 선택한 텍스트 복사"); };
            canvas.ContextMenuStrip = BuildContextMenu();
            ApplyTheme();
            Switch(false);
            RefreshDelay();
            PopulateMore();
            ShowContent();
        }

        private ToolStripButton Button(ToolStrip bar, string name, Action action, string icon, bool label = false)
        {
            var button = new ToolStripButton(label ? icon + "  " + name : icon) { AutoToolTip = true, ToolTipText = name,
                AccessibleName = name, DisplayStyle = ToolStripItemDisplayStyle.Text,
                Font = new Font(label ? "Segoe UI" : "Segoe Fluent Icons", label ? 10f : 13f), Padding = new Padding(4, 2, 4, 2) };
            button.Click += delegate { action(); };
            bar.Items.Add(button);
            return button;
        }

        private static ToolStripDropDownButton DropDown(ToolStrip bar, string name, string icon, bool label = false)
        {
            var menu = new ToolStripDropDownButton(label ? name : icon) { ToolTipText = name, AccessibleName = name,
                Font = new Font(label ? "Segoe UI" : "Segoe Fluent Icons", label ? 10f : 13f), Padding = new Padding(3, 2, 3, 2) };
            bar.Items.Add(menu);
            return menu;
        }

        private static ToolStripMenuItem Add(ToolStripDropDownItem item, string title, Action action)
        {
            var menu = new ToolStripMenuItem(title);
            menu.Click += delegate { action(); };
            item.DropDownItems.Add(menu);
            return menu;
        }

        public void Switch(bool videoMode)
        {
            screenshot.Checked = !videoMode;
            recording.Checked = videoMode;
            newCapture.Text = videoMode ? "+  새 녹화" : "+  새 캡처";
            delay.Visible = !videoMode;
            if (videoMode && Mode != CaptureMode.Window) Mode = CaptureMode.Rectangle;
            RefreshModes();
            empty.Text = videoMode ? "녹화를 시작하려면 Win + Shift + R을 누릅니다."
                : "화면 캡처를 시작하려면 Win + Shift + S를 누릅니다.";
        }

        private void RefreshModes()
        {
            mode.DropDownItems.Clear();
            foreach (ModeItem candidate in Recording ? ModeItem.VideoModes : ModeItem.ImageModes)
            {
                ModeItem entry = candidate;
                Add(mode, entry.ToString(), () => { Mode = entry.Value; RefreshModes(); }).Checked = Mode == entry.Value;
            }
        }

        private void RefreshDelay()
        {
            foreach (ToolStripMenuItem entry in delay.DropDownItems)
                entry.Checked = entry.Text == (Delay == 0 ? "지연 없음" : Delay + "초");
        }

        public void ShowImage(Bitmap bitmap)
        {
            StopVideo();
            canvas.LoadImage(bitmap);
            canvas.SetTool(MarkTool.Pen);
            ClientSize = new Size(Math.Max(ClientSize.Width, 775), Math.Max(ClientSize.Height, 480));
            revision++; scan = null; selected = ""; dirty = false;
            undo.Enabled = redo.Enabled = false;
            ShowContent();
        }

        public void ShowVideo(string path, bool changed = false)
        {
            StopVideo();
            video = path;
            dirty = changed;
            ClientSize = new Size(Math.Max(ClientSize.Width, 775), Math.Max(ClientSize.Height, 480));
            ShowContent();
            player.Source = new Uri(path);
            player.Play();
            player.Pause();
        }

        private void ShowContent()
        {
            bool isVideo = video != null;
            bool hasImage = canvas.HasImage && !isVideo;
            host.Visible = playback.Visible = seek.Visible = isVideo;
            canvas.Visible = edits.Visible = hasImage;
            empty.Visible = !isVideo && !hasImage;
        }

        private void StopVideo()
        {
            if (video == null) return;
            player.Close();
            player.Source = null;
            playing = false; duration = TimeSpan.Zero; video = null; seek.Value = 0;
        }

        private void OpenFile()
        {
            using (var dialog = new OpenFileDialog { Filter = "이미지 및 동영상|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.mp4;*.m4v;*.mov;*.wmv|모든 파일|*.*" })
                if (dialog.ShowDialog(this) == DialogResult.OK) OpenPath(dialog.FileName);
        }

        public void OpenPath(string path)
        {
            if (path == null || !CanReplace()) return;
            try
            {
                string extension = Path.GetExtension(path).ToLowerInvariant();
                if (new[] { ".mp4", ".m4v", ".mov", ".wmv" }.Contains(extension)) ShowVideo(path);
                else using (var original = Image.FromFile(path)) ShowImage(new Bitmap(original));
            }
            catch (Exception error) { Error(error); }
        }

        private bool Save()
        {
            if (video == null && !canvas.HasImage) return false;
            bool isVideo = video != null;
            using (var dialog = new SaveFileDialog
            {
                Filter = isVideo ? "동영상|*" + Path.GetExtension(video) : "PNG|*.png|JPEG|*.jpg|GIF|*.gif|BMP|*.bmp",
                FileName = isVideo ? Path.GetFileName(video) : "Screenshot_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png"
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return false;
                try
                {
                    if (isVideo)
                    {
                        if (!string.Equals(video, dialog.FileName, StringComparison.OrdinalIgnoreCase))
                            File.Copy(video, dialog.FileName, true);
                    }
                    else
                    {
                        ImageFormat format = dialog.FilterIndex == 2 ? ImageFormat.Jpeg : dialog.FilterIndex == 3
                            ? ImageFormat.Gif : dialog.FilterIndex == 4 ? ImageFormat.Bmp : ImageFormat.Png;
                        using (Bitmap result = canvas.Render()) result.Save(dialog.FileName, format);
                    }
                    dirty = false;
                    SetStatus("저장됨");
                    return true;
                }
                catch (Exception error) { Error(error); return false; }
            }
        }

        private void Copy()
        {
            if (video != null) app.CopyFile(video);
            else if (canvas.Tool == MarkTool.Text && selected.Length > 0) app.CopyText(selected);
            else if (canvas.HasImage) app.CopyImage(canvas.Render(), false);
        }

        private string ExportImage()
        {
            string folder = MediaTools.TempFolder;
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "Screenshot.png");
            using (Bitmap result = canvas.Render()) result.Save(path, ImageFormat.Png);
            return path;
        }

        private void Share()
        {
            if (video == null && !canvas.HasImage) return;
            try { WindowsShare.File(Handle, video ?? ExportImage()); }
            catch (Exception error) { Error(error); }
        }

        private void PaintExternally()
        {
            try { Process.Start("mspaint.exe", MediaTools.Quoted(ExportImage())); }
            catch (Exception error) { Error(error); }
        }

        private void Print()
        {
            using (Bitmap result = canvas.Render())
            using (var document = new System.Drawing.Printing.PrintDocument())
            using (var dialog = new PrintDialog { Document = document, UseEXDialog = true })
            {
                document.PrintPage += delegate(object sender, System.Drawing.Printing.PrintPageEventArgs args)
                {
                    float scale = Math.Min((float)args.MarginBounds.Width / result.Width, (float)args.MarginBounds.Height / result.Height);
                    args.Graphics.DrawImage(result, args.MarginBounds.X, args.MarginBounds.Y, result.Width * scale, result.Height * scale);
                };
                try { if (dialog.ShowDialog(this) == DialogResult.OK) document.Print(); }
                catch (Exception error) { Error(error); }
            }
        }

        private async Task TextMenu()
        {
            text.DropDownItems.Clear();
            if (scan == null)
            {
                Add(text, "텍스트 분석 중", () => { }).Enabled = false;
                int before = revision;
                try
                {
                    using (Bitmap bitmap = canvas.Render()) scan = await Task.Run(() => LocalText.Read(bitmap, true));
                    if (IsDisposed || revision != before) { scan = null; return; }
                    canvas.ShowText(scan);
                }
                catch (Exception error) { text.DropDownItems.Clear(); Add(text, error.Message, () => { }).Enabled = false; return; }
            }
            text.DropDownItems.Clear();
            Add(text, "모든 텍스트 복사", () => app.CopyText(scan.All)).Enabled = scan.All.Length > 0;
            Add(text, "이메일 및 전화번호 가리기", () => SetStatus(canvas.Redact(scan, false) + "곳을 가렸습니다."));
            Add(text, "모든 텍스트 가리기", () => SetStatus(canvas.Redact(scan, true) + "곳을 가렸습니다."));
            if (!string.IsNullOrEmpty(scan.Qr))
            {
                Add(text, "QR 코드 복사", () => app.CopyText(scan.Qr));
                Uri url;
                if (Uri.TryCreate(scan.Qr, UriKind.Absolute, out url) && (url.Scheme == "https" || url.Scheme == "http"))
                    Add(text, "QR 링크 열기", () => Launch(url.AbsoluteUri));
            }
        }

        private void PopulateMore()
        {
            more.DropDownItems.Clear();
            Add(more, "파일 열기", () => OpenFile());
            if (video != null || canvas.HasImage)
            {
                Add(more, "공유", () => Share());
                if (video == null)
                {
                    Add(more, "Paint로 편집", () => PaintExternally());
                    Add(more, "인쇄", () => Print());
                    Add(more, "눈금자", () => Measurement(1)).Checked = canvas.Measurement == 1;
                    Add(more, "각도기", () => Measurement(2)).Checked = canvas.Measurement == 2;
                    Add(more, "확대", () => canvas.Zoom(1));
                    Add(more, "축소", () => canvas.Zoom(-1));
                    Add(more, "화면에 맞춤", () => canvas.Fit());
                    Add(more, "Bing 시각 검색 열기", () => { app.CopyImage(canvas.Render(), false); Launch("https://www.bing.com/visualsearch"); });
                }
            }
            Add(more, "설정", () => app.Configure(this));
            Add(more, "종료", () => app.RequestExit());
        }

        private ContextMenuStrip BuildContextMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("복사", null, delegate { Copy(); });
            menu.Items.Add("다른 이름으로 저장", null, delegate { Save(); });
            menu.Items.Add("Paint로 편집", null, delegate { PaintExternally(); });
            menu.Items.Add("Bing 시각 검색 열기", null, delegate { app.CopyImage(canvas.Render(), false); Launch("https://www.bing.com/visualsearch"); });
            return menu;
        }

        private void Crop()
        {
            if (canvas.CropPending) canvas.CommitCrop();
            else { canvas.SetTool(MarkTool.Crop); SetStatus("자를 영역을 드래그하고 Enter"); }
        }

        private void Measurement(int kind)
        {
            canvas.Measurement = canvas.Measurement == kind ? 0 : kind;
            canvas.Invalidate();
            SetStatus("Shift+드래그로 이동, Shift+휠로 회전");
        }

        private void SetInk(MarkTool tool)
        {
            if (tool == MarkTool.Marker && canvas.Tool != MarkTool.Marker) canvas.Ink = Color.Gold;
            if (tool == MarkTool.Pen && canvas.Tool == MarkTool.Marker) canvas.Ink = Color.FromArgb(240, 67, 68);
            canvas.SetTool(tool);
        }

        private void PickColor(bool fill)
        {
            using (var dialog = new ColorDialog { FullOpen = true, Color = fill ? canvas.Fill : canvas.Ink })
                if (dialog.ShowDialog(this) == DialogResult.OK)
                { if (fill) canvas.Fill = dialog.Color; else canvas.Ink = dialog.Color; }
        }

        private void PickWidth()
        {
            int? result = NumberDialog("선 굵기", (int)canvas.InkWidth, 1, 80);
            if (result.HasValue) canvas.InkWidth = result.Value;
        }

        private void PickOpacity()
        {
            int? outline = NumberDialog("윤곽선 불투명도 (%)", canvas.Ink.A * 100 / 255, 0, 100);
            if (outline.HasValue) canvas.Ink = Color.FromArgb(outline.Value * 255 / 100, canvas.Ink);
            int? fill = NumberDialog("채우기 불투명도 (%)", canvas.Fill.A * 100 / 255, 0, 100);
            if (fill.HasValue) canvas.Fill = Color.FromArgb(fill.Value * 255 / 100, canvas.Fill);
        }

        private int? NumberDialog(string title, int value, int min, int max)
        {
            using (var dialog = new Form { Text = title, StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(265, 83),
                FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false })
            {
                var number = new NumericUpDown { Left = 12, Top = 12, Width = 231, Minimum = min, Maximum = max, Value = value };
                var ok = new Button { Text = "적용", DialogResult = DialogResult.OK, Left = 167, Top = 45 };
                dialog.Controls.AddRange(new Control[] { number, ok }); dialog.AcceptButton = ok;
                return dialog.ShowDialog(this) == DialogResult.OK ? (int?)number.Value : null;
            }
        }

        private void Emoji()
        {
            using (var dialog = new Form { Text = "이모지", StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(335, 118),
                FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false })
            {
                var row = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 55 };
                var input = new TextBox { Left = 10, Top = 65, Width = 220, Font = new Font("Segoe UI Emoji", 12) };
                foreach (string value in new[] { "⭐", "❤️", "👍", "✅", "❌", "😊", "🎉" })
                {
                    string emoji = value;
                    var key = new Button { Text = emoji, Width = 40, Height = 37, Font = input.Font };
                    key.Click += delegate { input.Text = emoji; };
                    row.Controls.Add(key);
                }
                var ok = new Button { Text = "추가", DialogResult = DialogResult.OK, Left = 241, Top = 65 };
                dialog.Controls.AddRange(new Control[] { row, input, ok }); dialog.AcceptButton = ok;
                if (dialog.ShowDialog(this) == DialogResult.OK) canvas.InsertEmoji(input.Text);
            }
        }

        private void Play()
        {
            if (video == null) return;
            if (playing) player.Pause(); else player.Play();
            playing = !playing;
        }

        private void UpdateSeek()
        {
            if (video == null || duration <= TimeSpan.Zero) return;
            updatingSeek = true;
            seek.Value = Math.Max(0, Math.Min(1000, (int)(player.Position.TotalMilliseconds * 1000 / duration.TotalMilliseconds)));
            updatingSeek = false;
            status.Text = player.Position.ToString(@"mm\:ss") + " / " + duration.ToString(@"mm\:ss");
        }

        private async void Trim()
        {
            if (duration.TotalSeconds < 0.1 || video == null) return;
            string source = video;
            using (var dialog = new Form { Text = "화면 녹화 자르기", StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(330, 124),
                FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false })
            {
                decimal max = (decimal)duration.TotalSeconds;
                var start = new NumericUpDown { Left = 150, Top = 12, Width = 152, DecimalPlaces = 2, Maximum = max, Increment = 0.1m };
                var end = new NumericUpDown { Left = 150, Top = 47, Width = 152, DecimalPlaces = 2, Maximum = max, Value = max, Increment = 0.1m };
                var apply = new Button { Left = 225, Top = 83, Text = "적용", DialogResult = DialogResult.OK };
                dialog.Controls.AddRange(new Control[] {
                    new Label { Left = 15, Top = 15, Text = "시작(초)" }, new Label { Left = 15, Top = 50, Text = "끝(초)" }, start, end, apply });
                dialog.AcceptButton = apply;
                if (dialog.ShowDialog(this) != DialogResult.OK || end.Value - start.Value < 0.1m) return;
                string folder = MediaTools.TempFolder; Directory.CreateDirectory(folder);
                string output = Path.Combine(folder, "Trimmed.mp4");
                try
                {
                    double from = (double)start.Value, length = (double)(end.Value - start.Value);
                    await Task.Run(() => MediaTools.Trim(source, output, from, length));
                    if (IsDisposed || video != source) return;
                    ShowVideo(output, true);
                    if (settings.CopyVideoEdits) app.CopyFile(output);
                }
                catch (Exception error) { Error(error); }
            }
        }

        private async void ExportGif(bool high, bool copy)
        {
            if (video == null) return;
            string source = video, path;
            if (copy)
            {
                string folder = MediaTools.TempFolder; Directory.CreateDirectory(folder);
                path = Path.Combine(folder, "Recording.gif");
            }
            else using (var dialog = new SaveFileDialog { Filter = "GIF|*.gif", FileName = "Recording.gif" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                path = dialog.FileName;
            }
            if (duration.TotalSeconds > 30 && MessageBox.Show(this, "처음 30초를 GIF로 내보냅니다.", Text,
                MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            try
            {
                await Task.Run(() => MediaTools.Gif(source, path, high));
                if (copy) app.CopyFile(path);
                else SetStatus("GIF 저장됨");
            }
            catch (Exception error) { Error(error); }
        }

        private async void Clipchamp()
        {
            const string clipchamp = "Clipchamp.Clipchamp_yxz26nhyzhsrt";
            string path = video;
            try
            {
                var file = await WindowsAsync.Wait(Windows.Storage.StorageFile.GetFileFromPathAsync(path));
                var options = new Windows.System.LauncherOptions { TargetApplicationPackageFamilyName = clipchamp };
                if (await WindowsAsync.Wait(Windows.System.Launcher.LaunchFileAsync(file, options))) return;
            }
            catch (Exception) { }
            app.CopyFile(path);
            Launch("shell:AppsFolder\\" + clipchamp + "!App");
        }

        private void Launch(string address)
        {
            try { Process.Start(new ProcessStartInfo(address) { UseShellExecute = true }); }
            catch (Exception error) { Error(error); }
        }

        public bool CanReplace()
        {
            if (!dirty || (video != null ? !settings.AskSaveVideo : !settings.AskSave)) return true;
            DialogResult response = MessageBox.Show(this, "편집 내용을 저장하시겠습니까?", Text, MessageBoxButtons.YesNoCancel);
            return response == DialogResult.No || (response == DialogResult.Yes && Save());
        }

        public void ApplyTheme()
        {
            bool dark = settings.Dark;
            BackColor = dark ? Color.FromArgb(35, 35, 35) : Color.FromArgb(250, 250, 250);
            ForeColor = dark ? Color.FromArgb(246, 246, 246) : Color.FromArgb(25, 25, 25);
            canvas.BackColor = dark ? Color.FromArgb(30, 30, 30) : Color.FromArgb(235, 235, 235);
            foreach (ToolStrip bar in new[] { top, edits, playback })
            {
                bar.BackColor = dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(248, 248, 248);
                bar.ForeColor = ForeColor;
                bar.Renderer = new CaptureBarRenderer(dark);
            }
            if (IsHandleCreated)
            {
                int enabled = dark ? 1 : 0;
                DwmSetWindowAttribute(Handle, 20, ref enabled, 4);
            }
        }

        protected override void OnHandleCreated(EventArgs args)
        { base.OnHandleCreated(args); ApplyTheme(); }

        protected override bool ProcessCmdKey(ref Message message, Keys keys)
        {
            switch (keys)
            {
                case Keys.Alt | Keys.N: app.FromEditor(this); return true;
                case Keys.Alt | Keys.M: mode.ShowDropDown(); return true;
                case Keys.Alt | Keys.D: delay.ShowDropDown(); return true;
                case Keys.Control | Keys.O: OpenFile(); return true;
                case Keys.Control | Keys.S: Save(); return true;
                case Keys.Control | Keys.C: Copy(); return true;
                case Keys.Control | Keys.Z: canvas.Undo(); return true;
                case Keys.Control | Keys.Y: canvas.Redo(); return true;
                case Keys.Control | Keys.R: Measurement(1); return true;
                case Keys.Control | Keys.P: if (video == null && canvas.HasImage) Print(); return true;
                case Keys.Enter: if (canvas.CropPending) { canvas.CommitCrop(); return true; } break;
            }
            return base.ProcessCmdKey(ref message, keys);
        }

        public void CloseForExit() { permittedClose = true; Close(); }
        protected override void OnFormClosing(FormClosingEventArgs args)
        {
            if (!permittedClose && args.CloseReason == CloseReason.UserClosing)
            {
                args.Cancel = true;
                if (CanReplace()) { dirty = false; if (playing) Play(); Hide(); }
            }
            base.OnFormClosing(args);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { refresh.Dispose(); StopVideo(); }
            base.Dispose(disposing);
        }

        private void SetStatus(string message) { Text = "QuickCapture  |  " + message; }
        private void Error(Exception error) { if (!IsDisposed) MessageBox.Show(this, error.Message, "QuickCapture"); }
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    }

    internal sealed class CaptureBarRenderer : ToolStripSystemRenderer
    {
        private readonly bool dark;
        public CaptureBarRenderer(bool dark) { this.dark = dark; }
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs args)
        {
            if (!(args.ToolStrip is ToolStripDropDown))
            {
                using (var pen = new Pen(dark ? Color.FromArgb(51, 51, 51) : Color.FromArgb(221, 221, 221)))
                    args.Graphics.DrawLine(pen, 0, args.ToolStrip.Height - 1, args.ToolStrip.Width, args.ToolStrip.Height - 1);
            }
            else base.OnRenderToolStripBorder(args);
        }
        protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs args)
        {
            var button = (ToolStripButton)args.Item;
            bool primary = button.AccessibleName == "새 캡처";
            if (!button.Checked && !button.Selected && !button.Pressed && !primary) return;
            var bounds = new Rectangle(0, 0, button.Width - 1, button.Height - 1);
            using (var fill = new SolidBrush(dark ? Color.FromArgb(button.Selected ? 56 : 44, button.Selected ? 56 : 44, button.Selected ? 56 : 44) :
                Color.FromArgb(button.Selected ? 227 : 239, button.Selected ? 227 : 239, button.Selected ? 227 : 239)))
                args.Graphics.FillRectangle(fill, bounds);
            if (button.Checked)
            {
                using (var accent = new SolidBrush(dark ? Color.FromArgb(67, 192, 255) : Color.FromArgb(0, 100, 190)))
                    args.Graphics.FillRectangle(accent, bounds.Width / 2 - 7, bounds.Bottom - 2, 14, 3);
            }
        }
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs args)
        {
            if (!(args.Item.Owner is ToolStripDropDown)) args.ArrowColor = args.Item.Owner.ForeColor;
            base.OnRenderArrow(args);
        }
    }
}
