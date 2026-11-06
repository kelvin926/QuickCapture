using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Serialization;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace QuickCapture
{
    public sealed class QuickSettings
    {
        public bool AutoSave = true, AdjustAfterSelection = true, AutoCopy = true, CopyEdits = true, AskSave = true, MultipleWindows;
        public bool Border, PrintScreen, Microphone, SystemAudio = true, SaveRecordings = true;
        public bool CopyVideoEdits = true, AskSaveVideo = true, ToneMap = true;
        public bool TextAutoCopy, TextSingleLine;
        public int BorderArgb = unchecked((int)0xff40c4ff), BorderWidth = 3, Theme;
        public string Pictures = DefaultFolder(Environment.SpecialFolder.MyPictures, "Pictures");
        public string Videos = DefaultFolder(Environment.SpecialFolder.MyVideos, "Videos");
        private static readonly string FilePath = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData), "QuickCapture", "settings.xml");

        private static string DefaultFolder(Environment.SpecialFolder known, string fallback)
        {
            string path = Environment.GetFolderPath(known, Environment.SpecialFolderOption.DoNotVerify);
            return Path.Combine(string.IsNullOrEmpty(path)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), fallback) : path, "QuickCapture");
        }

        public static QuickSettings Load()
        {
            try
            {
                using (var file = File.OpenRead(FilePath))
                    return (QuickSettings)new XmlSerializer(typeof(QuickSettings)).Deserialize(file);
            }
            catch (IOException) { return new QuickSettings(); }
            catch (InvalidOperationException) { return new QuickSettings(); }
            catch (UnauthorizedAccessException) { return new QuickSettings(); }
        }

        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            string temp = FilePath + ".tmp";
            try
            {
                using (var file = File.Create(temp))
                    new XmlSerializer(typeof(QuickSettings)).Serialize(file, this);
                if (File.Exists(FilePath)) File.Replace(temp, FilePath, null);
                else File.Move(temp, FilePath);
            }
            finally { File.Delete(temp); }
        }

        public bool Dark
        {
            get
            {
                if (Theme != 0) return Theme == 2;
                object setting = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
                return setting is int && (int)setting == 0;
            }
        }
    }

    internal sealed class RecognizedWord
    {
        public readonly string Text;
        public readonly Rectangle Bounds;
        public RecognizedWord(string text, Rectangle bounds) { Text = text; Bounds = bounds; }
    }

    internal sealed class TextScan
    {
        public readonly List<List<RecognizedWord>> Lines = new List<List<RecognizedWord>>();
        public string Qr;
        public string All { get { return string.Join(Environment.NewLine, Lines.Select(line => string.Join(" ", line.Select(w => w.Text)))); } }
    }

    internal static class LocalText
    {
        public static async Task<TextScan> Read(Bitmap image, bool readQr)
        {
            var result = new TextScan();
            OcrEngine engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine == null && OcrEngine.AvailableRecognizerLanguages.Count > 0)
                engine = OcrEngine.TryCreateFromLanguage(OcrEngine.AvailableRecognizerLanguages[0]);
            if (engine == null) throw new InvalidOperationException("Windows에 OCR 언어 팩을 설치해야 합니다.");

            float scale = Math.Min(1f, (float)OcrEngine.MaxImageDimension / Math.Max(image.Width, image.Height));
            using (var resized = new Bitmap(image, new System.Drawing.Size(Math.Max(1, (int)(image.Width * scale)),
                Math.Max(1, (int)(image.Height * scale)))))
            using (var stream = new InMemoryRandomAccessStream())
            {
                if (readQr)
                {
                    var scanner = new ZXing.BarcodeReader { AutoRotate = true };
                    scanner.Options.PossibleFormats = new[] { ZXing.BarcodeFormat.QR_CODE };
                    scanner.Options.TryHarder = true;
                    var qr = scanner.Decode(resized);
                    if (qr != null) result.Qr = qr.Text;
                }
                using (var writer = new DataWriter(stream))
                {
                    writer.WriteBytes(CaptureFiles.EncodePng(resized));
                    await WindowsAsync.Wait(writer.StoreAsync());
                    writer.DetachStream();
                }
                stream.Seek(0);
                var decoder = await WindowsAsync.Wait(BitmapDecoder.CreateAsync(stream));
                using (var pixels = await WindowsAsync.Wait(decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied)))
                {
                    OcrResult recognized = await WindowsAsync.Wait(engine.RecognizeAsync(pixels));
                    foreach (OcrLine line in recognized.Lines)
                    {
                        var words = new List<RecognizedWord>();
                        foreach (OcrWord word in line.Words)
                        {
                            var box = word.BoundingRect;
                            words.Add(new RecognizedWord(word.Text, Rectangle.FromLTRB(
                                (int)Math.Floor(box.Left / scale), (int)Math.Floor(box.Top / scale),
                                (int)Math.Ceiling(box.Right / scale), (int)Math.Ceiling(box.Bottom / scale))));
                        }
                        result.Lines.Add(words);
                    }
                }
            }
            return result;
        }
    }

    internal static class WindowsAsync
    {
        public static Task<T> Wait<T>(IAsyncOperation<T> operation)
        {
            var result = new TaskCompletionSource<T>();
            operation.Completed = delegate(IAsyncOperation<T> current, AsyncStatus status)
            {
                try { result.TrySetResult(current.GetResults()); }
                catch (Exception error) { result.TrySetException(error); }
            };
            return result.Task;
        }
    }

    internal static class WindowsShare
    {
        [ComImport, Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDataTransferManagerInterop
        {
            IntPtr GetForWindow(IntPtr window, ref Guid iid);
            void ShowShareUIForWindow(IntPtr window);
        }

        private static DataTransferManager GetManager(IntPtr window, out IDataTransferManagerInterop interop)
        {
            interop = (IDataTransferManagerInterop)WindowsRuntimeMarshal.GetActivationFactory(typeof(DataTransferManager));
            var iid = new Guid("A5CAEE9B-8708-49D1-8D36-67D25A8DA00C");
            IntPtr manager = interop.GetForWindow(window, ref iid);
            try { return (DataTransferManager)Marshal.GetObjectForIUnknown(manager); }
            finally { Marshal.Release(manager); }
        }

        public static void File(IntPtr window, string path)
        {
            IDataTransferManagerInterop interop;
            DataTransferManager manager = GetManager(window, out interop);
            TypedEventHandler<DataTransferManager, DataRequestedEventArgs> handler = null;
            handler = async delegate(DataTransferManager sender, DataRequestedEventArgs args)
            {
                sender.DataRequested -= handler;
                DataRequestDeferral deferral = args.Request.GetDeferral();
                try
                {
                    StorageFile file = await WindowsAsync.Wait(StorageFile.GetFileFromPathAsync(path));
                    args.Request.Data.Properties.Title = "QuickCapture";
                    args.Request.Data.RequestedOperation = DataPackageOperation.Copy;
                    args.Request.Data.SetStorageItems(new[] { file });
                }
                catch (Exception error) { args.Request.FailWithDisplayText(error.Message); }
                finally { deferral.Complete(); }
            };
            manager.DataRequested += handler;
            try { interop.ShowShareUIForWindow(window); }
            catch { manager.DataRequested -= handler; throw; }
        }
    }
}
