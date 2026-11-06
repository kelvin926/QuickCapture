using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace QuickCapture
{
    internal static class MediaTools
    {
        private static readonly string Engine = Path.Combine(Path.GetDirectoryName(typeof(MediaTools).Assembly.Location), "media", "ffmpeg.exe");
        internal static string TempFolder
        {
            get { return Path.Combine(Path.GetTempPath(), "QuickCapture", Guid.NewGuid().ToString("N")); }
        }
        public static string Quoted(string path) { return "\"" + path + "\""; }
        internal static string Number(double value) { return value.ToString("0.###", CultureInfo.InvariantCulture); }
        public static bool Available { get { return File.Exists(Engine); } }

        internal static Process Open(string arguments, StringBuilder diagnostics, bool interactive)
        {
            if (!Available) throw new FileNotFoundException("녹화 엔진이 없습니다. 전체 ZIP을 풀고 실행하세요.");
            var process = new Process
            {
                StartInfo = new ProcessStartInfo(Engine, "-hide_banner -loglevel error -y " +
                    (interactive ? "" : "-nostdin ") + arguments)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
                    RedirectStandardInput = interactive, WorkingDirectory = Path.GetDirectoryName(Engine)
                }
            };
            process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs args)
            { if (args.Data != null) lock (diagnostics) { if (diagnostics.Length < 6000) diagnostics.AppendLine(args.Data); } };
            try
            {
                process.Start();
                process.BeginErrorReadLine();
                return process;
            }
            catch { process.Dispose(); throw; }
        }

        public static void Run(string arguments)
        {
            var diagnostics = new StringBuilder();
            using (Process process = Open(arguments, diagnostics, false))
            {
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("동영상 처리에 실패했습니다. " + diagnostics.ToString().Trim());
            }
        }

        public static void Trim(string source, string output, double start, double duration)
        {
            Run("-ss " + Number(start) + " -i " + Quoted(source) + " -t " + Number(duration) +
                " -c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p -c:a aac -movflags +faststart " + Quoted(output));
        }

        public static void Gif(string source, string output, bool high)
        {
            string filter = (high ? "fps=15,scale='min(iw,960)':-1" : "fps=8,scale='min(iw,480)':-1") +
                ":flags=lanczos,split[a][b];[a]palettegen[p];[b][p]paletteuse";
            Run("-i " + Quoted(source) + " -t 30 -filter_complex \"" + filter + "\" -loop 0 " + Quoted(output));
        }
    }

    internal sealed class RecordingSession : IDisposable
    {
        private readonly string folder = MediaTools.TempFolder;
        private readonly Rectangle area;
        private readonly List<Segment> segments = new List<Segment>();
        private Segment current;
        public RecordingSession(Rectangle area) { this.area = area; Directory.CreateDirectory(folder); }

        public void Resume(bool mic, bool speaker)
        {
            if (current != null) throw new InvalidOperationException("녹화 중입니다.");
            current = new Segment(folder, segments.Count, area, mic, speaker);
        }

        public void Pause()
        {
            Segment segment = current;
            current = null;
            if (segment == null) return;
            try { segment.Stop(); segments.Add(segment); }
            catch { segment.Dispose(); throw; }
        }

        public string Finish(string destination)
        {
            Pause();
            if (segments.Count == 0) throw new InvalidOperationException("녹화된 구간이 없습니다.");
            Directory.CreateDirectory(destination);
            string output = Path.Combine(destination, "Recording_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") +
                "_" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".mp4");
            string work = output + ".part.mp4";
            try
            {
                var list = new List<string>();
                foreach (Segment segment in segments)
                {
                    segment.Mux();
                    list.Add("file '" + Path.GetFileName(segment.Muxed) + "'");
                }
                string paths = Path.Combine(folder, "segments.txt");
                File.WriteAllLines(paths, list, new UTF8Encoding(false));
                MediaTools.Run("-f concat -safe 1 -i " + MediaTools.Quoted(paths) +
                    " -c copy -movflags +faststart " + MediaTools.Quoted(work));
                File.Move(work, output);
                return output;
            }
            finally { File.Delete(work); }
        }

        public void Dispose()
        {
            if (current != null) current.Dispose();
            foreach (Segment segment in segments) segment.Dispose();
            try { Directory.Delete(folder, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private sealed class Segment : IDisposable
        {
            private readonly string video;
            private readonly AudioTrack microphone, loopback;
            private readonly StringBuilder diagnostics = new StringBuilder();
            private Process process;
            public readonly string Muxed;

            public Segment(string folder, int number, Rectangle area, bool mic, bool speaker)
            {
                string name = Path.Combine(folder, "segment" + number.ToString(CultureInfo.InvariantCulture));
                video = name + ".mp4";
                Muxed = name + "-mux.mp4";
                try
                {
                    if (mic) microphone = new AudioTrack(new WasapiCapture(), name + "-mic.wav");
                    if (speaker) loopback = new AudioTrack(new WasapiLoopbackCapture(), name + "-audio.wav");
                    string grab = string.Format(CultureInfo.InvariantCulture,
                        "-f gdigrab -framerate 30 -offset_x {0} -offset_y {1} -video_size {2}x{3} -i desktop",
                        area.X, area.Y, area.Width, area.Height);
                    process = MediaTools.Open(grab + " -an -vf pad=ceil(iw/2)*2:ceil(ih/2)*2 -c:v libx264" +
                        " -preset ultrafast -crf 21 -pix_fmt yuv420p " + MediaTools.Quoted(video), diagnostics, true);
                    if (microphone != null) microphone.Start();
                    if (loopback != null) loopback.Start();
                }
                catch { Dispose(); throw; }
            }

            public void Stop()
            {
                if (process == null) return;
                if (microphone != null) microphone.Stop();
                if (loopback != null) loopback.Stop();
                if (!process.HasExited)
                {
                    try { process.StandardInput.WriteLine("q"); }
                    catch (IOException) { }
                    if (!process.WaitForExit(10000)) { process.Kill(); process.WaitForExit(); }
                }
                process.WaitForExit();
                if (process.ExitCode != 0 || !File.Exists(video) || new FileInfo(video).Length < 500)
                    throw new InvalidOperationException("녹화 구간을 저장하지 못했습니다. " + diagnostics.ToString().Trim());
            }

            public void Mux()
            {
                var tracks = new[] { microphone, loopback }.Where(track => track != null && track.HasSamples).ToArray();
                string args = "-i " + MediaTools.Quoted(video);
                foreach (AudioTrack track in tracks) args += " -i " + MediaTools.Quoted(track.Path);
                if (tracks.Length == 0)
                    args += " -f lavfi -i anullsrc=r=48000:cl=stereo -map 0:v -map 1:a";
                else if (tracks.Length == 1)
                    args += " -map 0:v -map 1:a -af apad";
                else
                    args += " -filter_complex \"[1:a][2:a]amix=inputs=2:duration=longest:normalize=0,apad[a]\" -map 0:v -map \"[a]\"";
                MediaTools.Run(args + " -c:v copy -c:a aac -ar 48000 -ac 2 -shortest " + MediaTools.Quoted(Muxed));
            }

            public void Dispose()
            {
                if (process != null)
                {
                    if (!process.HasExited) { process.Kill(); process.WaitForExit(); }
                    process.Dispose();
                    process = null;
                }
                if (microphone != null) microphone.Dispose();
                if (loopback != null) loopback.Dispose();
            }
        }

        private sealed class AudioTrack : IDisposable
        {
            private readonly WasapiCapture input;
            private readonly WaveFileWriter output;
            private readonly ManualResetEvent completed = new ManualResetEvent(false);
            private readonly Stopwatch clock = new Stopwatch();
            private readonly object gate = new object();
            private readonly byte[] silence = new byte[16384];
            private bool running, closed;
            public readonly string Path;
            public bool HasSamples { get; private set; }

            public AudioTrack(WasapiCapture input, string path)
            {
                this.input = input;
                Path = path;
                try { output = new WaveFileWriter(path, input.WaveFormat); }
                catch { input.Dispose(); completed.Dispose(); throw; }
                input.DataAvailable += delegate(object sender, WaveInEventArgs args)
                {
                    lock (gate)
                    {
                        if (closed) return;
                        int align = input.WaveFormat.BlockAlign;
                        long expected = (long)(clock.Elapsed.TotalSeconds * input.WaveFormat.AverageBytesPerSecond) - args.BytesRecorded;
                        long missing = expected - output.Length;
                        while (missing > input.WaveFormat.AverageBytesPerSecond / 8)
                        {
                            int size = (int)(Math.Min(missing, silence.Length) / align * align);
                            if (size == 0) break;
                            output.Write(silence, 0, size);
                            missing -= size;
                        }
                        output.Write(args.Buffer, 0, args.BytesRecorded);
                        HasSamples = true;
                    }
                };
                input.RecordingStopped += delegate { completed.Set(); };
            }

            public void Start() { clock.Start(); input.StartRecording(); running = true; }
            public void Stop()
            {
                if (running)
                {
                    running = false;
                    input.StopRecording();
                    completed.WaitOne(2000);
                }
                lock (gate)
                {
                    if (closed) return;
                    closed = true;
                    output.Dispose();
                }
            }
            public void Dispose() { Stop(); input.Dispose(); completed.Dispose(); }
        }
    }

    internal sealed class RecordingControls : Form
    {
        private readonly RecordingSession session;
        private readonly string destination;
        private readonly Button start, stop;
        private readonly CheckBox mic, speaker;
        private readonly Label clock;
        private readonly Stopwatch active = new Stopwatch();
        private readonly System.Windows.Forms.Timer timer;
        private bool started, paused, busy, complete;
        public string Output { get; private set; }

        public RecordingControls(Rectangle area, string destination, QuickSettings settings)
        {
            this.destination = destination;
            session = new RecordingSession(area);
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            Text = "QuickCapture - 화면 녹화";
            ShowInTaskbar = true;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            ClientSize = new Size(405, 54);
            Rectangle work = Screen.FromPoint(area.Location).WorkingArea;
            Location = new Point(Math.Min(Math.Max(work.Left, area.Left), Math.Max(work.Left, work.Right - Width)),
                area.Top - Height >= work.Top ? area.Top - Height : Math.Max(work.Top, work.Bottom - Height));
            if (new Rectangle(Location, Size).IntersectsWith(area))
                foreach (Screen screen in Screen.AllScreens)
                {
                    Rectangle candidate = new Rectangle(screen.WorkingArea.Location, Size);
                    if (!area.IntersectsWith(candidate) && screen.WorkingArea.Contains(candidate))
                    { Location = candidate.Location; break; }
                }
            var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(5, 10, 5, 0), WrapContents = false };
            Controls.Add(bar);
            start = new Button { Text = "시작", Width = 75, Height = 29 };
            start.Click += async delegate { await StartOrPause(); };
            stop = new Button { Text = "중지", Width = 60, Height = 29, Enabled = false };
            stop.Click += async delegate { await Finish(); };
            mic = new CheckBox { Text = "마이크", Checked = settings.Microphone, AutoSize = true, Margin = new Padding(6, 7, 0, 0) };
            speaker = new CheckBox { Text = "시스템 소리", Checked = settings.SystemAudio, AutoSize = true, Margin = new Padding(6, 7, 0, 0) };
            clock = new Label { Text = "00:00", AutoSize = true, Margin = new Padding(8, 9, 0, 0) };
            bar.Controls.AddRange(new Control[] { start, stop, mic, speaker, clock });
            timer = new System.Windows.Forms.Timer { Interval = 250 };
            timer.Tick += delegate { if (started && !busy) clock.Text = active.Elapsed.ToString(@"hh\:mm\:ss"); };
            timer.Start();
        }

        protected override void OnShown(EventArgs args)
        {
            base.OnShown(args);
            SetWindowDisplayAffinity(Handle, 0x11);
        }

        private async Task StartOrPause()
        {
            if (busy) return;
            SetBusy(true);
            try
            {
                if (!started)
                    for (int remaining = 3; remaining > 0; remaining--) { clock.Text = remaining.ToString(); await Task.Delay(1000); }
                if (started && !paused)
                {
                    active.Stop();
                    await Task.Run(() => session.Pause());
                    paused = true;
                }
                else
                {
                    bool useMic = mic.Checked, useSpeaker = speaker.Checked;
                    await Task.Run(() => session.Resume(useMic, useSpeaker));
                    active.Start();
                    started = true;
                    paused = false;
                }
                start.Text = paused ? "다시 시작" : "일시 정지";
            }
            catch (Exception error) { MessageBox.Show(this, error.Message, Text); complete = true; Close(); }
            finally { if (!IsDisposed) SetBusy(false); }
        }

        private async Task Finish()
        {
            if (busy || !started) return;
            SetBusy(true);
            active.Stop();
            try
            {
                clock.Text = "저장 중";
                Output = await Task.Run(() => session.Finish(destination));
                DialogResult = DialogResult.OK;
            }
            catch (Exception error) { MessageBox.Show(this, error.Message, Text); }
            finally { complete = true; Close(); }
        }

        private void SetBusy(bool value)
        {
            busy = value;
            start.Enabled = !value;
            stop.Enabled = !value && started;
            mic.Enabled = speaker.Enabled = !value && (!started || paused);
        }

        protected override void OnFormClosing(FormClosingEventArgs args)
        {
            if (!complete && args.CloseReason == CloseReason.UserClosing)
                args.Cancel = busy || (started && MessageBox.Show(this, "녹화를 버리시겠습니까?", Text,
                    MessageBoxButtons.YesNo) != DialogResult.Yes);
            base.OnFormClosing(args);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { timer.Dispose(); session.Dispose(); }
            base.Dispose(disposing);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);
    }
}
