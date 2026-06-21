using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace ECGMonitor
{
    public partial class SplashScreen : Window
    {
        private TaskCompletionSource<bool>? _retryRequested;

        public SplashScreen()
        {
            InitializeComponent();
            Loaded += async (s, e) => await ConnectLoop();
        }

        private async Task ConnectLoop()
        {
            bool connected = false;

            while (!connected)
            {
                RetryBtn.Visibility = Visibility.Collapsed;
                LoadingBar.Background = new SolidColorBrush(Color.FromRgb(88, 166, 255));
                LoadingBar.Width = Math.Max(0, (ActualWidth - 96) * 0.4);

                connected = await BleConnectionService.ConnectAsync(status =>
                    Dispatcher.Invoke(() => LoadingText.Text = status)
                );

                if (!connected)
                {
                    LoadingBar.Background = new SolidColorBrush(Color.FromRgb(255, 107, 107));
                    LoadingBar.Width = Math.Max(0, ActualWidth - 96);
                    RetryBtn.Visibility = Visibility.Visible;

                    _retryRequested = new TaskCompletionSource<bool>();
                    await _retryRequested.Task;
                }
            }

            LoadingBar.Background = new SolidColorBrush(Color.FromRgb(63, 185, 80));
            LoadingBar.Width = Math.Max(0, ActualWidth - 96);
            LoadingText.Text = "Conectat — se încarcă interfața...";
            await Task.Delay(500);

            DialogResult = true;
            Close();
        }

        private void RetryBtn_Click(object sender, RoutedEventArgs e)
        {
            RetryBtn.Visibility = Visibility.Collapsed;
            _retryRequested?.TrySetResult(true);
        }
    }
}
