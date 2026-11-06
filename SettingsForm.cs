using System;
using System.Drawing;
using System.Windows.Forms;

namespace QuickCapture
{
    internal sealed class SettingsForm : Form
    {
        private readonly FlowLayoutPanel rows;
        public SettingsForm(QuickSettings settings)
        {
            Text = "QuickCapture - 설정";
            ClientSize = new Size(570, 620);
            MinimumSize = new Size(470, 350);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("맑은 고딕", 10);
            rows = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(18, 8, 18, 14),
                FlowDirection = FlowDirection.TopDown, WrapContents = false };
            Controls.Add(rows);
            Heading("캡처");
            Check("스크린샷 자동 복사", settings.AutoCopy, value => settings.AutoCopy = value);
            Check("스크린샷 변경 내용 자동 복사", settings.CopyEdits, value => settings.CopyEdits = value);
            Check("편집된 스크린샷을 저장하도록 요청", settings.AskSave, value => settings.AskSave = value);
            Check("스크린샷 PNG 자동 저장", settings.AutoSave, value => settings.AutoSave = value);
            Check("스크린샷 선택 후 영역 조정", settings.AdjustAfterSelection, value => settings.AdjustAfterSelection = value);
            Folder("스크린샷 저장 위치", settings.Pictures, value => settings.Pictures = value);
            Check("다른 창에서 새 스크린샷 열기", settings.MultipleWindows, value => settings.MultipleWindows = value);
            Check("각 스크린샷에 테두리 추가", settings.Border, value => settings.Border = value);
            var border = new FlowLayoutPanel { Width = 470, Height = 36, Margin = new Padding(0) };
            var color = new Button { Text = "테두리 색", Width = 105, BackColor = Color.FromArgb(settings.BorderArgb) };
            var width = new NumericUpDown { Width = 64, Minimum = 1, Maximum = 50, Value = Math.Max(1, Math.Min(50, settings.BorderWidth)) };
            color.Click += delegate
            {
                using (var dialog = new ColorDialog { Color = color.BackColor, FullOpen = true })
                    if (dialog.ShowDialog(this) == DialogResult.OK) { color.BackColor = dialog.Color; settings.BorderArgb = dialog.Color.ToArgb(); }
            };
            width.ValueChanged += delegate { settings.BorderWidth = (int)width.Value; };
            border.Controls.AddRange(new Control[] { color, new Label { Text = "굵기 (px)", AutoSize = true, Margin = new Padding(10, 5, 0, 0) }, width });
            rows.Controls.Add(border);
            Check("HDR 스크린샷 색 수정기", settings.ToneMap, value => settings.ToneMap = value);

            Heading("화면 녹화");
            Check("화면 녹화 변경 내용 자동 복사", settings.CopyVideoEdits, value => settings.CopyVideoEdits = value);
            Check("편집된 화면 녹화를 저장하도록 요청", settings.AskSaveVideo, value => settings.AskSaveVideo = value);
            Check("기본적으로 마이크 입력 포함", settings.Microphone, value => settings.Microphone = value);
            Check("기본적으로 시스템 오디오 포함", settings.SystemAudio, value => settings.SystemAudio = value);
            Check("화면 녹화 자동 저장", settings.SaveRecordings, value => settings.SaveRecordings = value);
            Folder("녹화 저장 위치", settings.Videos, value => settings.Videos = value);

            Heading("바로 가기");
            Check("Print Screen 키를 사용하여 QuickCapture 열기", settings.PrintScreen, value => settings.PrintScreen = value);
            rows.Controls.Add(new Label { Text = "Win+Shift+S  스크린샷   |   Win+Shift+R  녹화   |   Win+Shift+T  텍스트", AutoSize = true });
            Heading("모양");
            var theme = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 185 };
            theme.Items.AddRange(new object[] { "시스템 설정 사용", "밝게", "어둡게" });
            theme.SelectedIndex = Math.Max(0, Math.Min(2, settings.Theme));
            theme.SelectedIndexChanged += delegate { settings.Theme = theme.SelectedIndex; };
            rows.Controls.Add(theme);
            Heading("이 앱에 대한 정보");
            rows.Controls.Add(new Label { Text = "QuickCapture 2.0.2", AutoSize = true });
            var done = new Button { Text = "닫기", Width = 95, Height = 31, DialogResult = DialogResult.OK, Margin = new Padding(0, 12, 0, 0) };
            rows.Controls.Add(done); AcceptButton = done; CancelButton = done;
        }

        private void Heading(string text)
        {
            rows.Controls.Add(new Label { Text = text, Font = new Font(Font, FontStyle.Bold), AutoSize = true,
                Margin = new Padding(0, 13, 0, 7) });
        }

        private void Check(string text, bool value, Action<bool> changed)
        {
            var control = new CheckBox { Text = text, Checked = value, AutoSize = true, Margin = new Padding(0, 4, 0, 4) };
            control.CheckedChanged += delegate { changed(control.Checked); };
            rows.Controls.Add(control);
        }

        private void Folder(string label, string value, Action<string> changed)
        {
            rows.Controls.Add(new Label { Text = label, AutoSize = true });
            var row = new FlowLayoutPanel { Width = 474, Height = 38, Margin = new Padding(0) };
            var text = new TextBox { Text = value, Width = 366, ReadOnly = true };
            var button = new Button { Text = "변경", Width = 85 };
            button.Click += delegate
            {
                using (var dialog = new FolderBrowserDialog { Description = label, SelectedPath = text.Text })
                    if (dialog.ShowDialog(this) == DialogResult.OK) { text.Text = dialog.SelectedPath; changed(dialog.SelectedPath); }
            };
            row.Controls.Add(text); row.Controls.Add(button); rows.Controls.Add(row);
        }
    }
}
