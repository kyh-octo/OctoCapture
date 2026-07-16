using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OctoCapture.Windows
{
    /// <summary>백그라운드 작업 동안 표시되는 작은 대기 창 (불확정 프로그레스바)</summary>
    public class BusyWindow : Window
    {
        private BusyWindow(string message)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x26));

            var panel = new StackPanel { Margin = new Thickness(20, 16, 20, 16), MinWidth = 260 };
            panel.Children.Add(new TextBlock
            {
                Text = message,
                Foreground = Brushes.White,
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 10),
            });
            panel.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 6 });
            Content = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2F, 0x9B, 0xFF)),
                BorderThickness = new Thickness(1),
                Child = panel,
            };
            // 드래그로 이동 가능
            MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch { } };
        }

        /// <summary>작업이 끝날 때까지 대기 창을 표시하며 결과를 반환한다.</summary>
        public static async Task<T> RunAsync<T>(string message, Func<Task<T>> work)
        {
            var win = new BusyWindow(message);
            win.Show();
            try
            {
                return await work();
            }
            finally
            {
                win.Close();
            }
        }
    }
}
