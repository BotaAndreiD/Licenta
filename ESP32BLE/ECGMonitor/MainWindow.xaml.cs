using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Series;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;
using WpfColor = System.Windows.Media.Color;

namespace ECGMonitor
{
    public partial class MainWindow : Window
    {
        private double _bleTime = -1.0;
        private double _lastRawFileTime = -1.0;
        private double _bleLoopOffset = 0.0;
        private bool _recalibrateOnNextSample = false;

        private List<double> _times = new();
        private List<double> _amplitudes = new();

        private PlotModel _plotModel = new();
        private LineSeries _ecgSeries = new();
        private LinearAxis _xAxis = new();
        private LinearAxis _yAxis = new();

        private DispatcherTimer _timer = new();
        private DispatcherTimer _clockTimer = new();
        private DispatcherTimer _electrodeTimer = new();
        private string _lastBleRawStr = "";
        private bool _electrodeAlertShown = false;

        private int _currentIndex = 0;
        private int _windowSize = 1280;
        private int _stepPerTick = 4;
        private bool _isPlaying = true;
        private const double SaturationLimit = 165.0;
        private const double MaxPlausiblePauseSeconds = 5.0;

        private string _patientName = "Necunoscut";
        private string _patientAge = "--";
        private string _patientSex = "--";
        private string _patientId = "--";
        private string _doctorName = "--";
        private string _clinicalNotes = "";

        private ScatterSeries _rPeaks = new();
        private ScatterSeries _pPeaks = new();
        private ScatterSeries _qPoints = new();
        private ScatterSeries _sPoints = new();
        private ScatterSeries _tPeaks = new();

        private List<double> _rrIntervals = new();
        private List<double> _rAmplitudes = new();

        private int _tachycardiaFrames = 0;
        private const int TachycardiaThreshold = 33;

        private int _totalExtrasystoles = 0;
        private double _lastCountedExtrasystoleTime = -1.0;
        private double _sessionMaxPauseMs = 0.0;
        private double _lastProcessedPauseTime = -1.0;

        private Queue<double> _hrTrend = new();
        private const int HRTrendMaxPoints = 60;
        private int _hrTrendTickCounter = 0;

        public MainWindow()
        {
            InitializeComponent();
            WindowMaximizeFix.Apply(this);
            Window_StateChanged(this, EventArgs.Empty);

            var calibration = new CalibrationWindow();
            if (calibration.ShowDialog() != true)
            {
                Application.Current.Shutdown();
                return;
            }

            var dialog = new PatientDialog();
            if (dialog.ShowDialog() == true)
            {
                _patientName = string.IsNullOrEmpty(dialog.PatientName)
                    ? "Necunoscut"
                    : dialog.PatientName;
                _patientAge = string.IsNullOrEmpty(dialog.PatientAge) ? "--" : dialog.PatientAge;
                _patientSex = dialog.PatientSex;
                _patientId = string.IsNullOrEmpty(dialog.PatientId) ? "--" : dialog.PatientId;
                _doctorName = string.IsNullOrEmpty(dialog.DoctorName) ? "--" : dialog.DoctorName;
                _clinicalNotes = dialog.ClinicalNotes;
            }

            SetupPlot();
            StartTimers();

            AttachLiveConnection();
        }

