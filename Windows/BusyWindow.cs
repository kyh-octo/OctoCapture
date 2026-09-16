using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OctoCapture.Windows
{
    /// <summary>진행 상황 보고용 (Fraction=null이면 불확정 프로그레스)</summary>
    public sealed class BusyProgress
    {
        public double? Fraction { get; init; }
        public string? Status { get; init; }
    }

    /// <summary>백그라운드 작업 동안 표시되는 작은 대기 창 (불확정/확정 프로그레스, 선택적 취소 버튼)</summary>
    public class BusyWindow : Window
    {
        private readonly TextBlock _status;
        private readonly ProgressBar _bar;
        private readonly Button? _cancelBtn;
        private readonly CancellationTokenSource _cts = new();

        private BusyWindow(string message, bool allowCancel)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x26));

            var panel = new StackPanel { Margin = new Thickness(20, 16, 20, 16), MinWidth = 360 };
            panel.Children.Add(new TextBlock
            {
                Text = message,
                Foreground = Brushes.White,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6),
            });
            _status = new TextBlock
            {
                Text = "",
                Foreground = new SolidColorBrush(Color.FromRgb(0xBB, 0xBB, 0xBB)),
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 8),
                TextWrapping = TextWrapping.Wrap,
            };
            panel.Children.Add(_status);
            _bar = new ProgressBar { IsIndeterminate = true, Height = 8, Minimum = 0, Maximum = 100 };
            panel.Children.Add(_bar);

            if (allowCancel)
            {
                _cancelBtn = new Button
                {
                    Content = "취소",
                    Padding = new Thickness(14, 4, 14, 4),
                    Margin = new Thickness(0, 10, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Right,
                };
                _cancelBtn.Click += (_, _) =>
                {
                    _cancelBtn.IsEnabled = false;
                    _status.Text = "취소하는 중…";
                    _cts.Cancel();
                };
                panel.Children.Add(_cancelBtn);
            }

            Content = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2F, 0x9B, 0xFF)),
                BorderThickness = new Thickness(1),
                Child = panel,
            };
            // 드래그로 이동 가능
            MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch { } };
        }

        private void Apply(BusyProgress p)
        {
            if (p.Fraction is double f)
            {
                _bar.IsIndeterminate = false;
                _bar.Value = Math.Clamp(f, 0, 1) * 100;
            }
            else
            {
                _bar.IsIndeterminate = true;
            }
            if (p.Status != null) _status.Text = p.Status;
        }

        /// <summary>작업이 끝날 때까지 대기 창(불확정 프로그레스)을 표시하며 결과를 반환한다.</summary>
        public static Task<T> RunAsync<T>(string message, Func<Task<T>> work) =>
            RunAsync(message, (_, _) => work(), allowCancel: false);

        /// <summary>
        /// 진행률 보고와 취소가 가능한 대기 창. 취소 시 work에 전달된 토큰이 취소되며
        /// work가 OperationCanceledException을 던지면 그대로 호출자에게 전파된다.
        /// </summary>
        public static async Task<T> RunAsync<T>(string message,
            Func<IProgress<BusyProgress>, CancellationToken, Task<T>> work, bool allowCancel)
        {
            var win = new BusyWindow(message, allowCancel);
            win.Show();
            // Progress<T>는 생성 시점(UI 스레드)의 SynchronizationContext로 콜백을 마샬링한다
            var progress = new Progress<BusyProgress>(win.Apply);
            try
            {
                return await work(progress, win._cts.Token);
            }
            finally
            {
                win.Close();
                win._cts.Dispose();
            }
        }
    }
}
