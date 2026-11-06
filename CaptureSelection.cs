using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace QuickCapture
{
    internal enum CaptureMode { Rectangle, Window, Fullscreen, Freeform, Text, Color, Perfect }
    internal enum CaptureAction { Image = 1, Video = 2, Text = 3, Open = 4 }

    internal sealed class SelectionOverlay : Form
    {
        private readonly Bitmap snapshot;
        private readonly Rectangle desktop;
        private readonly List<Rectangle> windows;
        private readonly List<Point> path = new List<Point>();
        private readonly FlowLayoutPanel toolbar;
        private readonly Label guide;
        private readonly ComboBox modes;
        private readonly ComboBox colorFormats;
        private readonly Button image;
        private readonly Button video;
        private readonly Button quickButton;
        private readonly CheckBox savePng;
        private readonly CheckBox adjustAfterSelection;
        private readonly Button confirm;
        private readonly Button reselect;
        private FlowLayoutPanel markupBar;
        private MarkupCanvas quickCanvas;
        private bool quickMarkup;
        private Point anchor;
        private Point pointer;
        private Rectangle pathBounds;
        private bool dragging;
        private bool ready;
        private SelectionEdge adjustment;
        private Rectangle dragStart;
        private int magnification = 10;

        public Rectangle Selection { get; private set; }
        public Rectangle ScreenSelection { get { return new Rectangle(Selection.X + desktop.X, Selection.Y + desktop.Y, Selection.Width, Selection.Height); } }
        public CaptureMode Mode { get; private set; }
        public bool Recording { get; private set; }
        public bool SavePng { get { return savePng.Checked; } }
        public bool CanCopyAndClose { get { return !Recording && (ready || quickCanvas != null); } }
        public bool CopyAndCloseRequested { get; private set; }
        public string PickedColor { get; private set; }
        public event Action CopyAvailabilityChanged;

        public SelectionOverlay(Bitmap snapshot, Rectangle desktop, Rectangle monitor, CaptureMode mode, bool recording,
            bool saveByDefault, bool adjustByDefault)
        {
            this.snapshot = snapshot;
            this.desktop = desktop;
            windows = WindowBounds.Read(desktop);
            Recording = recording;
            Mode = mode;
            pointer = Clamp(new Point(Cursor.Position.X - desktop.X, Cursor.Position.Y - desktop.Y));
            StartPosition = FormStartPosition.Manual;
            FormBorderStyle = FormBorderStyle.None;
            AutoScaleMode = AutoScaleMode.None;
            ShowInTaskbar = false;
            TopMost = true;
            Cursor = Cursors.Cross;
            Text = "QuickCapture - 영역 선택";
            Bounds = desktop;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint | ControlStyles.Opaque, true);

            toolbar = new FlowLayoutPanel
            {
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false,
                Padding = new Padding(5), BackColor = Color.FromArgb(32, 32, 32), Cursor = Cursors.Default
            };
            image = Tool("스크린샷", "\ue722", () => Switch(false));
            video = Tool("화면 녹화", "\ue714", () => Switch(true));
            modes = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120,
                Margin = new Padding(8, 5, 3, 3), BackColor = toolbar.BackColor, ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat, AccessibleName = "캡처 영역" };
            modes.SelectedIndexChanged += delegate { if (modes.SelectedItem != null) ChangeMode((CaptureMode)((ModeItem)modes.SelectedItem).Value); };
            toolbar.Controls.Add(modes);
            Tool("텍스트 추출기", "\ue8a5", () => Choose(CaptureMode.Text));
            Tool("색상 선택기", "\uef3c", () => Choose(CaptureMode.Color));
            Tool("자동 맞춤 캡처", "\ue91b", () => Choose(CaptureMode.Perfect));
            quickButton = Tool("빠른 태그 (Ctrl+E)", "\ue70f", () => ToggleMarkup());
            quickButton.Enabled = !Recording;
            colorFormats = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 58,
                Margin = new Padding(3, 5, 3, 3), FlatStyle = FlatStyle.Flat, AccessibleName = "색상 형식" };
            colorFormats.Items.AddRange(new object[] { "HEX", "RGB", "HSL" });
            colorFormats.SelectedIndex = 0;
            toolbar.Controls.Add(colorFormats);
            savePng = Option("PNG 저장", saveByDefault);
            adjustAfterSelection = Option("선택 후 조정", adjustByDefault);
            confirm = TextButton("완료", ConfirmSelection);
            reselect = TextButton("다시 선택", () => ChangeMode(Mode));
            Tool("닫기", "\ue711", () => DialogResult = DialogResult.Cancel);
            Controls.Add(toolbar);
            guide = new Label
            {
                AutoSize = true, BackColor = toolbar.BackColor, ForeColor = Color.White,
                Font = new Font("맑은 고딕", 10f), Padding = new Padding(8, 5, 8, 5)
            };
            Controls.Add(guide);
            RefreshModes(mode);
            monitor.Offset(-desktop.X, -desktop.Y);
            toolbar.Location = new Point(monitor.Left + (monitor.Width - toolbar.PreferredSize.Width) / 2, monitor.Top + 12);
            guide.Location = new Point(toolbar.Left, toolbar.Bottom + 6);
            UpdateGuide();
        }

        private CheckBox Option(string label, bool value)
        {
            var option = new CheckBox { Text = label, Checked = value, AutoSize = true,
                ForeColor = Color.White, Margin = new Padding(8, 7, 0, 0) };
            (markupBar ?? toolbar).Controls.Add(option);
            return option;
        }

        private Button TextButton(string label, Action action)
        {
            var button = new Button { Text = label, AutoSize = true, Height = 31, FlatStyle = FlatStyle.Flat,
                ForeColor = Color.White, BackColor = Color.FromArgb(57, 57, 57), Margin = new Padding(4, 0, 0, 0) };
            button.FlatAppearance.BorderSize = 0;
            button.Click += delegate { action(); };
            toolbar.Controls.Add(button);
            return button;
        }

        private Button Tool(string name, string glyph, Action click)
        {
            var button = new Button { Text = glyph, AccessibleName = name, Width = 36, Height = 32,
                FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = toolbar.BackColor,
                Font = new Font("Segoe Fluent Icons", 13f), Margin = new Padding(2, 0, 2, 0) };
            button.FlatAppearance.BorderSize = 0;
            button.Click += delegate { click(); };
            (markupBar ?? toolbar).Controls.Add(button);
            return button;
        }

        private void Switch(bool recording)
        {
            if (Recording == recording)
                return;
            Recording = recording;
            quickButton.Enabled = !recording;
            RefreshModes(CaptureMode.Rectangle);
        }

        private void Choose(CaptureMode mode)
        {
            if (Recording)
                Switch(false);
            for (int i = 0; i < modes.Items.Count; i++)
                if (((ModeItem)modes.Items[i]).Value == mode)
                    modes.SelectedIndex = i;
        }

        private void RefreshModes(CaptureMode mode)
        {
            modes.Items.Clear();
            modes.Items.AddRange(Recording ? ModeItem.VideoModes : ModeItem.ImageModes);
            image.BackColor = Recording ? toolbar.BackColor : Color.FromArgb(62, 62, 62);
            video.BackColor = Recording ? Color.FromArgb(62, 62, 62) : toolbar.BackColor;
            modes.SelectedIndex = 0;
            Choose(mode == CaptureMode.Window || !Recording ? mode : CaptureMode.Rectangle);
        }

        private void ChangeMode(CaptureMode mode)
        {
            Mode = mode;
            dragging = ready = false;
            adjustment = SelectionEdge.None;
            Cursor = Cursors.Cross;
            path.Clear();
            Selection = Mode == CaptureMode.Fullscreen ? new Rectangle(Point.Empty, snapshot.Size) : Rectangle.Empty;
            colorFormats.Visible = mode == CaptureMode.Color;
            bool screenshot = !Recording && mode != CaptureMode.Text && mode != CaptureMode.Color;
            savePng.Visible = screenshot;
            adjustAfterSelection.Visible = screenshot && mode != CaptureMode.Freeform;
            confirm.Visible = reselect.Visible = false;
            if (guide != null)
                UpdateGuide();
            if (CopyAvailabilityChanged != null)
                CopyAvailabilityChanged();
            Invalidate();
        }

        private void UpdateGuide()
        {
            guide.Text = ready ? Selection.Width + " × " + Selection.Height +
                " px   |   드래그로 이동 및 크기 조정   |   Enter로 완료, Ctrl+C로 복사 후 닫기" :
                Mode == CaptureMode.Window ? "캡처할 창 클릭" :
                Mode == CaptureMode.Fullscreen ? "화면을 클릭하거나 Enter" :
                Mode == CaptureMode.Color ? "클릭하여 색상 복사   |   휠로 확대" :
                Mode == CaptureMode.Text ? "텍스트가 있는 영역을 드래그" :
                Mode == CaptureMode.Freeform ? "자유형 영역 그리기" : "캡처할 영역을 드래그";
        }

        protected override void OnLoad(EventArgs args)
        {
            SetWindowPos(Handle, IntPtr.Zero, desktop.X, desktop.Y, desktop.Width, desktop.Height, 0x0014);
            base.OnLoad(args);
        }

        protected override void OnShown(EventArgs args)
        {
            base.OnShown(args);
            SetForegroundWindow(Handle);
        }

        protected override void OnPaint(PaintEventArgs args)
        {
            Graphics g = args.Graphics;
            g.DrawImageUnscaled(snapshot, 0, 0);
            using (var shade = new SolidBrush(Color.FromArgb(105, Color.Black)))
                g.FillRectangle(shade, args.ClipRectangle);
            if (Mode == CaptureMode.Freeform && path.Count > 1)
            {
                if (path.Count > 2)
                {
                    using (var clip = new GraphicsPath())
                    {
                        clip.AddPolygon(path.ToArray());
                        var state = g.Save();
                        g.SetClip(clip, CombineMode.Intersect);
                        g.DrawImageUnscaled(snapshot, 0, 0);
                        g.Restore(state);
                    }
                }
                g.DrawLines(Pens.White, path.ToArray());
            }
            else if (!Selection.IsEmpty)
            {
                g.DrawImage(snapshot, Selection, Selection, GraphicsUnit.Pixel);
                g.DrawRectangle(Pens.White, Selection.X, Selection.Y, Selection.Width - 1, Selection.Height - 1);
                if (ready)
                    PaintHandles(g);
            }
            if (Mode == CaptureMode.Color)
                DrawLoupe(g);
        }

        private void PaintHandles(Graphics graphics)
        {
            int x = Selection.X, y = Selection.Y, right = Selection.Right - 1, bottom = Selection.Bottom - 1;
            int centerX = x + Selection.Width / 2, centerY = y + Selection.Height / 2;
            foreach (Point point in new[] { new Point(x, y), new Point(centerX, y), new Point(right, y),
                new Point(x, centerY), new Point(right, centerY), new Point(x, bottom),
                new Point(centerX, bottom), new Point(right, bottom) })
            {
                var handle = new Rectangle(point.X - 4, point.Y - 4, 8, 8);
                graphics.FillRectangle(Brushes.White, handle);
                graphics.DrawRectangle(Pens.DodgerBlue, handle);
            }
        }

        private void DrawLoupe(Graphics g)
        {
            var loupe = new Rectangle(Math.Max(0, Math.Min(pointer.X + 22, Width - 126)),
                Math.Max(0, Math.Min(pointer.Y + 22, Height - 110)), 118, 98);
            int side = 13;
            int sample = Math.Max(3, side * 7 / magnification);
            Rectangle source = Rectangle.Intersect(new Rectangle(pointer.X - sample / 2, pointer.Y - sample / 2, sample, sample),
                new Rectangle(Point.Empty, snapshot.Size));
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.FillRectangle(Brushes.Black, loupe);
            g.DrawImage(snapshot, new Rectangle(loupe.X + 20, loupe.Y + 2, 78, 70), source, GraphicsUnit.Pixel);
            g.DrawRectangle(Pens.White, loupe.X + 55, loupe.Y + 33, 8, 8);
            TextRenderer.DrawText(g, FormatColor(snapshot.GetPixel(pointer.X, pointer.Y)), Font,
                new Rectangle(loupe.X, loupe.Bottom - 24, loupe.Width, 24), Color.White, TextFormatFlags.HorizontalCenter);
        }

        protected override void OnMouseDown(MouseEventArgs args)
        {
            base.OnMouseDown(args);
            if (args.Button == MouseButtons.Right) { DialogResult = DialogResult.Cancel; return; }
            if (args.Button != MouseButtons.Left || quickCanvas != null) return;
            pointer = Clamp(args.Location);
            if (ready)
            {
                adjustment = SelectionGeometry.Hit(Selection, pointer);
                if (adjustment != SelectionEdge.None)
                {
                    anchor = pointer;
                    dragStart = Selection;
                    dragging = true;
                    Capture = true;
                }
                return;
            }
            if (Mode == CaptureMode.Color)
            {
                PickedColor = FormatColor(snapshot.GetPixel(pointer.X, pointer.Y));
                DialogResult = DialogResult.OK;
                return;
            }
            if (Mode == CaptureMode.Window || Mode == CaptureMode.Fullscreen)
            {
                if (Mode == CaptureMode.Window) Selection = windows.Find(rect => rect.Contains(pointer));
                if (!Selection.IsEmpty) ReviewOrConfirm(false);
                return;
            }
            anchor = pointer;
            path.Clear();
            path.Add(anchor);
            pathBounds = new Rectangle(anchor, new Size(1, 1));
            dragging = true;
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs args)
        {
            base.OnMouseMove(args);
            if (quickCanvas != null) return;
            Point previousPointer = pointer;
            pointer = Clamp(args.Location);
            Rectangle before = Selection;
            if (ready)
            {
                if (dragging) UpdateAdjustment(pointer);
                else Cursor = ResizeCursor(SelectionGeometry.Hit(Selection, pointer));
                return;
            }
            if (Mode == CaptureMode.Window)
                Selection = windows.Find(rect => rect.Contains(pointer));
            else if (dragging)
            {
                if (Mode == CaptureMode.Freeform && path[path.Count - 1] != pointer)
                {
                    path.Add(pointer);
                    pathBounds = Rectangle.Union(pathBounds, new Rectangle(pointer, new Size(1, 1)));
                }
                else
                    Selection = FromPoints(anchor, pointer);
            }

            if (Mode == CaptureMode.Color)
            {
                Invalidate(new Rectangle(previousPointer.X - 132, previousPointer.Y - 116, 274, 248));
                Invalidate(new Rectangle(pointer.X - 132, pointer.Y - 116, 274, 248));
            }
            else if (dragging && Mode == CaptureMode.Freeform)
                Invalidate(Rectangle.Inflate(pathBounds, 3, 3));
            else if (before != Selection)
            {
                Invalidate(before);
                Invalidate(Selection);
            }
        }

        protected override void OnMouseUp(MouseEventArgs args)
        {
            base.OnMouseUp(args);
            if (!dragging || args.Button != MouseButtons.Left) return;
            dragging = false;
            Capture = false;
            Point end = Clamp(args.Location);
            if (ready)
            {
                UpdateAdjustment(end);
                adjustment = SelectionEdge.None;
                Cursor = ResizeCursor(SelectionGeometry.Hit(Selection, end));
                return;
            }
            if (Mode == CaptureMode.Freeform)
            {
                path.Add(end);
                if (path.Count < 3) return;
                using (var polygon = new GraphicsPath())
                {
                    polygon.AddPolygon(path.ToArray());
                    Selection = Rectangle.Intersect(Rectangle.Ceiling(polygon.GetBounds()), new Rectangle(Point.Empty, snapshot.Size));
                }
            }
            else Selection = FromPoints(anchor, end);
            if (Selection.Width < 2 || Selection.Height < 2) return;
            bool fit = Mode == CaptureMode.Perfect ||
                (Mode == CaptureMode.Rectangle && !Recording && (ModifierKeys & Keys.Control) != 0);
            ReviewOrConfirm(fit);
        }

        protected override void OnMouseCaptureChanged(EventArgs args)
        {
            base.OnMouseCaptureChanged(args);
            if (dragging && !Capture)
            {
                dragging = false;
                if (ready)
                {
                    adjustment = SelectionEdge.None;
                    return;
                }
                path.Clear();
                Selection = Rectangle.Empty;
                Invalidate();
            }
        }

        protected override void OnMouseWheel(MouseEventArgs args)
        {
            base.OnMouseWheel(args);
            if (Mode == CaptureMode.Color) Zoom(Math.Sign(args.Delta));
        }

        protected override bool ProcessCmdKey(ref Message message, Keys key)
        {
            if (key == Keys.Escape) { DialogResult = DialogResult.Cancel; return true; }
            if (key == (Keys.Control | Keys.C) && CanCopyAndClose)
            {
                if (quickCanvas != null && quickCanvas.CropPending)
                    quickCanvas.CommitCrop();
                CopyAndCloseRequested = true;
                DialogResult = DialogResult.OK;
                return true;
            }
            if (key == (Keys.Control | Keys.E) && !Recording && quickCanvas == null)
            { ToggleMarkup(); return true; }
            if (key == Keys.Enter && quickCanvas != null)
            {
                if (quickCanvas.CropPending) quickCanvas.CommitCrop();
                else DialogResult = DialogResult.OK;
                return true;
            }
            if (key == Keys.Enter && !Selection.IsEmpty)
            {
                if (ready) { ConfirmSelection(); return true; }
                if (Mode == CaptureMode.Fullscreen || Mode == CaptureMode.Window)
                { ReviewOrConfirm(false); return true; }
            }
            if (Mode == CaptureMode.Color && (key == (Keys.Control | Keys.Add) || key == (Keys.Control | Keys.Oemplus)))
            { Zoom(1); return true; }
            if (Mode == CaptureMode.Color && (key == (Keys.Control | Keys.Subtract) || key == (Keys.Control | Keys.OemMinus)))
            { Zoom(-1); return true; }
            return base.ProcessCmdKey(ref message, key);
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == HotkeyHook.OverlayKeyMessage && Visible)
            {
                ProcessCmdKey(ref message, (Keys)message.WParam.ToInt32());
                return;
            }
            if (message.Msg == 0x007e && Visible)
                DialogResult = DialogResult.Cancel;
            base.WndProc(ref message);
        }

        public Bitmap MakeImage()
        {
            if (quickCanvas != null) return quickCanvas.Render();
            if (Mode != CaptureMode.Freeform)
                return snapshot.Clone(Selection, PixelFormat.Format24bppRgb);
            var result = new Bitmap(Selection.Width, Selection.Height, PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(result))
            using (var brush = new TextureBrush(snapshot))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.TranslateTransform(-Selection.X, -Selection.Y);
                graphics.FillPolygon(brush, path.ToArray());
            }
            return result;
        }

        private void ToggleMarkup()
        {
            if (Recording) return;
            quickMarkup = !quickMarkup;
            quickButton.BackColor = quickMarkup ? Color.FromArgb(63, 81, 99) : toolbar.BackColor;
        }

        private void ReviewOrConfirm(bool fit)
        {
            if (!fit && (Recording || !adjustAfterSelection.Checked || Mode == CaptureMode.Freeform || Mode == CaptureMode.Text))
            {
                ConfirmSelection();
                return;
            }
            if (fit) Selection = ContentFrame.Find(snapshot, Selection);
            ready = true;
            confirm.Visible = reselect.Visible = true;
            UpdateGuide();
            if (CopyAvailabilityChanged != null)
                CopyAvailabilityChanged();
            Invalidate();
        }

        private void UpdateAdjustment(Point end)
        {
            Rectangle previous = Selection;
            Selection = SelectionGeometry.Adjust(dragStart, end.X - anchor.X, end.Y - anchor.Y, adjustment, snapshot.Size);
            if (previous == Selection) return;
            UpdateGuide();
            Invalidate(Rectangle.Inflate(Rectangle.Union(previous, Selection), 6, 6));
        }

        private static Cursor ResizeCursor(SelectionEdge edge)
        {
            switch (edge)
            {
                case SelectionEdge.Move: return Cursors.SizeAll;
                case SelectionEdge.Left:
                case SelectionEdge.Right: return Cursors.SizeWE;
                case SelectionEdge.Top:
                case SelectionEdge.Bottom: return Cursors.SizeNS;
                case SelectionEdge.Left | SelectionEdge.Top:
                case SelectionEdge.Right | SelectionEdge.Bottom: return Cursors.SizeNWSE;
                case SelectionEdge.Right | SelectionEdge.Top:
                case SelectionEdge.Left | SelectionEdge.Bottom: return Cursors.SizeNESW;
                default: return Cursors.Cross;
            }
        }

        private void ConfirmSelection()
        {
            if (!quickMarkup || Recording || Mode == CaptureMode.Text)
            {
                DialogResult = DialogResult.OK;
                return;
            }
            Bitmap selected = MakeImage();
            quickCanvas = new MarkupCanvas { Bounds = Selection, EdgePadding = 0, BackColor = Color.FromArgb(34, 34, 34) };
            quickCanvas.LoadImage(selected);
            Controls.Add(quickCanvas);
            quickCanvas.BringToFront();
            toolbar.Hide(); guide.Hide();
            markupBar = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false,
                Padding = new Padding(5), BackColor = toolbar.BackColor, Cursor = Cursors.Default, Location = toolbar.Location };
            Tool("펜", "\ue70f", () => { quickCanvas.Ink = Color.OrangeRed; quickCanvas.SetTool(MarkTool.Pen); });
            Tool("형광펜", "\ue7e6", () => { quickCanvas.Ink = Color.Gold; quickCanvas.SetTool(MarkTool.Marker); });
            Tool("지우개", "\ue75c", () => quickCanvas.SetTool(MarkTool.Eraser));
            Button shapes = Tool("도형과 이모지", "\uf158", () => { });
            var menu = new ContextMenuStrip();
            foreach (MarkTool tool in new[] { MarkTool.Rectangle, MarkTool.Ellipse, MarkTool.Line, MarkTool.Arrow })
            {
                MarkTool shape = tool;
                string label = tool == MarkTool.Rectangle ? "사각형" : tool == MarkTool.Ellipse ? "타원" : tool == MarkTool.Line ? "선" : "화살표";
                menu.Items.Add(label, null, delegate { quickCanvas.SetTool(shape); });
            }
            menu.Items.Add("이모지", null, delegate { quickCanvas.InsertEmoji("⭐"); });
            menu.Items.Add("이동 및 크기 조정", null, delegate { quickCanvas.SetTool(MarkTool.Select); });
            shapes.ContextMenuStrip = menu;
            shapes.Click += delegate { menu.Show(shapes, new Point(0, shapes.Height)); };
            Tool("영역 자르기", "\ue7a8", () =>
            {
                if (quickCanvas.CropPending) quickCanvas.CommitCrop();
                else quickCanvas.SetTool(MarkTool.Crop);
            });
            Tool("실행 취소", "\ue7a7", () => quickCanvas.Undo());
            Tool("다시 실행", "\ue7a6", () => quickCanvas.Redo());
            CheckBox markupSave = Option("PNG 저장", SavePng);
            markupSave.CheckedChanged += delegate { savePng.Checked = markupSave.Checked; };
            Tool("캡처 완료", "\ue73e", () => DialogResult = DialogResult.OK);
            Tool("취소", "\ue711", () => DialogResult = DialogResult.Cancel);
            Controls.Add(markupBar);
            markupBar.BringToFront();
            quickCanvas.Focus();
            if (CopyAvailabilityChanged != null)
                CopyAvailabilityChanged();
        }

        private void Zoom(int delta)
        {
            magnification = Math.Max(4, Math.Min(24, magnification + delta));
            Invalidate();
        }

        private string FormatColor(Color value)
        {
            if (colorFormats.SelectedIndex == 1)
                return string.Format(CultureInfo.InvariantCulture, "rgb({0}, {1}, {2})", value.R, value.G, value.B);
            if (colorFormats.SelectedIndex == 2)
                return string.Format(CultureInfo.InvariantCulture, "hsl({0:F0}, {1:P0}, {2:P0})",
                    value.GetHue(), value.GetSaturation(), value.GetBrightness()).Replace(" %", "%");
            return string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", value.R, value.G, value.B);
        }

        private Point Clamp(Point p)
        {
            return new Point(Math.Max(0, Math.Min(snapshot.Width - 1, p.X)), Math.Max(0, Math.Min(snapshot.Height - 1, p.Y)));
        }

        internal static Rectangle FromPoints(Point first, Point last)
        {
            return Rectangle.FromLTRB(Math.Min(first.X, last.X), Math.Min(first.Y, last.Y),
                Math.Max(first.X, last.X), Math.Max(first.Y, last.Y));
        }

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int w, int h, uint flags);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);
    }

    [Flags]
    internal enum SelectionEdge { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8, Move = 16 }

    internal static class SelectionGeometry
    {
        public static SelectionEdge Hit(Rectangle area, Point point)
        {
            const int radius = 7;
            if (!Rectangle.Inflate(area, radius, radius).Contains(point)) return SelectionEdge.None;
            int left = Math.Abs(point.X - area.Left), right = Math.Abs(point.X - area.Right + 1);
            int top = Math.Abs(point.Y - area.Top), bottom = Math.Abs(point.Y - area.Bottom + 1);
            SelectionEdge edge = SelectionEdge.None;
            if (Math.Min(left, right) <= radius) edge |= left <= right ? SelectionEdge.Left : SelectionEdge.Right;
            if (Math.Min(top, bottom) <= radius) edge |= top <= bottom ? SelectionEdge.Top : SelectionEdge.Bottom;
            return edge == SelectionEdge.None && area.Contains(point) ? SelectionEdge.Move : edge;
        }

        public static Rectangle Adjust(Rectangle area, int dx, int dy, SelectionEdge edge, Size bounds)
        {
            if (edge == SelectionEdge.Move)
                return new Rectangle(Clamp(area.X + dx, 0, bounds.Width - area.Width),
                    Clamp(area.Y + dy, 0, bounds.Height - area.Height), area.Width, area.Height);

            int left = area.Left, top = area.Top, right = area.Right, bottom = area.Bottom;
            if ((edge & SelectionEdge.Left) != 0) left = Clamp(left + dx, 0, right - 2);
            if ((edge & SelectionEdge.Right) != 0) right = Clamp(right + dx, left + 2, bounds.Width);
            if ((edge & SelectionEdge.Top) != 0) top = Clamp(top + dy, 0, bottom - 2);
            if ((edge & SelectionEdge.Bottom) != 0) bottom = Clamp(bottom + dy, top + 2, bounds.Height);
            return Rectangle.FromLTRB(left, top, right, bottom);
        }

        private static int Clamp(int value, int min, int max) { return Math.Max(min, Math.Min(max, value)); }
    }

    internal sealed class ModeItem
    {
        public readonly CaptureMode Value;
        private readonly string label;
        private ModeItem(CaptureMode value, string label) { Value = value; this.label = label; }
        public override string ToString() { return label; }

        public static readonly ModeItem[] ImageModes = {
            new ModeItem(CaptureMode.Rectangle, "직사각형"), new ModeItem(CaptureMode.Window, "창"),
            new ModeItem(CaptureMode.Fullscreen, "전체 화면"), new ModeItem(CaptureMode.Freeform, "자유형"),
            new ModeItem(CaptureMode.Text, "텍스트 추출기"), new ModeItem(CaptureMode.Color, "색상 선택기"),
            new ModeItem(CaptureMode.Perfect, "자동 맞춤 캡처")
        };
        public static readonly ModeItem[] VideoModes = { ImageModes[0], ImageModes[1] };
    }

    internal static class ContentFrame
    {
        public static Rectangle Find(Bitmap source, Rectangle area)
        {
            // Estimate the solid background from the perimeter and keep the non-background pixels.
            var inset = Rectangle.Inflate(area, -2, -2);
            if (inset.Width < 20 || inset.Height < 20) return area;
            Color background = source.GetPixel(area.Left + 1, area.Top + 1);
            int step = Math.Max(1, Math.Max(area.Width, area.Height) / 400);
            int matching = 0;
            for (int x = inset.Left; x < inset.Right; x += step)
                if (Difference(background, source.GetPixel(x, area.Top + 1)) < 30) matching++;
            if (matching < (inset.Width / step) * 0.8) return area;
            Rectangle content = Rectangle.Empty;
            for (int y = inset.Top; y < inset.Bottom; y += step)
                for (int x = inset.Left; x < inset.Right; x += step)
                    if (Difference(background, source.GetPixel(x, y)) > 70)
                    {
                        var pixel = new Rectangle(x, y, step, step);
                        content = content.IsEmpty ? pixel : Rectangle.Union(content, pixel);
                    }
            return content.Width < 10 || content.Height < 10 ? area :
                Rectangle.Intersect(Rectangle.Inflate(content, Math.Max(4, step * 2), Math.Max(4, step * 2)), area);
        }

        private static int Difference(Color a, Color b)
        { return Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B); }
    }

    internal static class WindowBounds
    {
        public static List<Rectangle> Read(Rectangle desktop)
        {
            var output = new List<Rectangle>();
            EnumWindows(delegate(IntPtr hwnd, IntPtr unused)
            {
                if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                if (pid == GetCurrentProcessId()) return true;
                var name = new StringBuilder(128);
                GetClassName(hwnd, name, name.Capacity);
                if (name.ToString() == "Progman" || name.ToString() == "WorkerW" || name.ToString().StartsWith("Shell_"))
                    return true;
                int cloaked;
                if (DwmGetWindowAttribute(hwnd, 14, out cloaked, 4) == 0 && cloaked != 0) return true;
                NativeRect rect;
                if (DwmGetWindowAttribute(hwnd, 9, out rect, 16) != 0 && !GetWindowRect(hwnd, out rect)) return true;
                var area = Rectangle.Intersect(desktop, Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom));
                if (area.Width >= 20 && area.Height >= 20)
                {
                    area.Offset(-desktop.X, -desktop.Y);
                    output.Add(area);
                }
                return true;
            }, IntPtr.Zero);
            return output;
        }

        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
        private delegate bool EnumWindow(IntPtr window, IntPtr state);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, IntPtr state);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int count);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out NativeRect value, int size);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
    }
}
