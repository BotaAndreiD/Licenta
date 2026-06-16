using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ECGMonitor
{
    public partial class SplashScreen : Window
    {
        private DispatcherTimer _timer = new();
        private double _currentProgress = 0;
        private double _targetProgress = 0;
        private int _step = 0;

        // Date preincarcate — accesibile din MainWindow
        public List<double> PreloadedTimes { get; private set; } = new();
        public List<double> PreloadedAmplitudes { get; private set; } = new();

        private readonly (string text, double progress)[] _steps =
        {
            ("Se încarcă modulele sistem...", 0.15),
            ("Se inițializează interfața grafică...", 0.35),
            ("Se configurează motorul ECG...", 0.55),
            ("Se pregătesc algoritmii de detecție...", 0.75),
            ("Se încarcă datele ECG...", 0.95),
        };

        public SplashScreen()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Timer smooth pentru bara de progres — 30fps
            var smoothTimer = new DispatcherTimer();
            smoothTimer.Interval = TimeSpan.FromMilliseconds(16);
            smoothTimer.Tick += (s, ev) =>
            {
                if (_currentProgress < _targetProgress)
                {
                    _currentProgress += 0.008;
                    if (_currentProgress > _targetProgress)
                        _currentProgress = _targetProgress;

                    double barWidth = Math.Max(0, (ActualWidth - 96) * _currentProgress);
                    LoadingBar.Width = barWidth;
                }
            };
            smoothTimer.Start();

            // Pas cu pas cu delays reale
            foreach (var (text, progress) in _steps)
            {
                LoadingText.Text = text;
                _targetProgress = progress;
                await Task.Delay(500);
                _step++;
            }

            // Incarcare reala date ECG in background
            LoadingText.Text = "Se încarcă semnalul ECG...";
            _targetProgress = 0.95;

            await Task.Run(() => PreloadECGData());

            _targetProgress = 1.0;
            await Task.Delay(400);

            smoothTimer.Stop();
            LoadingBar.Background = new SolidColorBrush(Color.FromRgb(63, 185, 80));
            LoadingBar.Width = ActualWidth - 96;

            await Task.Delay(300);

            DialogResult = true;
            Close();
        }

        private void PreloadECGData()
        {
            string[] possiblePaths =
            {
                "ekg_signal2.txt",
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ekg_signal2.txt"),
                Path.Combine(Directory.GetCurrentDirectory(), "ekg_signal2.txt"),
            };

            string? filePath = null;
            foreach (var p in possiblePaths)
                if (File.Exists(p))
                {
                    filePath = p;
                    break;
                }

            if (filePath == null)
                return;

            var lines = File.ReadAllLines(filePath);
            foreach (var line in lines)
            {
                if (line.StartsWith("#") || string.IsNullOrWhiteSpace(line))
                    continue;
                var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                    continue;
                if (
                    double.TryParse(
                        parts[0],
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out double t
                    )
                    && double.TryParse(
                        parts[1],
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out double amp
                    )
                )
                {
                    PreloadedTimes.Add(t);
                    PreloadedAmplitudes.Add(amp * 1000.0);
                }
            }

            // Skip primele 35 secunde
            int startIndex = 0;
            for (int i = 0; i < PreloadedTimes.Count; i++)
            {
                if (PreloadedTimes[i] >= 35.0)
                {
                    startIndex = i;
                    break;
                }
            }
            if (startIndex > 0)
            {
                PreloadedTimes = PreloadedTimes.GetRange(
                    startIndex,
                    PreloadedTimes.Count - startIndex
                );
                PreloadedAmplitudes = PreloadedAmplitudes.GetRange(
                    startIndex,
                    PreloadedAmplitudes.Count - startIndex
                );
            }
        }
    }
}