        private void MinimizeBtn_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void MaximizeBtn_Click(object sender, RoutedEventArgs e)
        {
            WindowState =
                WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Window_StateChanged(object sender, EventArgs e)
        {
            MaximizeBtn.Content = WindowState == WindowState.Maximized ? "❐" : "▢";
        }

        private void AttachLiveConnection(bool isReconnect = false)
        {
            if (isReconnect)
                _recalibrateOnNextSample = true;

            if (BleConnectionService.Characteristic != null)
            {
                BleConnectionService.Characteristic.ValueChanged -= Characteristic_ValueChanged;
                BleConnectionService.Characteristic.ValueChanged += Characteristic_ValueChanged;
            }

            if (BleConnectionService.Device != null)
            {
                BleConnectionService.Device.ConnectionStatusChanged -=
                    Device_ConnectionStatusChanged;
                BleConnectionService.Device.ConnectionStatusChanged +=
                    Device_ConnectionStatusChanged;
            }

            StatusDot.Fill = new SolidColorBrush(WpfColor.FromRgb(88, 166, 255));
            DisconnectOverlay.Visibility = Visibility.Collapsed;
        }

        private void Device_ConnectionStatusChanged(BluetoothLEDevice sender, object args)
        {
            if (sender.ConnectionStatus != BluetoothConnectionStatus.Disconnected)
                return;

            Dispatcher.Invoke(() =>
            {
                StatusDot.Fill = new SolidColorBrush(WpfColor.FromRgb(255, 107, 107));
                DisconnectOverlay.Visibility = Visibility.Visible;
            });

            _ = ReconnectLoop();
        }

        private async Task ReconnectLoop()
        {
            bool ok = false;
            while (!ok)
            {
                ok = await BleConnectionService.ConnectAsync(status =>
                    Dispatcher.Invoke(() => OverlayStatusText.Text = status)
                );

                if (!ok)
                    await Task.Delay(1000);
            }

            Dispatcher.Invoke(() => AttachLiveConnection(isReconnect: true));
        }

        private void Characteristic_ValueChanged(
            GattCharacteristic sender,
            GattValueChangedEventArgs args
        )
        {
            try
            {
                var dataReader = DataReader.FromBuffer(args.CharacteristicValue);
                var rawBytes = new byte[dataReader.UnconsumedBufferLength];
                dataReader.ReadBytes(rawBytes);
                string str = System.Text.Encoding.UTF8.GetString(rawBytes);

                string trimmed = str.Trim();
                if (trimmed == _lastBleRawStr)
                    return;

                var parts = trimmed.Split('\t');

                double fileTime = 0;
                double amp = 0;
                bool ok =
                    parts.Length >= 2
                    && double.TryParse(
                        parts[0],
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out fileTime
                    )
                    && double.TryParse(
                        parts[1],
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out amp
                    );

                if (!ok)
                    return;

                Dispatcher.Invoke(() =>
                {
                    if (_recalibrateOnNextSample)
                    {
                        // Reconectare confirmata: nu mai ghicim cate ture a facut fisierul cat
                        // am fost deconectati - recalibram offsetul ca sa continuam exact de unde
                        // am ramas, indiferent de fileTime-ul brut primit acum.
                        _bleLoopOffset = _bleTime + 0.05 - fileTime;
                        _lastRawFileTime = fileTime;
                        _recalibrateOnNextSample = false;
                    }
                    else if (fileTime < _lastRawFileTime - 1.0)
                    {
                        _bleLoopOffset += _lastRawFileTime;
                    }

                    _lastRawFileTime = fileTime;
                    double effectiveTime = fileTime + _bleLoopOffset;

                    if (effectiveTime <= _bleTime)
                        return;

                    _lastBleRawStr = trimmed;
                    double scaledAmp = amp * 1000.0;

                    _bleTime = effectiveTime;
                    _times.Add(_bleTime);
                    _amplitudes.Add(scaledAmp);
                });
            }
            catch
            {
                // citire BLE invalida - ignorata
            }
        }

        private void SetupPlot()
        {
            _plotModel.Background = OxyColors.Transparent;
            _plotModel.PlotAreaBackground = OxyColor.FromArgb(255, 13, 17, 23);
            _plotModel.PlotAreaBorderColor = OxyColor.FromArgb(80, 48, 54, 61);
            _plotModel.PlotAreaBorderThickness = new OxyThickness(1);
            _plotModel.Padding = new OxyThickness(10, 10, 10, 10);

            _xAxis = new LinearAxis
            {
                Position = AxisPosition.Bottom,
                Title = "Time (s)",
                TitleFontSize = 11,
                TitleColor = OxyColor.FromArgb(255, 139, 148, 158),
                TextColor = OxyColor.FromArgb(255, 139, 148, 158),
                MajorGridlineStyle = LineStyle.Solid,
                MajorGridlineColor = OxyColor.FromArgb(35, 139, 0, 0),
                MinorGridlineStyle = LineStyle.Solid,
                MinorGridlineColor = OxyColor.FromArgb(15, 139, 0, 0),
                AxislineColor = OxyColor.FromArgb(80, 48, 54, 61),
                FontSize = 10,
                IsZoomEnabled = false,
                IsPanEnabled = false,
                MajorStep = 0.2,
                MinorStep = 0.04,
            };

            _yAxis = new LinearAxis
            {
                Position = AxisPosition.Left,
                Title = "Amplitude (mV)",
                TitleFontSize = 11,
                TitleColor = OxyColor.FromArgb(255, 139, 148, 158),
                TextColor = OxyColor.FromArgb(255, 139, 148, 158),
                MajorGridlineStyle = LineStyle.Solid,
                MajorGridlineColor = OxyColor.FromArgb(35, 139, 0, 0),
                MinorGridlineStyle = LineStyle.Solid,
                MinorGridlineColor = OxyColor.FromArgb(15, 139, 0, 0),
                AxislineColor = OxyColor.FromArgb(80, 48, 54, 61),
                FontSize = 10,
                IsZoomEnabled = false,
                IsPanEnabled = false,
                MajorStep = 0.5,
                MinorStep = 0.1,
            };

            var satPlus = new LineAnnotation
            {
                Type = LineAnnotationType.Horizontal,
                Y = SaturationLimit,
                Color = OxyColor.FromArgb(60, 255, 107, 107),
                LineStyle = LineStyle.Dash,
                StrokeThickness = 1,
            };
            var satMinus = new LineAnnotation
            {
                Type = LineAnnotationType.Horizontal,
                Y = -SaturationLimit,
                Color = OxyColor.FromArgb(60, 255, 107, 107),
                LineStyle = LineStyle.Dash,
                StrokeThickness = 1,
            };

            _ecgSeries = new LineSeries
            {
                Color = OxyColor.FromArgb(255, 0, 210, 130),
                StrokeThickness = 1.5,
                MarkerType = MarkerType.None,
            };
            _rPeaks = new ScatterSeries
            {
                MarkerType = MarkerType.Cross,
                MarkerSize = 5,
                MarkerStroke = OxyColor.FromArgb(220, 255, 80, 80),
                MarkerStrokeThickness = 2,
                MarkerFill = OxyColors.Transparent,
            };
            _pPeaks = new ScatterSeries
            {
                MarkerType = MarkerType.Circle,
                MarkerSize = 3,
                MarkerFill = OxyColors.Transparent,
                MarkerStroke = OxyColor.FromArgb(200, 255, 210, 50),
                MarkerStrokeThickness = 1.5,
            };
            _qPoints = new ScatterSeries
            {
                MarkerType = MarkerType.Square,
                MarkerSize = 2,
                MarkerFill = OxyColors.Transparent,
                MarkerStroke = OxyColor.FromArgb(160, 255, 140, 0),
                MarkerStrokeThickness = 1.5,
            };
            _sPoints = new ScatterSeries
            {
                MarkerType = MarkerType.Square,
                MarkerSize = 2,
                MarkerFill = OxyColors.Transparent,
                MarkerStroke = OxyColor.FromArgb(160, 255, 140, 0),
                MarkerStrokeThickness = 1.5,
            };
            _tPeaks = new ScatterSeries
            {
                MarkerType = MarkerType.Circle,
                MarkerSize = 3,
                MarkerFill = OxyColors.Transparent,
                MarkerStroke = OxyColor.FromArgb(200, 80, 200, 255),
                MarkerStrokeThickness = 1.5,
            };

            _plotModel.Axes.Add(_xAxis);
            _plotModel.Axes.Add(_yAxis);
            _plotModel.Annotations.Add(satPlus);
            _plotModel.Annotations.Add(satMinus);
            _plotModel.Series.Add(_ecgSeries);
            _plotModel.Series.Add(_pPeaks);
            _plotModel.Series.Add(_qPoints);
            _plotModel.Series.Add(_rPeaks);
            _plotModel.Series.Add(_sPoints);
            _plotModel.Series.Add(_tPeaks);

            PlotView.Model = _plotModel;
        }

        private void StartTimers()
        {
            _timer.Interval = TimeSpan.FromMilliseconds(30);
            _timer.Tick += Timer_Tick;
            _timer.Start();

            _clockTimer.Interval = TimeSpan.FromSeconds(1);
            _clockTimer.Tick += (s, e) => HeaderTime.Text = DateTime.Now.ToString("HH:mm:ss");
            _clockTimer.Start();

            _electrodeTimer.Interval = TimeSpan.FromSeconds(90);
            _electrodeTimer.Tick += (s, e) =>
            {
                _electrodeTimer.Stop();
                if (!_electrodeAlertShown)
                {
                    _electrodeAlertShown = true;
                    ShowElectrodeAlert();
                }
            };
            _electrodeTimer.Start();
        }

        private void ShowElectrodeAlert()
        {
            var popup = new Window
            {
                Title = "Alertă Electrozi",
                Width = 380,
                Height = 220,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = new SolidColorBrush(WpfColor.FromRgb(13, 17, 23)),
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = true,
            };
            var border = new System.Windows.Controls.Border
            {
                Background = new SolidColorBrush(WpfColor.FromRgb(22, 27, 34)),
                CornerRadius = new CornerRadius(12),
                BorderBrush = new SolidColorBrush(WpfColor.FromRgb(255, 107, 107)),
                BorderThickness = new Thickness(1),
            };
            var stack = new StackPanel { Margin = new Thickness(28) };
            stack.Children.Add(
                new TextBlock
                {
                    Text = "⚠",
                    Foreground = new SolidColorBrush(WpfColor.FromRgb(255, 107, 107)),
                    FontSize = 28,
                    Margin = new Thickness(0, 0, 0, 10),
                }
            );
            stack.Children.Add(
                new TextBlock
                {
                    Text = "Contact Electrod Deficitar",
                    Foreground = new SolidColorBrush(WpfColor.FromRgb(230, 237, 243)),
                    FontSize = 16,
                    FontWeight = System.Windows.FontWeights.Bold,
                    FontFamily = new FontFamily("Segoe UI"),
                    Margin = new Thickness(0, 0, 0, 8),
                }
            );
            stack.Children.Add(
                new TextBlock
                {
                    Text =
                        "Semnalul ECG depășește limita INA (±165mV).\nReatasați corect electrozii.",
                    Foreground = new SolidColorBrush(WpfColor.FromRgb(139, 148, 158)),
                    FontSize = 12,
                    FontFamily = new FontFamily("Segoe UI"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 20),
                }
            );
            var btn = new System.Windows.Controls.Button
            {
                Content = "OK — Am înțeles",
                Background = new SolidColorBrush(WpfColor.FromRgb(255, 107, 107)),
                Foreground = new SolidColorBrush(WpfColor.FromRgb(255, 255, 255)),
                BorderThickness = new Thickness(0),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 13,
                FontWeight = System.Windows.FontWeights.Bold,
                Padding = new Thickness(20, 10, 20, 10),
                Cursor = System.Windows.Input.Cursors.Hand,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            };
            btn.Click += (s, e) => popup.Close();
            stack.Children.Add(btn);
            border.Child = stack;
            popup.Content = border;
            popup.ShowDialog();
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (!_isPlaying || _amplitudes.Count == 0)
                return;

            // In BLE mode - afiseaza punctele noi in ritmul ales de utilizator (Viteza),
            // restul ramane in buffer (_times/_amplitudes) si se afiseaza la urmatoarele tick-uri
            int drawn = 0;
            while (_currentIndex < _amplitudes.Count && drawn < _stepPerTick)
            {
                _ecgSeries.Points.Add(
                    new DataPoint(_times[_currentIndex], _amplitudes[_currentIndex])
                );
                _currentIndex++;
                drawn++;
            }

            if (_ecgSeries.Points.Count > _windowSize)
                _ecgSeries.Points.RemoveRange(0, _ecgSeries.Points.Count - _windowSize);

            if (_ecgSeries.Points.Count > 0)
            {
                _xAxis.Minimum = _ecgSeries.Points[0].X;
                _xAxis.Maximum = _ecgSeries.Points[^1].X;

                double visibleSpan = _xAxis.Maximum - _xAxis.Minimum;
                _xAxis.MajorStep = visibleSpan switch
                {
                    <= 6 => 0.5,
                    <= 12 => 1,
                    <= 25 => 2,
                    _ => 5,
                };
                _xAxis.MinorStep = _xAxis.MajorStep / 5;

                var amps = _ecgSeries.Points.Select(p => p.Y).ToList();
                double margin = (amps.Max() - amps.Min()) * 0.25;
                _yAxis.Minimum = amps.Min() - margin;
                _yAxis.Maximum = amps.Max() + margin;

                DetectPQRST();
                UpdateSignalQuality(amps);
            }

            _plotModel.InvalidatePlot(true);
        }

        private static double Median(List<double> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            int mid = sorted.Count / 2;
            return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2.0 : sorted[mid];
        }

        private void DetectPQRST()
        {
            var points = _ecgSeries.Points;
            int n = points.Count;
            if (n < 100)
                return;

            _rPeaks.Points.Clear();
            _pPeaks.Points.Clear();
            _qPoints.Points.Clear();
            _sPoints.Points.Clear();
            _tPeaks.Points.Clear();
            _rAmplitudes.Clear();

            var amps = points.Select(p => p.Y).ToList();
            double minAmp = amps.Min(),
                maxAmp = amps.Max(),
                range = maxAmp - minAmp;

            double avgDt = (points[n - 1].X - points[0].X) / Math.Max(1, n - 1);
            if (avgDt <= 0)
                return;
            int OffsetSamples(double seconds) => Math.Max(1, (int)Math.Round(seconds / avgDt));

            // ── Pan-Tompkins preprocessing — detecție R robustă la zgomot ──────────
            // Derivată → pătrat → integrare pe fereastră mobilă formează un envelope
            // unde vârfurile QRS ies în evidență indiferent de amplitudinea absolută.
            var envelope = PanTompkinsEnvelope(amps, avgDt);
            double envMedian = Median(envelope);
            double envMad = Median(envelope.Select(v => Math.Abs(v - envMedian)).ToList());
            double envSigma = envMad * 1.4826;
            double envThreshold = Math.Max(
                envMedian + envSigma * 3.0,
                envMedian + (envelope.Max() - envMedian) * 0.25
            );

            int minRRSamples = OffsetSamples(0.30);
            int backprojectWin = OffsetSamples(0.060);

            List<int> rIndices = new();
            for (int i = 5; i < n - 5; i++)
            {
                if (
                    envelope[i] > envThreshold
                    && envelope[i] > envelope[i - 1]
                    && envelope[i] > envelope[i - 2]
                    && envelope[i] > envelope[i + 1]
                    && envelope[i] > envelope[i + 2]
                )
                {
                    if (rIndices.Count == 0 || i - rIndices[^1] > minRRSamples)
                    {
                        // Backproiecție: maximul din semnalul original în ±60ms față de vârful din envelope
                        int lo = Math.Max(0, i - backprojectWin);
                        int hi = Math.Min(n - 1, i + backprojectWin);
                        int actualR = lo;
                        for (int j = lo + 1; j <= hi; j++)
                            if (amps[j] > amps[actualR])
                                actualR = j;
                        if (amps[actualR] > minAmp + range * 0.25)
                            rIndices.Add(actualR);
                    }
                }
            }

            List<double> pTimes = new(),
                qTimes = new(),
                sTimes = new(),
                tTimes = new();
            var stDeviations = new List<double>();
            var rTimesList = rIndices.Select(i => points[i].X).ToList();

            for (int beatIdx = 0; beatIdx < rIndices.Count; beatIdx++)
            {
                int ri = rIndices[beatIdx];
                _rPeaks.Points.Add(new ScatterPoint(points[ri].X, points[ri].Y));
                _rAmplitudes.Add(points[ri].Y);

                // ── Ferestre adaptive proporționale cu RR-ul local ─────────────────
                // Rezolvă problema că la bradicardie/tahicardie undele sunt mai depărtate
                // sau mai apropiate decât presupun ferestrele fixe.
                double localRR;
                if (beatIdx > 0 && beatIdx < rIndices.Count - 1)
                    localRR = (rTimesList[beatIdx + 1] - rTimesList[beatIdx - 1]) / 2.0;
                else if (beatIdx > 0)
                    localRR = rTimesList[beatIdx] - rTimesList[beatIdx - 1];
                else if (beatIdx < rIndices.Count - 1)
                    localRR = rTimesList[beatIdx + 1] - rTimesList[beatIdx];
                else
                    localRR = 0.80;
                localRR = Math.Clamp(localRR, 0.33, 2.0);

                int qWinStart = OffsetSamples(Math.Min(localRR * 0.15, 0.117));
                int qWinEnd = OffsetSamples(0.015);
                int sWinStart = OffsetSamples(0.015);
                int sWinEnd = OffsetSamples(Math.Min(localRR * 0.15, 0.117));
                int pWinStart = OffsetSamples(Math.Min(localRR * 0.70, 0.450));
                int pWinEnd = OffsetSamples(Math.Max(localRR * 0.25, 0.100));
                int tWinStart = OffsetSamples(0.140);
                int tWinEnd = OffsetSamples(Math.Min(localRR * 0.60, 0.500));

                // ── Q ──────────────────────────────────────────────────────────────
                int qStart = Math.Max(0, ri - qWinStart);
                int qEnd = Math.Max(0, ri - qWinEnd);
                int qIdx = qStart;
                bool qFound = false;
                if (qStart < qEnd)
                {
                    for (int j = qStart; j <= qEnd; j++)
                        if (amps[j] < amps[qIdx])
                            qIdx = j;
                    _qPoints.Points.Add(new ScatterPoint(points[qIdx].X, points[qIdx].Y));
                    qTimes.Add(points[qIdx].X);
                    qFound = true;
                }

                // ── S ──────────────────────────────────────────────────────────────
                int sStart = Math.Min(n - 1, ri + sWinStart);
                int sEnd = Math.Min(n - 1, ri + sWinEnd);
                int sIdx = sStart;
                bool sFound = false;
                if (sStart < sEnd)
                {
                    for (int j = sStart; j <= sEnd; j++)
                        if (amps[j] < amps[sIdx])
                            sIdx = j;
                    _sPoints.Points.Add(new ScatterPoint(points[sIdx].X, points[sIdx].Y));
                    sTimes.Add(points[sIdx].X);
                    sFound = true;
                }

                // ── P ──────────────────────────────────────────────────────────────
                int pStart = Math.Max(0, ri - pWinStart);
                int pEnd = Math.Max(0, ri - pWinEnd);
                bool pFound = false;
                int pIdx = pStart;
                if (pStart < pEnd)
                {
                    for (int j = pStart; j <= pEnd; j++)
                        if (amps[j] > amps[pIdx])
                            pIdx = j;
                    if (amps[pIdx] > minAmp + range * 0.05)
                    {
                        _pPeaks.Points.Add(new ScatterPoint(points[pIdx].X, points[pIdx].Y));
                        pTimes.Add(points[pIdx].X);
                        pFound = true;
                    }
                }

                // ── T ──────────────────────────────────────────────────────────────
                int tStart = Math.Min(n - 1, ri + tWinStart);
                int tEnd = Math.Min(n - 1, ri + tWinEnd);
                if (tStart < tEnd)
                {
                    int tIdx = tStart;
                    for (int j = tStart; j <= tEnd; j++)
                        if (amps[j] > amps[tIdx])
                            tIdx = j;
                    if (amps[tIdx] > minAmp + range * 0.05)
                    {
                        _tPeaks.Points.Add(new ScatterPoint(points[tIdx].X, points[tIdx].Y));
                        tTimes.Add(points[tIdx].X);
                    }
                }

                // ── Segment ST: deviația J-point față de baseline PR ───────────────
                // J-point = ~60ms după S; baseline = media segmentului PR
                if (sFound && qFound)
                {
                    int jOffset = OffsetSamples(0.060);
                    int jIdx = Math.Min(n - 1, sIdx + jOffset);
                    double jAmp = amps[jIdx];

                    double baseline;
                    if (pFound && pIdx < qIdx)
                    {
                        int prLen = qIdx - pIdx;
                        baseline =
                            prLen > 2
                                ? Enumerable.Range(pIdx, prLen).Select(k => amps[k]).Average()
                                : (amps[pIdx] + amps[qIdx]) / 2.0;
                    }
                    else
                    {
                        int isoIdx = Math.Max(0, qIdx - OffsetSamples(0.040));
                        baseline = amps[isoIdx];
                    }

                    stDeviations.Add(jAmp - baseline);
                }
            }

            if (rIndices.Count >= 2)
            {
                _rrIntervals.Clear();
                for (int i = 1; i < rTimesList.Count; i++)
                    _rrIntervals.Add(rTimesList[i] - rTimesList[i - 1]);

                double avgRR = _rrIntervals.Average();
                double bpm = 60.0 / avgRR;

                if (bpm > 30 && bpm < 220)
                {
                    HRValue.Text = ((int)bpm).ToString();
                    try
                    {
                        HRBar.Value = Math.Min(180, bpm);
                    }
                    catch { }

                    _hrTrendTickCounter++;
                    if (_hrTrendTickCounter >= 33)
                    {
                        _hrTrendTickCounter = 0;
                        UpdateHRTrend(bpm);
                    }

                    if (bpm > 110)
                        _tachycardiaFrames++;
                    else
                        _tachycardiaFrames = Math.Max(0, _tachycardiaFrames - 1);

                    double rrStd = Math.Sqrt(
                        _rrIntervals.Select(r => Math.Pow(r - avgRR, 2)).Average()
                    );

                    // RR haotic + unde P absente → fibrilație atrială;
                    // RR haotic dar P prezente → aritmie nespecificată
                    if (rrStd > 0.15)
                    {
                        bool pAbsent = _pPeaks.Points.Count < rIndices.Count * 0.5;
                        if (pAbsent)
                            SetRitm(
                                "FIBR. ATRIALĂ",
                                WpfColor.FromRgb(255, 107, 107),
                                "RR haotic + unde P absente",
                                WpfColor.FromArgb(40, 255, 107, 107)
                            );
                        else
                            SetRitm(
                                "NEREGULAT",
                                WpfColor.FromRgb(255, 107, 107),
                                "Variabilitate R-R ridicată",
                                WpfColor.FromArgb(40, 255, 107, 107)
                            );
                    }
                    else if (bpm > 110 && _tachycardiaFrames > TachycardiaThreshold)
                        SetRitm(
                            "TAHICARDIE",
                            WpfColor.FromRgb(255, 180, 50),
                            $"Puls rapid susținut: {(int)bpm} bpm",
                            WpfColor.FromArgb(40, 255, 180, 50)
                        );
                    else if (bpm < 50)
                        SetRitm(
                            "BRADICARDIE",
                            WpfColor.FromRgb(80, 180, 255),
                            $"Puls lent: {(int)bpm} bpm",
                            WpfColor.FromArgb(40, 80, 180, 255)
                        );
                    else
                        SetRitm(
                            "NORMAL",
                            WpfColor.FromRgb(63, 185, 80),
                            "Ritm sinusal regulat",
                            WpfColor.FromArgb(255, 15, 42, 26)
                        );

                    double sdnn =
                        Math.Sqrt(_rrIntervals.Select(r => Math.Pow(r - avgRR, 2)).Average())
                        * 1000.0;
                    HRVValue.Text = ((int)sdnn).ToString();
                    HRVStatus.Text =
                        sdnn > 50 ? "Bun"
                        : sdnn > 20 ? "Moderat"
                        : "Scăzut";
                    HRVStatus.Foreground =
                        sdnn > 50 ? new SolidColorBrush(WpfColor.FromRgb(63, 185, 80))
                        : sdnn > 20 ? new SolidColorBrush(WpfColor.FromRgb(255, 180, 50))
                        : new SolidColorBrush(WpfColor.FromRgb(255, 107, 107));

                    if (_rAmplitudes.Count > 0)
                    {
                        double avgR = _rAmplitudes.Average();
                        RAmplValue.Text = avgR.ToString("F2");
                        RAmplStatus.Text =
                            avgR > 1.5 ? "Posibilă hipertrofie"
                            : avgR > 0.5 ? "Normal"
                            : "Amplitudine scăzută";
                        RAmplStatus.Foreground =
                            avgR > 1.5
                                ? new SolidColorBrush(WpfColor.FromRgb(255, 180, 50))
                                : new SolidColorBrush(WpfColor.FromRgb(63, 185, 80));
                    }

                    // Total cumulativ pe toată sesiunea — aceeași bătaie nu se numără de două ori
                    for (int i = 0; i < _rrIntervals.Count; i++)
                    {
                        double beatTime = rTimesList[i + 1];
                        if (
                            _rrIntervals[i] < avgRR * 0.80
                            && beatTime > _lastCountedExtrasystoleTime
                        )
                        {
                            _totalExtrasystoles++;
                            _lastCountedExtrasystoleTime = beatTime;
                        }
                        if (beatTime > _lastProcessedPauseTime)
                        {
                            // RR-uri peste 5s nu sunt fiziologic plauzibile ca pauză cardiacă reală
                            if (_rrIntervals[i] <= MaxPlausiblePauseSeconds)
                                _sessionMaxPauseMs = Math.Max(
                                    _sessionMaxPauseMs,
                                    _rrIntervals[i] * 1000.0
                                );
                            _lastProcessedPauseTime = beatTime;
                        }
                    }
                    ExtrasistoleValue.Text = _totalExtrasystoles.ToString();
                    ExtrasistoleValue.Foreground =
                        _totalExtrasystoles > 0
                            ? new SolidColorBrush(WpfColor.FromRgb(255, 107, 107))
                            : new SolidColorBrush(WpfColor.FromRgb(210, 168, 255));

                    MaxPauseValue.Text = ((int)_sessionMaxPauseMs).ToString();
                    MaxPauseStatus.Text =
                        _sessionMaxPauseMs > 1500 ? "Atenție — pauză prelungită" : "Normal";
                    MaxPauseStatus.Foreground =
                        _sessionMaxPauseMs > 1500
                            ? new SolidColorBrush(WpfColor.FromRgb(255, 107, 107))
                            : new SolidColorBrush(WpfColor.FromRgb(63, 185, 80));

                    if (pTimes.Count > 0 && qTimes.Count > 0)
                    {
                        var validPR = new List<double>();
                        for (int i = 0; i < Math.Min(pTimes.Count, qTimes.Count); i++)
                        {
                            double pr = (qTimes[i] - pTimes[i]) * 1000.0;
                            if (pr >= 80 && pr <= 320)
                                validPR.Add(pr);
                        }
                        if (validPR.Count > 0)
                            PRValue.Text = ((int)validPR.Average()).ToString();
                    }

                    if (qTimes.Count > 0 && sTimes.Count > 0)
                    {
                        var validQRS = new List<double>();
                        for (int i = 0; i < Math.Min(qTimes.Count, sTimes.Count); i++)
                        {
                            double qrs = (sTimes[i] - qTimes[i]) * 1000.0;
                            if (qrs >= 40 && qrs <= 200)
                                validQRS.Add(qrs);
                        }
                        if (validQRS.Count > 0)
                            QRSValue.Text = ((int)validQRS.Average()).ToString();

                        // QTc per bătaie cu formula Bazett — folosim RR-ul bătăii curente,
                        // nu media globală, pentru că QT variază cu frecvența cardiacă.
                        if (tTimes.Count > 0)
                        {
                            var validQTc = new List<double>();
                            for (int i = 0; i < Math.Min(qTimes.Count, tTimes.Count); i++)
                            {
                                double qt = (tTimes[i] - qTimes[i]) * 1000.0;
                                if (qt < 200 || qt > 600)
                                    continue;
                                double beatRR = i < _rrIntervals.Count ? _rrIntervals[i] : avgRR;
                                validQTc.Add(qt / Math.Sqrt(beatRR));
                            }
                            if (validQTc.Count > 0)
                            {
                                double avgQTc = validQTc.Average();
                                QTcValue.Text = ((int)avgQTc).ToString();
                                QTcValue.Foreground =
                                    avgQTc > 450
                                        ? new SolidColorBrush(WpfColor.FromRgb(255, 107, 107))
                                        : new SolidColorBrush(WpfColor.FromRgb(230, 237, 243));
                            }
                        }
                    }

                    // Segment ST — deviația J-point față de linia izoelectrică PR
                    if (stDeviations.Count > 0)
                    {
                        double avgST = stDeviations.Average();
                        double rHeight =
                            _rAmplitudes.Count > 0 ? _rAmplitudes.Average() - minAmp : range;
                        double stPct = rHeight > 1 ? (avgST / rHeight) * 100.0 : 0;
                        STValue.Text = stPct >= 0 ? $"+{stPct:F1}%" : $"{stPct:F1}%";

                        WpfColor stColor;
                        string stStatus;
                        if (stPct > 15)
                        {
                            stStatus = "Posibilă supradenivelare";
                            stColor = WpfColor.FromRgb(255, 107, 107);
                        }
                        else if (stPct < -15)
                        {
                            stStatus = "Posibilă subdenivelare";
                            stColor = WpfColor.FromRgb(255, 180, 50);
                        }
                        else
                        {
                            stStatus = "Normal";
                            stColor = WpfColor.FromRgb(63, 185, 80);
                        }
                        STStatus.Text = stStatus;
                        STStatus.Foreground = new SolidColorBrush(stColor);
                    }
                }
            }
        }

        // Pan-Tompkins preprocessing (1985) — preprocesare standard pentru detecție R robustă.
        // Derivată în 5 puncte → pătrat → integrare pe fereastră mobilă (150ms).
        // Rezultatul e un envelope pozitiv cu vârfuri clare la complexele QRS,
        // independent de amplitudinea absolută a semnalului.
        private static List<double> PanTompkinsEnvelope(List<double> signal, double avgDt)
        {
            int n = signal.Count;

            // 1. Derivată în 5 puncte — accentuează pantele abrupte QRS
            var deriv = new double[n];
            for (int i = 2; i < n - 2; i++)
                deriv[i] =
                    (signal[i + 2] + 2 * signal[i + 1] - 2 * signal[i - 1] - signal[i - 2]) / 8.0;

            // 2. Ridicare la pătrat — toate valorile pozitive, vârfurile mari domină
            var squared = new double[n];
            for (int i = 0; i < n; i++)
                squared[i] = deriv[i] * deriv[i];

            // 3. Integrare pe fereastră mobilă ~150ms — netezire, formează envelope
            int winSize = Math.Max(2, (int)Math.Round(0.150 / avgDt));
            var result = new List<double>(n);
            double runSum = 0;
            var winQueue = new Queue<double>(winSize + 1);
            for (int i = 0; i < n; i++)
            {
                runSum += squared[i];
                winQueue.Enqueue(squared[i]);
                if (winQueue.Count > winSize)
                    runSum -= winQueue.Dequeue();
                result.Add(runSum / winQueue.Count);
            }
            return result;
        }

        private void SetRitm(string text, WpfColor textColor, string subtext, WpfColor bgColor)
        {
            RitmText.Text = text;
            RitmText.Foreground = new SolidColorBrush(textColor);
            RitmSubtext.Text = subtext;
            RitmBorder.Background = new SolidColorBrush(bgColor);
        }

        private int _qualityGoodTicks = 0;
        private int _qualityWeakTicks = 0;
        private int _qualityBadTicks = 0;
        private int _qualityNoneTicks = 0;

        private void UpdateSignalQuality(List<double> amps)
        {
            double maxAbs = amps.Select(Math.Abs).Max();
            if (maxAbs > SaturationLimit * 0.95)
            {
                _qualityBadTicks++;
                SetQuality(
                    "CONTACT PROST",
                    WpfColor.FromRgb(255, 107, 107),
                    "Semnal saturat",
                    WpfColor.FromRgb(255, 107, 107)
                );
            }
            else if (maxAbs > SaturationLimit * 0.80)
            {
                _qualityWeakTicks++;
                SetQuality(
                    "CONTACT SLAB",
                    WpfColor.FromRgb(255, 180, 50),
                    "Semnal instabil",
                    WpfColor.FromRgb(255, 180, 50)
                );
            }
            else if (maxAbs < 0.05)
            {
                _qualityNoneTicks++;
                SetQuality(
                    "FARA SEMNAL",
                    WpfColor.FromRgb(139, 148, 158),
                    "Verificați electrozii",
                    WpfColor.FromRgb(139, 148, 158)
                );
            }
            else
            {
                _qualityGoodTicks++;
                SetQuality(
                    "CONTACT BUN",
                    WpfColor.FromRgb(63, 185, 80),
                    "Electrozi conectați corect",
                    WpfColor.FromRgb(63, 185, 80)
                );
            }
        }

        private void SetQuality(string text, WpfColor textColor, string subtext, WpfColor dotColor)
        {
            QualityDot.Fill = new SolidColorBrush(dotColor);
            QualityText.Text = text;
            QualityText.Foreground = new SolidColorBrush(textColor);
            QualitySubtext.Text = subtext;
        }

        private void UpdateHRTrend(double bpm)
        {
            _hrTrend.Enqueue(bpm);
            if (_hrTrend.Count > HRTrendMaxPoints)
                _hrTrend.Dequeue();
            HRTrendCanvas.Children.Clear();
            if (_hrTrend.Count < 2)
                return;

            var values = _hrTrend.ToList();
            double minVal = Math.Max(30, values.Min() - 5),
                maxVal = Math.Min(200, values.Max() + 5);
            double range = maxVal - minVal;
            if (range < 10)
                range = 10;

            double canvasWidth = HRTrendCanvas.ActualWidth > 0 ? HRTrendCanvas.ActualWidth : 248;
            double canvasHeight = 40;
            double stepX = canvasWidth / (HRTrendMaxPoints - 1);

            for (int i = 0; i <= 2; i++)
                HRTrendCanvas.Children.Add(
                    new System.Windows.Shapes.Line
                    {
                        X1 = 0,
                        Y1 = canvasHeight * i / 2,
                        X2 = canvasWidth,
                        Y2 = canvasHeight * i / 2,
                        Stroke = new SolidColorBrush(WpfColor.FromArgb(30, 255, 255, 255)),
                        StrokeThickness = 0.5,
                    }
                );

            var fillPoints = new System.Windows.Media.PointCollection();
            fillPoints.Add(new System.Windows.Point((values.Count - 1) * stepX, canvasHeight));
            fillPoints.Add(new System.Windows.Point(0, canvasHeight));
            for (int i = 0; i < values.Count; i++)
                fillPoints.Insert(
                    fillPoints.Count - 1,
                    new System.Windows.Point(
                        i * stepX,
                        canvasHeight - ((values[i] - minVal) / range * canvasHeight * 0.85) - 2
                    )
                );
            HRTrendCanvas.Children.Add(
                new System.Windows.Shapes.Polygon
                {
                    Points = fillPoints,
                    Fill = new LinearGradientBrush(
                        WpfColor.FromArgb(60, 255, 107, 107),
                        WpfColor.FromArgb(5, 255, 107, 107),
                        new System.Windows.Point(0, 0),
                        new System.Windows.Point(0, 1)
                    ),
                }
            );

            for (int i = 1; i < values.Count; i++)
            {
                double y1 =
                    canvasHeight - ((values[i - 1] - minVal) / range * canvasHeight * 0.85) - 2;
                double y2 = canvasHeight - ((values[i] - minVal) / range * canvasHeight * 0.85) - 2;
                HRTrendCanvas.Children.Add(
                    new System.Windows.Shapes.Line
                    {
                        X1 = (i - 1) * stepX,
                        Y1 = y1,
                        X2 = i * stepX,
                        Y2 = y2,
                        Stroke = new SolidColorBrush(WpfColor.FromRgb(255, 107, 107)),
                        StrokeThickness = 1.5,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round,
                    }
                );
            }

            double lastY = canvasHeight - ((values[^1] - minVal) / range * canvasHeight * 0.85) - 2;
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = new SolidColorBrush(WpfColor.FromRgb(255, 107, 107)),
                Stroke = new SolidColorBrush(WpfColor.FromRgb(13, 17, 23)),
                StrokeThickness = 1.5,
            };
            Canvas.SetLeft(dot, (values.Count - 1) * stepX - 3);
            Canvas.SetTop(dot, lastY - 3);
            HRTrendCanvas.Children.Add(dot);

            var maxLbl = new TextBlock
            {
                Text = $"{(int)maxVal}",
                Foreground = new SolidColorBrush(WpfColor.FromArgb(150, 139, 148, 158)),
                FontSize = 9,
                FontFamily = new FontFamily("Segoe UI"),
            };
            Canvas.SetLeft(maxLbl, 2);
            Canvas.SetTop(maxLbl, 0);
            HRTrendCanvas.Children.Add(maxLbl);

            var minLbl = new TextBlock
            {
                Text = $"{(int)minVal}",
                Foreground = new SolidColorBrush(WpfColor.FromArgb(150, 139, 148, 158)),
                FontSize = 9,
                FontFamily = new FontFamily("Segoe UI"),
            };
            Canvas.SetLeft(minLbl, 2);
            Canvas.SetTop(minLbl, canvasHeight - 12);
            HRTrendCanvas.Children.Add(minLbl);

            var curLbl = new TextBlock
            {
                Text = $"{(int)values[^1]}",
                Foreground = new SolidColorBrush(WpfColor.FromRgb(255, 107, 107)),
                FontSize = 9,
                FontWeight = System.Windows.FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI"),
            };
            Canvas.SetLeft(curLbl, canvasWidth - 24);
            Canvas.SetTop(curLbl, canvasHeight / 2 - 6);
            HRTrendCanvas.Children.Add(curLbl);
        }

