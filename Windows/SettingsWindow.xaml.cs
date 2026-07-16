using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using OctoCapture.Models;
using OctoCapture.Services;

namespace OctoCapture.Windows
{
    public partial class SettingsWindow : Window
    {
        private readonly AppSettings _settings;

        public SettingsWindow(AppSettings settings)
        {
            InitializeComponent();
            _settings = settings;

            ChkStartup.IsChecked = _settings.RunAtStartup;
            ChkTray.IsChecked = _settings.MinimizeToTrayOnClose;
            ChkClipboard.IsChecked = _settings.CopyToClipboardOnCapture;
            ChkOpenEditor.IsChecked = _settings.OpenEditorAfterCapture;
            TxtSaveFolder.Text = _settings.SaveFolder;
            SelectCombo(CmbImageFormat, _settings.ImageFormat);
            SelectCombo(CmbVideoFormat, _settings.VideoFormat);
            ChkSysAudio.IsChecked = _settings.RecordSystemAudio;
            ChkMic.IsChecked = _settings.RecordMicrophone;
            SelectCombo(CmbFps, _settings.RecordFps.ToString());
            SelectCombo(CmbGifFps, _settings.GifFps.ToString());

            HkRegion.Text = _settings.HotkeyRegionCapture;
            HkWindow.Text = _settings.HotkeyWindowCapture;
            HkUnit.Text = _settings.HotkeyUnitCapture;
            HkMonitor.Text = _settings.HotkeyMonitorCapture;
            HkFull.Text = _settings.HotkeyFullCapture;
            HkScroll.Text = _settings.HotkeyScrollCapture;
            HkRecord.Text = _settings.HotkeyRecord;
            HkShowMain.Text = _settings.HotkeyShowMain;
        }

        private static void SelectCombo(ComboBox combo, string value)
        {
            foreach (ComboBoxItem item in combo.Items)
            {
                if (string.Equals(item.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = item;
                    return;
                }
            }
            combo.SelectedIndex = 0;
        }

        private static string ComboValue(ComboBox combo) =>
            (combo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog { Title = "캡쳐 저장 폴더 선택" };
            if (dlg.ShowDialog() == true)
                TxtSaveFolder.Text = dlg.FolderName;
        }

        private void Hotkey_KeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = true;
            var box = (TextBox)sender;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;

            if (key == Key.Escape) { box.Text = ""; return; }
            // 수정키 단독 입력은 무시
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;

            var parts = new List<string>();
            var mods = Keyboard.Modifiers;
            if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
            parts.Add(key.ToString());
            box.Text = string.Join("+", parts);
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            // 단축키 유효성 검사 (비어있으면 미사용)
            foreach (var (name, text) in new[]
            {
                ("직접 캡쳐", HkRegion.Text), ("창 캡쳐", HkWindow.Text), ("단위별 캡쳐", HkUnit.Text),
                ("화면 캡쳐", HkMonitor.Text), ("전체 캡쳐", HkFull.Text), ("스크롤 캡쳐", HkScroll.Text),
                ("녹화", HkRecord.Text), ("메인 창", HkShowMain.Text),
            })
            {
                if (!string.IsNullOrWhiteSpace(text) && !HotkeyManager.TryParse(text, out _, out _))
                {
                    MessageBox.Show($"'{name}' 단축키가 올바르지 않습니다: {text}", "OctoCapture",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            _settings.RunAtStartup = ChkStartup.IsChecked == true;
            _settings.MinimizeToTrayOnClose = ChkTray.IsChecked == true;
            _settings.CopyToClipboardOnCapture = ChkClipboard.IsChecked == true;
            _settings.OpenEditorAfterCapture = ChkOpenEditor.IsChecked == true;
            _settings.SaveFolder = TxtSaveFolder.Text.Trim();
            _settings.ImageFormat = ComboValue(CmbImageFormat).ToLowerInvariant();
            _settings.VideoFormat = ComboValue(CmbVideoFormat).ToLowerInvariant();
            _settings.RecordSystemAudio = ChkSysAudio.IsChecked == true;
            _settings.RecordMicrophone = ChkMic.IsChecked == true;
            _settings.RecordFps = int.TryParse(ComboValue(CmbFps), out int fps) ? fps : 30;
            _settings.GifFps = int.TryParse(ComboValue(CmbGifFps), out int gfps) ? gfps : 15;

            _settings.HotkeyRegionCapture = HkRegion.Text;
            _settings.HotkeyWindowCapture = HkWindow.Text;
            _settings.HotkeyUnitCapture = HkUnit.Text;
            _settings.HotkeyMonitorCapture = HkMonitor.Text;
            _settings.HotkeyFullCapture = HkFull.Text;
            _settings.HotkeyScrollCapture = HkScroll.Text;
            _settings.HotkeyRecord = HkRecord.Text;
            _settings.HotkeyShowMain = HkShowMain.Text;

            _settings.Save();
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
