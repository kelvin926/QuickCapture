using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace QuickCapture
{
    internal enum MarkTool { Pen, Marker, Eraser, Rectangle, Ellipse, Line, Arrow, Crop, Text, Select }

    internal sealed class Mark
    {
        public MarkTool Tool;
        public Point[] Points;
        public Color Color, Fill;
        public float Width;
        public string Emoji;
        public Rectangle Bounds { get { return Rectangle.FromLTRB(Points.Min(p => p.X), Points.Min(p => p.Y),
            Points.Max(p => p.X) + 1, Points.Max(p => p.Y) + 1); } }

        public void Paint(Graphics graphics)
        {
            if (Emoji != null)
            {
                using (var font = new Font("Segoe UI Emoji", Width, GraphicsUnit.Pixel))
                using (var brush = new SolidBrush(Color.Black))
                    graphics.DrawString(Emoji, font, brush, Points[0]);
                return;
            }
            Rectangle box = Bounds;
            using (var pen = new Pen(Color, Width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            using (var brush = new SolidBrush(Fill))
            {
                switch (Tool)
                {
                    case MarkTool.Rectangle:
                        if (Fill.A > 0) graphics.FillRectangle(brush, box);
                        if (Color.A > 0) graphics.DrawRectangle(pen, box);
                        break;
                    case MarkTool.Ellipse:
                        if (Fill.A > 0) graphics.FillEllipse(brush, box);
                        if (Color.A > 0) graphics.DrawEllipse(pen, box);
                        break;
                    case MarkTool.Arrow:
                        using (var cap = new AdjustableArrowCap(4, 5)) { pen.CustomEndCap = cap; graphics.DrawLine(pen, Points[0], Points[1]); }
                        break;
                    default:
                        if (Points.Length == 1) graphics.DrawLine(pen, Points[0], new PointF(Points[0].X + 0.15f, Points[0].Y));
                        else graphics.DrawLines(pen, Points);
                        break;
                }
            }
        }

        public bool Hit(Point point)
        {
            int tolerance = (int)Math.Max(7, Width / 2 + 4);
            if (Emoji != null) return new Rectangle(Points[0], new Size((int)Width + 12, (int)Width + 12)).Contains(point);
            if (!Rectangle.Inflate(Bounds, tolerance, tolerance).Contains(point)) return false;
            if (Tool == MarkTool.Ellipse || Tool == MarkTool.Rectangle)
                return Fill.A > 0 || !Rectangle.Inflate(Bounds, -tolerance, -tolerance).Contains(point);
            for (int i = 1; i < Points.Length; i++)
            {
                Point start = Points[i - 1], end = Points[i];
                float x = end.X - start.X, y = end.Y - start.Y;
                float length = x * x + y * y;
                float t = length == 0 ? 0 : Math.Max(0, Math.Min(1, ((point.X - start.X) * x + (point.Y - start.Y) * y) / length));
                float dx = point.X - start.X - t * x, dy = point.Y - start.Y - t * y;
                if (dx * dx + dy * dy <= tolerance * tolerance) return true;
            }
            return Points.Length == 1;
        }
    }

    internal sealed class MarkupCanvas : Control
    {
        private sealed class State
        {
            public Rectangle Crop;
            public readonly List<Mark> Marks;
            public State(Rectangle crop, IEnumerable<Mark> marks) { Crop = crop; Marks = new List<Mark>(marks); }
        }

        private Bitmap image;
        private State state;
        private readonly List<State> undo = new List<State>();
        private readonly Stack<State> redo = new Stack<State>();
        private readonly List<Point> stroke = new List<Point>();
        private readonly Timer hold;
        private bool drawing, panning, moveMeasure;
        private Point start, current, panPoint;
        private Rectangle cropPreview, textSelection;
        private Mark perfect;
        private Mark moving;
        private bool resizing;
        private PointF pan;
        private float zoom = 1;
        private TextScan scan;
        private Point measureCenter;
        private float measureAngle;
        public MarkTool Tool = MarkTool.Pen;
        public Color Ink { get; set; }
        public Color Fill { get; set; }
        public float InkWidth = 4;
        public int Measurement;
        public bool DrawAndHold = true;
        public int EdgePadding = 20;
        public event Action Changed;
        public event Action<string> TextSelected;
        public bool HasImage { get { return image != null; } }
        public bool CanUndo { get { return undo.Count > 0; } }
        public bool CanRedo { get { return redo.Count > 0; } }
        public bool CropPending { get { return !cropPreview.IsEmpty; } }

        public MarkupCanvas()
        {
            Ink = Color.FromArgb(240, 67, 68);
            Fill = Color.Transparent;
            DoubleBuffered = true;
            ResizeRedraw = true;
            TabStop = true;
            hold = new Timer { Interval = 600 };
            hold.Tick += delegate
            {
                hold.Stop();
                if (!drawing || Tool != MarkTool.Pen || stroke.Count < 5) return;
                perfect = RecognizeStroke();
                Invalidate();
            };
        }

        public void LoadImage(Bitmap value)
        {
            if (image != null) image.Dispose();
            image = value;
            state = new State(new Rectangle(Point.Empty, value.Size), new Mark[0]);
            undo.Clear(); redo.Clear(); scan = null;
            drawing = false; cropPreview = Rectangle.Empty; textSelection = Rectangle.Empty;
            Fit();
        }

        public void Fit() { zoom = 1; pan = PointF.Empty; Invalidate(); }
        public void Zoom(int direction)
        {
            zoom = Math.Max(0.25f, Math.Min(12f, zoom * (direction > 0 ? 1.2f : 0.833333f)));
            Invalidate();
        }

        public void SetTool(MarkTool tool)
        {
            Tool = tool;
            cropPreview = Rectangle.Empty;
            moving = null;
            if (tool != MarkTool.Text) { scan = null; textSelection = Rectangle.Empty; }
            Cursor = tool == MarkTool.Text ? Cursors.IBeam : Cursors.Cross;
            Invalidate();
        }

        public void ShowText(TextScan value)
        {
            SetTool(MarkTool.Text);
            scan = value;
            Invalidate();
        }

        public Bitmap Render()
        {
            if (!HasImage) return null;
            var result = new Bitmap(state.Crop.Width, state.Crop.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(result))
            {
                g.TranslateTransform(-state.Crop.X, -state.Crop.Y);
                PaintImage(g, false);
            }
            return result;
        }

        public void Undo()
        {
            if (!CanUndo) return;
            redo.Push(state);
            state = undo[undo.Count - 1];
            undo.RemoveAt(undo.Count - 1);
            Notify();
        }

        public void Redo()
        {
            if (!CanRedo) return;
            undo.Add(state);
            state = redo.Pop();
            Notify();
        }

        private void Edit(Action<State> action)
        {
            if (!HasImage) return;
            undo.Add(state);
            if (undo.Count > 100) undo.RemoveAt(0);
            state = new State(state.Crop, state.Marks);
            redo.Clear();
            action(state);
            Notify();
        }

        private void Notify()
        {
            cropPreview = Rectangle.Empty;
            scan = null;
            textSelection = Rectangle.Empty;
            Invalidate();
            if (Changed != null) Changed();
        }

        public void CommitCrop()
        {
            Rectangle selected = Rectangle.Intersect(cropPreview, state.Crop);
            if (selected.Width > 1 && selected.Height > 1)
                Edit(doc => doc.Crop = selected);
            Fit();
        }

        public void InsertEmoji(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || !HasImage) return;
            var center = new Point(state.Crop.X + state.Crop.Width / 2, state.Crop.Y + state.Crop.Height / 2);
            var mark = new Mark { Emoji = value, Width = Math.Max(24, state.Crop.Width / 13), Points = new[] { center } };
            Edit(doc => doc.Marks.Add(mark));
            SetTool(MarkTool.Select);
        }

        public int Redact(TextScan text, bool all)
        {
            var regions = new List<Rectangle>();
            foreach (var line in text.Lines)
            {
                string raw = string.Join(" ", line.Select(word => word.Text));
                var matches = all ? Regex.Matches(raw, @".+") : Regex.Matches(raw,
                    @"[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}|(?<!\w)(?:\+?\d[\d().\- ]{6,}\d)(?!\w)");
                foreach (Match match in matches)
                {
                    int offset = 0;
                    Rectangle area = Rectangle.Empty;
                    foreach (RecognizedWord word in line)
                    {
                        if (offset < match.Index + match.Length && offset + word.Text.Length > match.Index)
                            area = area.IsEmpty ? word.Bounds : Rectangle.Union(area, word.Bounds);
                        offset += word.Text.Length + 1;
                    }
                    if (!area.IsEmpty) regions.Add(Rectangle.Inflate(area, 2, 2));
                }
            }
            if (regions.Count != 0)
                Edit(doc =>
                {
                    foreach (Rectangle area in regions)
                    {
                        var a = new Point(area.Left + doc.Crop.X, area.Top + doc.Crop.Y);
                        doc.Marks.Add(new Mark { Tool = MarkTool.Rectangle, Width = 1, Fill = Color.Black,
                            Points = new[] { a, new Point(a.X + area.Width, a.Y + area.Height) } });
                    }
                });
            return regions.Count;
        }

        private float ImageScale { get { return !HasImage ? 1 : Math.Max(0.01f, Math.Min((Width - EdgePadding) / (float)state.Crop.Width,
            (Height - EdgePadding) / (float)state.Crop.Height)) * zoom; } }
        private PointF Origin { get { return new PointF((Width - state.Crop.Width * ImageScale) / 2 + pan.X,
            (Height - state.Crop.Height * ImageScale) / 2 + pan.Y); } }

        private Point Pixel(Point point)
        {
            return new Point((int)Math.Round((point.X - Origin.X) / ImageScale + state.Crop.X),
                (int)Math.Round((point.Y - Origin.Y) / ImageScale + state.Crop.Y));
        }

        protected override void OnPaint(PaintEventArgs args)
        {
            base.OnPaint(args);
            if (!HasImage) return;
            Graphics graphics = args.Graphics;
            GraphicsState previous = graphics.Save();
            graphics.TranslateTransform(Origin.X, Origin.Y);
            graphics.ScaleTransform(ImageScale, ImageScale);
            graphics.TranslateTransform(-state.Crop.X, -state.Crop.Y);
            graphics.SetClip(state.Crop, CombineMode.Intersect);
            PaintImage(graphics, true);
            graphics.Restore(previous);
            if (Measurement != 0) PaintMeasurement(graphics);
        }

        private void PaintImage(Graphics g, bool preview)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.DrawImageUnscaled(image, 0, 0);
            foreach (Mark mark in state.Marks)
                if (!preview || mark != moving) mark.Paint(g);
            if (!preview) return;
            if (drawing && moving != null) MoveMark(moving).Paint(g);
            else if (drawing && Tool != MarkTool.Eraser && Tool != MarkTool.Crop && Tool != MarkTool.Text && Tool != MarkTool.Select)
                (perfect ?? NewMark()).Paint(g);
            if (!cropPreview.IsEmpty)
            {
                using (var shade = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
                using (var outside = new Region(state.Crop))
                {
                    outside.Exclude(cropPreview);
                    g.FillRegion(shade, outside);
                }
                using (var border = new Pen(Color.White, 2 / ImageScale) { DashStyle = DashStyle.Dash })
                    g.DrawRectangle(border, cropPreview);
            }
            if (scan != null)
            {
                using (var selected = new SolidBrush(Color.FromArgb(130, 23, 127, 228)))
                using (var normal = new Pen(Color.FromArgb(170, 69, 155, 255), 1 / ImageScale))
                    foreach (var word in scan.Lines.SelectMany(line => line))
                    {
                        Rectangle bounds = word.Bounds;
                        bounds.Offset(state.Crop.Location);
                        if (textSelection.IntersectsWith(bounds)) g.FillRectangle(selected, bounds);
                        else g.DrawRectangle(normal, bounds);
                    }
            }
        }

        private void PaintMeasurement(Graphics g)
        {
            if (measureCenter == Point.Empty) measureCenter = new Point(Width / 2, Height / 2);
            GraphicsState saved = g.Save();
            g.TranslateTransform(measureCenter.X, measureCenter.Y);
            g.RotateTransform(measureAngle);
            using (var bg = new SolidBrush(Color.FromArgb(200, 242, 242, 242)))
            {
                if (Measurement == 1)
                {
                    g.FillRectangle(bg, -165, 0, 330, 48);
                    for (int i = -160; i <= 160; i += 8)
                        g.DrawLine(Pens.Black, i, 0, i, i % 40 == 0 ? 22 : 11);
                }
                else
                {
                    g.FillPie(bg, -135, -135, 270, 270, 180, 180);
                    for (int i = 0; i <= 180; i += 5)
                    {
                        double angle = i * Math.PI / 180;
                        float x = (float)Math.Cos(angle), y = -(float)Math.Sin(angle);
                        g.DrawLine(Pens.Black, x * 133, y * 133, x * (i % 15 == 0 ? 113 : 125), y * (i % 15 == 0 ? 113 : 125));
                    }
                }
            }
            g.Restore(saved);
        }

        protected override void OnMouseDown(MouseEventArgs args)
        {
            base.OnMouseDown(args);
            if (!HasImage) return;
            Focus();
            if (args.Button == MouseButtons.Middle) { panning = true; panPoint = args.Location; Capture = true; return; }
            if (args.Button != MouseButtons.Left) return;
            if (Measurement != 0 && (ModifierKeys & Keys.Shift) != 0) { moveMeasure = true; Capture = true; return; }
            current = start = Pixel(args.Location);
            if (!state.Crop.Contains(start)) return;
            if (Tool == MarkTool.Select)
            {
                moving = state.Marks.LastOrDefault(mark => mark.Hit(start));
                if (moving == null) return;
                resizing = (ModifierKeys & Keys.Shift) != 0;
            }
            drawing = true;
            Capture = true;
            perfect = null;
            stroke.Clear(); stroke.Add(start);
            if (Tool == MarkTool.Text) textSelection = new Rectangle(start, new Size(1, 1));
            if (Tool == MarkTool.Crop) cropPreview = Rectangle.Empty;
        }

        protected override void OnMouseMove(MouseEventArgs args)
        {
            base.OnMouseMove(args);
            if (panning)
            {
                pan.X += args.X - panPoint.X; pan.Y += args.Y - panPoint.Y;
                panPoint = args.Location; Invalidate(); return;
            }
            if (moveMeasure) { measureCenter = args.Location; Invalidate(); return; }
            if (!drawing) return;
            Point next = Pixel(args.Location);
            if (next == current) return;
            current = next;
            if (Tool == MarkTool.Pen || Tool == MarkTool.Marker) stroke.Add(current);
            if (Tool == MarkTool.Pen && DrawAndHold) { hold.Stop(); hold.Start(); }
            if (Tool == MarkTool.Crop) cropPreview = Rectangle.Intersect(SelectionOverlay.FromPoints(start, current), state.Crop);
            if (Tool == MarkTool.Text) textSelection = Rectangle.Inflate(SelectionOverlay.FromPoints(start, current), 1, 1);
            perfect = null;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs args)
        {
            base.OnMouseUp(args);
            if (panning || moveMeasure) { panning = moveMeasure = false; Capture = false; return; }
            if (!drawing || args.Button != MouseButtons.Left) return;
            drawing = false;
            Capture = false;
            hold.Stop();
            current = Pixel(args.Location);
            if (Tool == MarkTool.Select && moving != null)
            {
                Mark previous = moving;
                Mark next = MoveMark(previous);
                moving = null;
                if (current != start) Edit(doc => doc.Marks[doc.Marks.IndexOf(previous)] = next);
            }
            else if (Tool == MarkTool.Eraser)
            {
                Mark hit = state.Marks.LastOrDefault(mark => mark.Hit(current));
                if (hit != null) Edit(doc => doc.Marks.Remove(hit));
            }
            else if (Tool == MarkTool.Crop)
            {
                cropPreview = Rectangle.Intersect(SelectionOverlay.FromPoints(start, current), state.Crop);
            }
            else if (Tool == MarkTool.Text)
            {
                if (scan != null && TextSelected != null)
                {
                    string value = string.Join(Environment.NewLine, scan.Lines.Select(line =>
                        string.Join(" ", line.Where(word => {
                            Rectangle box = word.Bounds; box.Offset(state.Crop.Location); return box.IntersectsWith(textSelection);
                        }).Select(word => word.Text))).Where(line => line.Length != 0));
                    TextSelected(value);
                }
            }
            else
            {
                Mark mark = perfect ?? NewMark();
                Edit(doc => doc.Marks.Add(mark));
            }
            Invalidate();
        }

        protected override void OnMouseCaptureChanged(EventArgs args)
        {
            base.OnMouseCaptureChanged(args);
            if (!Capture && (drawing || panning || moveMeasure))
            {
                drawing = panning = moveMeasure = false;
                moving = null;
                hold.Stop();
                Invalidate();
            }
        }

        private Mark MoveMark(Mark mark)
        {
            int dx = current.X - start.X, dy = current.Y - start.Y;
            Point origin = mark.Bounds.Location;
            float scale = Math.Max(0.15f, Math.Min(10f, 1f + (dx + dy) / (2f * Math.Max(15, Math.Max(mark.Width, mark.Bounds.Width)))));
            Point[] points = resizing ? mark.Points.Select(p => new Point(origin.X + (int)((p.X - origin.X) * scale),
                origin.Y + (int)((p.Y - origin.Y) * scale))).ToArray() :
                mark.Points.Select(p => new Point(p.X + dx, p.Y + dy)).ToArray();
            return new Mark { Tool = mark.Tool, Color = mark.Color, Fill = mark.Fill, Emoji = mark.Emoji,
                Width = resizing ? mark.Width * scale : mark.Width, Points = points };
        }

        protected override void OnMouseWheel(MouseEventArgs args)
        {
            base.OnMouseWheel(args);
            if (Measurement != 0 && (ModifierKeys & Keys.Shift) != 0) { measureAngle += Math.Sign(args.Delta) * 5; Invalidate(); }
            else Zoom(Math.Sign(args.Delta));
        }

        private Mark NewMark()
        {
            bool free = Tool == MarkTool.Pen || Tool == MarkTool.Marker;
            return new Mark { Tool = Tool, Width = Tool == MarkTool.Marker ? InkWidth * 3 : InkWidth,
                Color = Tool == MarkTool.Marker ? Color.FromArgb(105, Ink) : Ink,
                Fill = Fill, Points = free ? stroke.ToArray() : new[] { start, current } };
        }

        private Mark RecognizeStroke()
        {
            Mark mark = NewMark();
            Rectangle bounds = mark.Bounds;
            if (bounds.Width + bounds.Height < 20) return null;
            int distance = Math.Abs(start.X - current.X) + Math.Abs(start.Y - current.Y);
            if (distance < Math.Max(bounds.Width, bounds.Height) / 3 && bounds.Width > 10 && bounds.Height > 10)
            {
                int corners = stroke.Count(p => (p.X < bounds.Left + bounds.Width / 5 || p.X > bounds.Right - bounds.Width / 5) &&
                    (p.Y < bounds.Top + bounds.Height / 5 || p.Y > bounds.Bottom - bounds.Height / 5));
                mark.Tool = corners * 5 > stroke.Count ? MarkTool.Rectangle : MarkTool.Ellipse;
                mark.Points = new[] { bounds.Location, new Point(bounds.Right - 1, bounds.Bottom - 1) };
            }
            else { mark.Tool = MarkTool.Line; mark.Points = new[] { start, current }; }
            return mark;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { hold.Dispose(); if (image != null) image.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