        private void PlayPauseBtn_Click(object sender, RoutedEventArgs e)
        {
            _isPlaying = !_isPlaying;
            PlayPauseBtn.Content = _isPlaying ? "⏸  PAUSE" : "▶  PLAY";
        }

        private void Speed_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && int.TryParse(btn.Tag?.ToString(), out int step))
                _stepPerTick = step;
        }

        private void Window_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && int.TryParse(btn.Tag?.ToString(), out int size))
                _windowSize = size;
        }

        private (double good, double weak, double bad, double none) BuildQualityStats()
        {
            int total =
                _qualityGoodTicks + _qualityWeakTicks + _qualityBadTicks + _qualityNoneTicks;
            if (total == 0)
                return (0, 0, 0, 0);

            return (
                _qualityGoodTicks * 100.0 / total,
                _qualityWeakTicks * 100.0 / total,
                _qualityBadTicks * 100.0 / total,
                _qualityNoneTicks * 100.0 / total
            );
        }

        private void ExportBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                QuestPDF.Settings.License = LicenseType.Community;
                string fileName =
                    $"Raport_ECG_{_patientName.Replace(" ", "_")}_{DateTime.Now:yyyyMMdd_HHmm}.pdf";
                string savePath = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    fileName
                );

                var quality = BuildQualityStats();

                var (events, avgHr, rhythmSummary) = EcgAnalyzer.DetectEvents(_times, _amplitudes);

                var session = new EcgSession
                {
                    PatientName = _patientName,
                    PatientAge = _patientAge,
                    PatientSex = _patientSex,
                    PatientId = _patientId,
                    DoctorName = _doctorName,
                    ClinicalNotes = _clinicalNotes,
                    RecordedAt = DateTime.Now,
                    DurationSeconds = _times.Count > 0 ? _times[^1] - _times[0] : 0,
                    Times = new List<double>(_times),
                    Amplitudes = new List<double>(_amplitudes),
                    Events = events,
                    AvgHeartRate = avgHr,
                    RhythmSummary = rhythmSummary,
                };
                SessionStore.Save(session);

                Document
                    .Create(container =>
                    {
                        container.Page(page =>
                        {
                            page.Size(PageSizes.A4);
                            page.Margin(40);
                            page.PageColor(QuestPDF.Helpers.Colors.White);
                            page.Header().Element(ComposeHeader);
                            page.Content().Element(c => ComposeContent(c, quality, events));
                            page.Footer()
                                .AlignCenter()
                                .Text(x =>
                                {
                                    x.Span("CardioMed — Raport ECG generat automat  |  ")
                                        .FontSize(9)
                                        .FontColor("#8B949E");
                                    x.CurrentPageNumber().FontSize(9).FontColor("#8B949E");
                                    x.Span(" / ").FontSize(9).FontColor("#8B949E");
                                    x.TotalPages().FontSize(9).FontColor("#8B949E");
                                });
                        });
                    })
                    .GeneratePdf(savePath);

                AppMessageBox.Show(
                    this,
                    $"Raport salvat pe Desktop: {fileName}\n\n"
                        + "Înregistrare completă salvată în Istoric (CardioMed → Sessions), "
                        + "disponibilă pentru vizualizare detaliată oricând.",
                    "Export reușit"
                );
            }
            catch (Exception ex)
            {
                AppMessageBox.ShowError(this, $"Eroare export: {ex.Message}");
            }
        }

        private void HistoryBtn_Click(object sender, RoutedEventArgs e)
        {
            var history = new HistoryWindow();
            history.Show();
        }

        private void ComposeHeader(IContainer container)
        {
            container.Column(col =>
            {
                col.Item()
                    .Row(row =>
                    {
                        row.RelativeItem()
                            .Column(c =>
                            {
                                c.Item()
                                    .Text("RAPORT ECG")
                                    .FontSize(22)
                                    .Bold()
                                    .FontColor("#1a1a2e");
                                c.Item()
                                    .Text("Sistem CardioMed de Monitorizare Cardiacă")
                                    .FontSize(11)
                                    .FontColor("#8B949E");
                            });
                        row.ConstantItem(160)
                            .Column(c =>
                            {
                                c.Item()
                                    .AlignRight()
                                    .Text($"Data: {DateTime.Now:dd/MM/yyyy}")
                                    .FontSize(10)
                                    .FontColor("#555");
                                c.Item()
                                    .AlignRight()
                                    .Text($"Ora: {DateTime.Now:HH:mm}")
                                    .FontSize(10)
                                    .FontColor("#555");
                            });
                    });
                col.Item().PaddingTop(8).LineHorizontal(1).LineColor("#E0E0E0");
            });
        }

        private void ComposeContent(
            IContainer container,
            (double good, double weak, double bad, double none) quality,
            List<EcgEvent> events
        )
        {
            container.Column(col =>
            {
                col.Item()
                    .PaddingTop(20)
                    .Text("DATE PACIENT")
                    .FontSize(12)
                    .Bold()
                    .FontColor("#58A6FF");
                bool qtcAlert = int.TryParse(QTcValue.Text, out int qtcVal) && qtcVal > 450;

                col.Item()
                    .PaddingTop(8)
                    .Table(table =>
                    {
                        table.ColumnsDefinition(c =>
                        {
                            c.RelativeColumn();
                            c.RelativeColumn();
                        });
                        void AddRow(string label, string value)
                        {
                            table
                                .Cell()
                                .Padding(6)
                                .Background("#F8F9FA")
                                .Text(label)
                                .FontSize(10)
                                .FontColor("#555")
                                .Bold();
                            table.Cell().Padding(6).Text(value).FontSize(10).FontColor("#1a1a2e");
                        }
                        AddRow("Nume complet:", _patientName);
                        AddRow("Vârstă:", $"{_patientAge} ani");
                        AddRow("Sex:", _patientSex);
                        AddRow("CNP / ID:", _patientId);
                        AddRow("Medic curant:", _doctorName);
                    });

                if (!string.IsNullOrEmpty(_clinicalNotes))
                {
                    col.Item()
                        .PaddingTop(8)
                        .Text("Observații clinice:")
                        .FontSize(10)
                        .Bold()
                        .FontColor("#555");
                    col.Item()
                        .PaddingTop(4)
                        .Background("#F8F9FA")
                        .Padding(8)
                        .Text(_clinicalNotes)
                        .FontSize(10)
                        .FontColor("#1a1a2e");
                }

                col.Item()
                    .PaddingTop(24)
                    .Text("CALITATE ÎNREGISTRARE")
                    .FontSize(12)
                    .Bold()
                    .FontColor("#58A6FF");

                col.Item()
                    .PaddingTop(8)
                    .Row(row =>
                    {
                        void Chip(string label, double pct, string color)
                        {
                            row.RelativeItem()
                                .Background("#F8F9FA")
                                .BorderLeft(3)
                                .BorderColor(color)
                                .Padding(10)
                                .Column(c =>
                                {
                                    c.Item()
                                        .Text($"{pct:F0}%")
                                        .FontSize(18)
                                        .Bold()
                                        .FontColor(color);
                                    c.Item().Text(label).FontSize(9).FontColor("#555");
                                });
                        }
                        Chip("Contact bun", quality.good, "#3FB950");
                        Chip("Contact slab", quality.weak, "#FFB432");
                        Chip("Contact prost", quality.bad, "#FF6B6B");
                        Chip("Fără semnal", quality.none, "#8B949E");
                    });

                col.Item()
                    .PaddingTop(24)
                    .Text("PARAMETRI ECG MĂSURAȚI")
                    .FontSize(12)
                    .Bold()
                    .FontColor("#58A6FF");
                col.Item()
                    .PaddingTop(8)
                    .Table(table =>
                    {
                        table.ColumnsDefinition(c =>
                        {
                            c.RelativeColumn(2);
                            c.RelativeColumn();
                            c.RelativeColumn();
                            c.RelativeColumn(2);
                        });
                        table
                            .Cell()
                            .Background("#1a1a2e")
                            .Padding(8)
                            .Text("Parametru")
                            .FontSize(10)
                            .Bold()
                            .FontColor("#FFFFFF");
                        table
                            .Cell()
                            .Background("#1a1a2e")
                            .Padding(8)
                            .Text("Valoare")
                            .FontSize(10)
                            .Bold()
                            .FontColor("#FFFFFF");
                        table
                            .Cell()
                            .Background("#1a1a2e")
                            .Padding(8)
                            .Text("Unitate")
                            .FontSize(10)
                            .Bold()
                            .FontColor("#FFFFFF");
                        table
                            .Cell()
                            .Background("#1a1a2e")
                            .Padding(8)
                            .Text("Interval Normal")
                            .FontSize(10)
                            .Bold()
                            .FontColor("#FFFFFF");

                        void AddParam(
                            string param,
                            string value,
                            string unit,
                            string normal,
                            bool alert = false
                        )
                        {
                            string bg = alert ? "#FFF3CD" : "#FFFFFF";
                            table.Cell().Background(bg).Padding(7).Text(param).FontSize(10);
                            table
                                .Cell()
                                .Background(bg)
                                .Padding(7)
                                .Text(value)
                                .FontSize(10)
                                .Bold()
                                .FontColor(alert ? "#856404" : "#1a1a2e");
                            table
                                .Cell()
                                .Background(bg)
                                .Padding(7)
                                .Text(unit)
                                .FontSize(10)
                                .FontColor("#555");
                            table
                                .Cell()
                                .Background(bg)
                                .Padding(7)
                                .Text(normal)
                                .FontSize(10)
                                .FontColor("#555");
                        }

                        AddParam("Frecvență cardiacă", HRValue.Text, "bpm", "60 – 100");
                        AddParam("Ritm cardiac", RitmText.Text, "—", "Sinusal normal");
                        AddParam("Interval PR", PRValue.Text, "ms", "120 – 200");
                        AddParam("Durata QRS", QRSValue.Text, "ms", "60 – 120");
                        AddParam(
                            "Interval QTc",
                            QTcValue.Text,
                            "ms",
                            "< 450 (M) / < 460 (F)",
                            qtcAlert
                        );
                        AddParam("HRV — SDNN", HRVValue.Text, "ms", "> 50 normal");
                        AddParam("Amplitudine R", RAmplValue.Text, "mV", "0.5 – 1.5");
                        AddParam("Extrasistole", ExtrasistoleValue.Text, "/fereastră", "0");
                        AddParam("Calitate semnal", QualityText.Text, "—", "Contact Bun");
                    });

                col.Item()
                    .PaddingTop(24)
                    .Text("INTERPRETARE AUTOMATĂ")
                    .FontSize(12)
                    .Bold()
                    .FontColor("#58A6FF");
                col.Item()
                    .PaddingTop(8)
                    .Background("#F0F7FF")
                    .Padding(12)
                    .Column(interp =>
                    {
                        var findings = new List<string>();
                        if (int.TryParse(HRValue.Text, out int hr))
                        {
                            if (hr > 100)
                                findings.Add(
                                    $"• Tahicardie ({hr} bpm) — frecvență cardiacă crescută."
                                );
                            else if (hr < 60)
                                findings.Add(
                                    $"• Bradicardie ({hr} bpm) — frecvență cardiacă scăzută."
                                );
                            else
                                findings.Add($"• Frecvență cardiacă normală ({hr} bpm).");
                        }
                        if (RitmText.Text == "NEREGULAT")
                            findings.Add("• Ritm cardiac neregulat detectat.");
                        if (qtcAlert)
                            findings.Add(
                                $"• QTc prelungit ({QTcValue.Text} ms) — consultați cardiolog."
                            );
                        if (int.TryParse(PRValue.Text, out int pr) && pr > 200)
                            findings.Add(
                                $"• Interval PR prelungit ({pr} ms) — posibil bloc AV grad I."
                            );
                        if (int.TryParse(HRVValue.Text, out int hrv) && hrv < 20)
                            findings.Add(
                                $"• HRV scăzut ({hrv} ms) — posibil stres sau disfuncție autonomă."
                            );
                        if (findings.Count == 0)
                            findings.Add("• Parametrii ECG în limite normale.");
                        findings.Add(
                            "\n⚠ Raport generat automat — nu substituie consultul medical."
                        );
                        foreach (var f in findings)
                            interp.Item().Text(f).FontSize(10).FontColor("#1a1a2e");
                    });

                col.Item()
                    .PaddingTop(24)
                    .Text("EVENIMENTE DETECTATE PE TOATĂ ÎNREGISTRAREA")
                    .FontSize(12)
                    .Bold()
                    .FontColor("#58A6FF");

                if (events.Count == 0)
                {
                    col.Item()
                        .PaddingTop(8)
                        .Text("Niciun eveniment anormal detectat pe durata înregistrării.")
                        .FontSize(10)
                        .FontColor("#1a1a2e");
                }
                else
                {
                    col.Item()
                        .PaddingTop(8)
                        .Column(evCol =>
                        {
                            foreach (var ev in events)
                            {
                                evCol
                                    .Item()
                                    .PaddingBottom(4)
                                    .Background("#FFF3CD")
                                    .Padding(8)
                                    .Text(
                                        $"• {ev.Type} — interval {ev.StartTime:F1}s – {ev.EndTime:F1}s   ({ev.Detail})"
                                    )
                                    .FontSize(10)
                                    .FontColor("#856404");
                            }
                        });
                }

                col.Item()
                    .PaddingTop(8)
                    .Text(
                        "Înregistrarea completă (traseu integral, navigabil cu zoom) este disponibilă "
                            + "în CardioMed → Istoric, pentru verificare detaliată a momentelor de mai sus."
                    )
                    .FontSize(9)
                    .Italic()
                    .FontColor("#8B949E");

                col.Item()
                    .PaddingTop(40)
                    .Row(row =>
                    {
                        row.RelativeItem()
                            .Column(sig =>
                            {
                                sig.Item().Text("Medic curant:").FontSize(10).FontColor("#555");
                                sig.Item().PaddingTop(30).LineHorizontal(1).LineColor("#555");
                                sig.Item()
                                    .PaddingTop(4)
                                    .Text(_doctorName)
                                    .FontSize(10)
                                    .FontColor("#1a1a2e");
                            });
                        row.ConstantItem(100);
                        row.RelativeItem()
                            .Column(sig =>
                            {
                                sig.Item().Text("Semnătură:").FontSize(10).FontColor("#555");
                                sig.Item().PaddingTop(30).LineHorizontal(1).LineColor("#555");
                            });
                    });
            });
        }
    }
}
