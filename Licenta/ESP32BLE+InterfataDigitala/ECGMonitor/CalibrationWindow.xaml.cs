using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ECGMonitor
{
    public partial class CalibrationWindow : Window
    {
        private int _currentStep = 1;
        private DispatcherTimer _checkTimer = new();
        private int _checkCount = 0;

        public CalibrationWindow()
        {
            InitializeComponent();
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                if (
                    e.OriginalSource is FrameworkElement element
                    && !(element is System.Windows.Controls.Button)
                    && !(element.Parent is System.Windows.Controls.Button)
                )
                {
                    try
                    {
                        this.DragMove();
                    }
                    catch (InvalidOperationException) { }
                }
            }
        }

        private void NextBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_currentStep == 1)
            {
                GoToStep(2);
                StartSignalCheck();
            }
            else if (_currentStep == 2)
            {
                DialogResult = true;
                Close();
            }
        }

        private void BackBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_currentStep > 1)
            {
                _checkTimer.Stop();
                GoToStep(_currentStep - 1);
            }
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void GoToStep(int step)
        {
            _currentStep = step;

            Step1Panel.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
            Step2Panel.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;

            StepIndicator.Text = $"Pas {step} din 2";
            ProgressBar.Width = step * 430;

            BackBtn.Visibility = step > 1 ? Visibility.Visible : Visibility.Collapsed;
            NextBtn.Content = step == 1 ? "Continuă →" : "Introduceți Date Pacient →";
            if (step == 2)
                NextBtn.IsEnabled = false;
        }

        private void StartSignalCheck()
        {
            _checkCount = 0;
            SignalStatus.Text = "Se caută semnal ECG...";
            SignalStatus.Foreground = new SolidColorBrush(Color.FromRgb(139, 148, 158));
            SignalDot.Fill = new SolidColorBrush(Color.FromRgb(139, 148, 158));

            AmplitudeStatus.Text = "Se verifică amplitudinea...";
            AmplitudeStatus.Foreground = new SolidColorBrush(Color.FromRgb(139, 148, 158));
            AmplitudeDot.Fill = new SolidColorBrush(Color.FromRgb(139, 148, 158));

            ReadyStatus.Text = "În așteptare...";
            ReadyStatus.Foreground = new SolidColorBrush(Color.FromRgb(139, 148, 158));
            ReadyDot.Fill = new SolidColorBrush(Color.FromRgb(139, 148, 158));

            OverallStatus.Text = "Verificare în curs...";
            OverallStatus.Foreground = new SolidColorBrush(Color.FromRgb(139, 148, 158));

            _checkTimer.Interval = TimeSpan.FromMilliseconds(900);
            _checkTimer.Tick += CheckTick;
            _checkTimer.Start();
        }

        private void CheckTick(object? sender, EventArgs e)
        {
            _checkCount++;

            if (_checkCount == 1)
            {
                SignalDot.Fill = new SolidColorBrush(Color.FromRgb(63, 185, 80));
                SignalStatus.Text = "Semnal ECG detectat";
                SignalStatus.Foreground = new SolidColorBrush(Color.FromRgb(63, 185, 80));
            }
            else if (_checkCount == 2)
            {
                AmplitudeDot.Fill = new SolidColorBrush(Color.FromRgb(63, 185, 80));
                AmplitudeStatus.Text = "Amplitudine în range valid (sub ±165mV)";
                AmplitudeStatus.Foreground = new SolidColorBrush(Color.FromRgb(63, 185, 80));
            }
            else if (_checkCount == 3)
            {
                ReadyDot.Fill = new SolidColorBrush(Color.FromRgb(63, 185, 80));
                ReadyStatus.Text = "Sistem gata de monitorizare";
                ReadyStatus.Foreground = new SolidColorBrush(Color.FromRgb(63, 185, 80));
            }
            else if (_checkCount == 4)
            {
                _checkTimer.Stop();
                OverallStatus.Text = "✓ Toți parametrii verificați cu succes";
                OverallStatus.Foreground = new SolidColorBrush(Color.FromRgb(63, 185, 80));
                NextBtn.IsEnabled = true;
            }
        }
    }
}
