using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using InTheHand.Bluetooth;
using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Series;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using WpfColor = System.Windows.Media.Color;

namespace ECGMonitor
{
    public partial class MainWindow : Window
    {
        private List<double> _times = new();
        private List<double> _amplitudes = new();
        private PlotModel _plotModel = new();
        private LineSeries _ecgSeries = new();
        private LinearAxis _xAxis = new();
        private LinearAxis _yAxis = new();
        private DispatcherTimer _timer = new();
        private DispatcherTimer _clockTimer = new();
        private DispatcherTimer _electrodeTimer = new();
        private bool _electrodeAlertShown = false;
        private int _currentIndex = 0;
        private int _windowSize = 1280;
        private int _stepPerTick = 4;
        private bool _isPlaying = true;
        private const double SaturationLimit = 165.0;

        private BluetoothDevice? _bleDevice;
        private GattCharacteristic? _bleCharacteristic;
        private bool _bleMode = false;
        private double _bleTime = 0;

        // Date pacient
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

        // Pentru tahicardie sustinuta
        private int _tachycardiaFrames = 0;
        private const int TachycardiaThreshold = 33; // ~30s la 30fps

        // Trend HR
        private Queue<double> _hrTrend = new();
        private const int HRTrendMaxPoints = 60;
        private int _hrTrendTickCounter = 0;

        public MainWindow()
        {
            InitializeComponent();

            // Splash screen cu preloading real
            var splash = new SplashScreen();
            splash.ShowDialog();

            // Folosim datele deja incarcate
            if (splash.PreloadedTimes.Count > 0)
            {
                _times = splash.PreloadedTimes;
                _amplitudes = splash.PreloadedAmplitudes;
                FooterSource.Text = $"Source: ekg_signal2.txt  |  {_times.Count} samples";
            }

            // Calibrare electrozi
            var calibration = new CalibrationWindow();
            if (calibration.ShowDialog() != true)
            {
                Application.Current.Shutdown();
                return;
            }

            // Date pacient
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

            LoadECGData();
            SetupPlot();
            StartTimers();
        }

        private void LoadECGData()
        {
            if (_times.Count > 0)
                return;
            string[] possiblePaths =
            {
                "ekg_signal2.txt",
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ekg_signal2.txt"),
                Path.Combine(Directory.GetCurrentDirectory(), "ekg_signal2.txt"),
            };

            string? filePath = possiblePaths.FirstOrDefault(File.Exists);

            if (filePath == null)
            {
                GenerateDemoData();
                return;
            }

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
                    _times.Add(t);
                    _amplitudes.Add(amp * 1000.0);
                }
            }

            int startIndex = 0;
            for (int i = 0; i < _times.Count; i++)
            {
                if (_times[i] >= 35.0)
                {
                    startIndex = i;
                    break;
                }
            }
            _times = _times.Skip(startIndex).ToList();
            _amplitudes = _amplitudes.Skip(startIndex).ToList();

            FooterSource.Text = $"Source: {Path.GetFileName(filePath)}  |  {_times.Count} samples";
        }

        private async Task ConnectBLE()
        {
            try
            {
                var device = await Bluetooth.RequestDeviceAsync(
                    new RequestDeviceOptions
                    {
                        options.Filters.Add(new BluetoothLEScanFilter { Name = "CardioMed_ECG" });
                        {
                            new BluetoothLEScanFilter { Name = "CardioMed_ECG" },
                        },
                    }
                );

                if (device == null)
                    return;

                _bleDevice = device;
                await device.Gatt.ConnectAsync();

                var service = await device.Gatt.GetPrimaryServiceAsync(
                    BluetoothUuid.FromGuid(new Guid("6E400001-B5A3-F393-E0A9-E50E24DCCA9E"))
                );

                _bleCharacteristic = await service.GetCharacteristicAsync(
                    BluetoothUuid.FromGuid(new Guid("6E400003-B5A3-F393-E0A9-E50E24DCCA9E"))
                );

                _bleCharacteristic.CharacteristicValueChanged += (s, e) =>
                {
                    var bytes = e.Value;
                    var str = System.Text.Encoding.UTF8.GetString(bytes);
                    if (
                        double.TryParse(
                            str,
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out double amp
                        )
                    )
                    {
                        Dispatcher.Invoke(() =>
                        {
                            _bleTime += 0.002;
                            _times.Add(_bleTime);
                            _amplitudes.Add(amp);
                            _bleMode = true;
                            FooterSource.Text = "Source: BLE — CardioMed_ECG";
                        });
                    }
                };

                await _bleCharacteristic.StartNotificationsAsync();

                Dispatcher.Invoke(() =>
                {
                    FooterSource.Text = "BLE conectat — CardioMed_ECG";
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Eroare BLE: {ex.Message}");
            }
        }

        private void GenerateDemoData()
        {
            for (int i = 0; i < 10000; i++)
            {
                double t = i * 0.007812;
                double val =
                    0.05 * Math.Sin(2 * Math.PI * 1.2 * t)
                    + 0.8 * Math.Exp(-Math.Pow((t % 0.833 - 0.1) / 0.015, 2))
                    - 0.1 * Math.Exp(-Math.Pow((t % 0.833 - 0.08) / 0.01, 2))
                    + 0.15 * Math.Exp(-Math.Pow((t % 0.833 - 0.35) / 0.04, 2));
                _times.Add(t);
                _amplitudes.Add(val * 1000.0);
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

            // Markere mai subtile - Cross pentru R, cerc mic pentru P/T, patrat mic pentru Q/S
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
                Margin = new Thickness(0),
            };

            var stack = new StackPanel { Margin = new Thickness(28) };

            var icon = new TextBlock
            {
                Text = "⚠",
                Foreground = new SolidColorBrush(WpfColor.FromRgb(255, 107, 107)),
                FontSize = 28,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 10),
            };

            var title = new TextBlock
            {
                Text = "Contact Electrod Deficitar",
                Foreground = new SolidColorBrush(WpfColor.FromRgb(230, 237, 243)),
                FontSize = 16,
                FontWeight = System.Windows.FontWeights.Bold,
                FontFamily = new FontFamily("Segoe UI"),
                Margin = new Thickness(0, 0, 0, 8),
            };

            var msg = new TextBlock
            {
                Text =
                    "Semnalul ECG depășește limita INA (±165mV).\nReatasați corect electrozii și verificați contactul.",
                Foreground = new SolidColorBrush(WpfColor.FromRgb(139, 148, 158)),
                FontSize = 12,
                FontFamily = new FontFamily("Segoe UI"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 20),
            };

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

            stack.Children.Add(icon);
            stack.Children.Add(title);
            stack.Children.Add(msg);
            stack.Children.Add(btn);
            border.Child = stack;
            popup.Content = border;
            popup.ShowDialog();
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (!_isPlaying || _amplitudes.Count == 0)
                return;

            for (int i = 0; i < _stepPerTick; i++)
            {
                int idx = _currentIndex % _amplitudes.Count;
                _ecgSeries.Points.Add(new DataPoint(_times[idx], _amplitudes[idx]));
                _currentIndex++;
            }

            if (_ecgSeries.Points.Count > _windowSize)
                _ecgSeries.Points.RemoveRange(0, _ecgSeries.Points.Count - _windowSize);

            if (_ecgSeries.Points.Count > 0)
            {
                _xAxis.Minimum = _ecgSeries.Points[0].X;
                _xAxis.Maximum = _ecgSeries.Points[^1].X;

                var amps = _ecgSeries.Points.Select(p => p.Y).ToList();
                double margin = (amps.Max() - amps.Min()) * 0.25;
                _yAxis.Minimum = amps.Min() - margin;
                _yAxis.Maximum = amps.Max() + margin;

                double currentAmp = _ecgSeries.Points[^1].Y;
                AmpValue.Text = $"{currentAmp:F2} mV";
                FooterTime.Text = $"t = {_ecgSeries.Points[^1].X:F3} s";

                DetectPQRST();
                UpdateSignalQuality(amps);
            }

            _plotModel.InvalidatePlot(true);
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
            double maxAmp = amps.Max();
            double minAmp = amps.Min();
            double range = maxAmp - minAmp;
            double rThreshold = minAmp + range * 0.60;
            int minRDistance = 40;

            List<int> rIndices = new();
            for (int i = 5; i < n - 5; i++)
            {
                if (
                    amps[i] > rThreshold
                    && amps[i] > amps[i - 1]
                    && amps[i] > amps[i - 2]
                    && amps[i] > amps[i + 1]
                    && amps[i] > amps[i + 2]
                )
                {
                    if (rIndices.Count == 0 || i - rIndices[^1] > minRDistance)
                        rIndices.Add(i);
                }
            }

            List<double> pTimes = new();
            List<double> qTimes = new();
            List<double> sTimes = new();
            List<double> tTimes = new();

            foreach (int ri in rIndices)
            {
                _rPeaks.Points.Add(new ScatterPoint(points[ri].X, points[ri].Y));
                _rAmplitudes.Add(points[ri].Y);

                // Q
                int qStart = Math.Max(0, ri - 15);
                int qEnd = Math.Max(0, ri - 2);
                if (qStart < qEnd)
                {
                    int qIdx = qStart;
                    for (int j = qStart; j <= qEnd; j++)
                        if (amps[j] < amps[qIdx])
                            qIdx = j;
                    _qPoints.Points.Add(new ScatterPoint(points[qIdx].X, points[qIdx].Y));
                    qTimes.Add(points[qIdx].X);
                }

                // S
                int sStart = Math.Min(n - 1, ri + 2);
                int sEnd = Math.Min(n - 1, ri + 15);
                if (sStart < sEnd)
                {
                    int sIdx = sStart;
                    for (int j = sStart; j <= sEnd; j++)
                        if (amps[j] < amps[sIdx])
                            sIdx = j;
                    _sPoints.Points.Add(new ScatterPoint(points[sIdx].X, points[sIdx].Y));
                    sTimes.Add(points[sIdx].X);
                }

                // P
                int pStart = Math.Max(0, ri - 40);
                int pEnd = Math.Max(0, ri - 18);
                if (pStart < pEnd)
                {
                    int pIdx = pStart;
                    for (int j = pStart; j <= pEnd; j++)
                        if (amps[j] > amps[pIdx])
                            pIdx = j;
                    if (amps[pIdx] > minAmp + range * 0.05)
                    {
                        _pPeaks.Points.Add(new ScatterPoint(points[pIdx].X, points[pIdx].Y));
                        pTimes.Add(points[pIdx].X);
                    }
                }

                // T
                int tStart = Math.Min(n - 1, ri + 18);
                int tEnd = Math.Min(n - 1, ri + 55);
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
            }

            // HR si RR
            if (rIndices.Count >= 2)
            {
                var rTimes = rIndices.Select(i => points[i].X).ToList();
                _rrIntervals.Clear();
                for (int i = 1; i < rTimes.Count; i++)
                    _rrIntervals.Add(rTimes[i] - rTimes[i - 1]);

                double avgRR = _rrIntervals.Average();
                double bpm = 60.0 / avgRR;

                if (bpm > 30 && bpm < 220)
                {
                    HRValue.Text = ((int)bpm).ToString();
                    try
                    {
                        HRBar.Value = Math.Min(180, bpm);
                        _hrTrendTickCounter++;
                        if (_hrTrendTickCounter >= 33) // ~1 secunda la 30fps
                        {
                            _hrTrendTickCounter = 0;
                            UpdateHRTrend(bpm);
                        }
                    }
                    catch { }

                    // Tahicardie sustinuta
                    if (bpm > 110)
                        _tachycardiaFrames++;
                    else
                        _tachycardiaFrames = Math.Max(0, _tachycardiaFrames - 1);

                    double rrStd = Math.Sqrt(
                        _rrIntervals.Select(r => Math.Pow(r - avgRR, 2)).Average()
                    );

                    if (rrStd > 0.15)
                    {
                        SetRitm(
                            "NEREGULAT",
                            WpfColor.FromRgb(255, 107, 107),
                            "Variabilitate R-R ridicata",
                            WpfColor.FromArgb(40, 255, 107, 107)
                        );
                    }
                    else if (bpm > 110 && _tachycardiaFrames > TachycardiaThreshold)
                    {
                        SetRitm(
                            "TAHICARDIE",
                            WpfColor.FromRgb(255, 180, 50),
                            $"Puls rapid sustinut: {(int)bpm} bpm",
                            WpfColor.FromArgb(40, 255, 180, 50)
                        );
                    }
                    else if (bpm < 50)
                    {
                        SetRitm(
                            "BRADICARDIE",
                            WpfColor.FromRgb(80, 180, 255),
                            $"Puls lent: {(int)bpm} bpm",
                            WpfColor.FromArgb(40, 80, 180, 255)
                        );
                    }
                    else
                    {
                        SetRitm(
                            "NORMAL",
                            WpfColor.FromRgb(63, 185, 80),
                            "Ritm sinusal regulat",
                            WpfColor.FromArgb(255, 15, 42, 26)
                        );
                    }

                    // HRV - SDNN in ms
                    double sdnn =
                        Math.Sqrt(_rrIntervals.Select(r => Math.Pow(r - avgRR, 2)).Average())
                        * 1000.0;
                    HRVValue.Text = ((int)sdnn).ToString();
                    string hrvStatus =
                        sdnn > 50 ? "Bun"
                        : sdnn > 20 ? "Moderat"
                        : "Scazut";
                    HRVStatus.Text = hrvStatus;
                    HRVStatus.Foreground =
                        sdnn > 50 ? new SolidColorBrush(WpfColor.FromRgb(63, 185, 80))
                        : sdnn > 20 ? new SolidColorBrush(WpfColor.FromRgb(255, 180, 50))
                        : new SolidColorBrush(WpfColor.FromRgb(255, 107, 107));

                    // Amplitudine R medie
                    if (_rAmplitudes.Count > 0)
                    {
                        double avgR = _rAmplitudes.Average();
                        RAmplValue.Text = avgR.ToString("F2");
                        string rStatus =
                            avgR > 1.5 ? "Posibila hipertrofie"
                            : avgR > 0.5 ? "Normal"
                            : "Amplitudine scazuta";
                        RAmplStatus.Text = rStatus;
                        RAmplStatus.Foreground =
                            avgR > 1.5
                                ? new SolidColorBrush(WpfColor.FromRgb(255, 180, 50))
                                : new SolidColorBrush(WpfColor.FromRgb(63, 185, 80));
                    }

                    // Extrasistole - RR cu >20% mai scurt decat media
                    int extrasistole = _rrIntervals.Count(rr => rr < avgRR * 0.80);
                    ExtrasistoleValue.Text = extrasistole.ToString();
                    ExtrasistoleValue.Foreground =
                        extrasistole > 0
                            ? new SolidColorBrush(WpfColor.FromRgb(255, 107, 107))
                            : new SolidColorBrush(WpfColor.FromRgb(210, 168, 255));
                }

                // PR — filtrat: doar valori intre 80-320ms
                if (pTimes.Count > 0 && qTimes.Count > 0)
                {
                    var validPR = new List<double>();
                    int pairs = Math.Min(pTimes.Count, qTimes.Count);
                    for (int i = 0; i < pairs; i++)
                    {
                        double pr = (qTimes[i] - pTimes[i]) * 1000.0;
                        if (pr >= 80 && pr <= 320)
                            validPR.Add(pr);
                    }
                    if (validPR.Count > 0)
                        PRValue.Text = ((int)validPR.Average()).ToString();
                }

                // QRS
                if (qTimes.Count > 0 && sTimes.Count > 0)
                {
                    var validQRS = new List<double>();
                    int pairs = Math.Min(qTimes.Count, sTimes.Count);
                    for (int i = 0; i < pairs; i++)
                    {
                        double qrs = (sTimes[i] - qTimes[i]) * 1000.0;
                        if (qrs >= 40 && qrs <= 200)
                            validQRS.Add(qrs);
                    }
                    if (validQRS.Count > 0)
                        QRSValue.Text = ((int)validQRS.Average()).ToString();

                    // QTc
                    if (tTimes.Count > 0)
                    {
                        var validQT = new List<double>();
                        int tPairs = Math.Min(qTimes.Count, tTimes.Count);
                        for (int i = 0; i < tPairs; i++)
                        {
                            double qt = (tTimes[i] - qTimes[i]) * 1000.0;
                            if (qt >= 200 && qt <= 600)
                                validQT.Add(qt);
                        }
                        if (validQT.Count > 0)
                        {
                            double avgQT = validQT.Average();
                            double qtc = avgQT / Math.Sqrt(avgRR);
                            QTcValue.Text = ((int)qtc).ToString();
                            QTcValue.Foreground =
                                qtc > 450
                                    ? new SolidColorBrush(WpfColor.FromRgb(255, 107, 107))
                                    : new SolidColorBrush(WpfColor.FromRgb(230, 237, 243));
                        }
                    }
                }
            }
        }

        private void SetRitm(string text, WpfColor textColor, string subtext, WpfColor bgColor)
        {
            RitmText.Text = text;
            RitmText.Foreground = new SolidColorBrush(textColor);
            RitmSubtext.Text = subtext;
            RitmBorder.Background = new SolidColorBrush(bgColor);
        }

        private void UpdateSignalQuality(List<double> amps)
        {
            double maxAbs = amps.Select(Math.Abs).Max();
            double pct = maxAbs / SaturationLimit * 100.0;

            if (maxAbs > SaturationLimit * 0.95)
            {
                SetQuality(
                    "CONTACT PROST",
                    WpfColor.FromRgb(255, 107, 107),
                    $"INA saturat — date invalide",
                    WpfColor.FromRgb(255, 107, 107)
                );
            }
            else if (maxAbs > SaturationLimit * 0.80)
            {
                SetQuality(
                    "CONTACT SLAB",
                    WpfColor.FromRgb(255, 180, 50),
                    $"Semnal la {(int)pct}% din limita",
                    WpfColor.FromRgb(255, 180, 50)
                );
            }
            else if (maxAbs < 0.05)
            {
                SetQuality(
                    "FARA SEMNAL",
                    WpfColor.FromRgb(139, 148, 158),
                    "Verificati electrozii",
                    WpfColor.FromRgb(139, 148, 158)
                );
            }
            else
            {
                SetQuality(
                    "CONTACT BUN",
                    WpfColor.FromRgb(63, 185, 80),
                    $"Semnal la {(int)pct}% din limita INA",
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
            double minVal = Math.Max(30, values.Min() - 5);
            double maxVal = Math.Min(200, values.Max() + 5);
            double range = maxVal - minVal;
            if (range < 10)
                range = 10;

            double canvasWidth = HRTrendCanvas.ActualWidth > 0 ? HRTrendCanvas.ActualWidth : 248;
            double canvasHeight = 40;
            double stepX = canvasWidth / (HRTrendMaxPoints - 1);

            // Linii grila
            for (int i = 0; i <= 2; i++)
            {
                double y = canvasHeight * i / 2;
                var gridLine = new System.Windows.Shapes.Line
                {
                    X1 = 0,
                    Y1 = y,
                    X2 = canvasWidth,
                    Y2 = y,
                    Stroke = new SolidColorBrush(WpfColor.FromArgb(30, 255, 255, 255)),
                    StrokeThickness = 0.5,
                };
                HRTrendCanvas.Children.Add(gridLine);
            }

            // Fill
            var fillPoints = new System.Windows.Media.PointCollection();
            fillPoints.Add(new System.Windows.Point((values.Count - 1) * stepX, canvasHeight));
            fillPoints.Add(new System.Windows.Point(0, canvasHeight));
            for (int i = 0; i < values.Count; i++)
            {
                double x = i * stepX;
                double y = canvasHeight - ((values[i] - minVal) / range * canvasHeight * 0.85) - 2;
                fillPoints.Insert(fillPoints.Count - 1, new System.Windows.Point(x, y));
            }
            var fill = new System.Windows.Shapes.Polygon
            {
                Points = fillPoints,
                Fill = new LinearGradientBrush(
                    WpfColor.FromArgb(60, 255, 107, 107),
                    WpfColor.FromArgb(5, 255, 107, 107),
                    new System.Windows.Point(0, 0),
                    new System.Windows.Point(0, 1)
                ),
            };
            HRTrendCanvas.Children.Add(fill);

            // Linie principala
            for (int i = 1; i < values.Count; i++)
            {
                double x1 = (i - 1) * stepX;
                double y1 =
                    canvasHeight - ((values[i - 1] - minVal) / range * canvasHeight * 0.85) - 2;
                double x2 = i * stepX;
                double y2 = canvasHeight - ((values[i] - minVal) / range * canvasHeight * 0.85) - 2;
                var line = new System.Windows.Shapes.Line
                {
                    X1 = x1,
                    Y1 = y1,
                    X2 = x2,
                    Y2 = y2,
                    Stroke = new SolidColorBrush(WpfColor.FromRgb(255, 107, 107)),
                    StrokeThickness = 1.5,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                };
                HRTrendCanvas.Children.Add(line);
            }

            // Punct curent
            if (values.Count > 0)
            {
                double lastX = (values.Count - 1) * stepX;
                double lastY =
                    canvasHeight - ((values[^1] - minVal) / range * canvasHeight * 0.85) - 2;
                var dot = new System.Windows.Shapes.Ellipse
                {
                    Width = 6,
                    Height = 6,
                    Fill = new SolidColorBrush(WpfColor.FromRgb(255, 107, 107)),
                    Stroke = new SolidColorBrush(WpfColor.FromRgb(13, 17, 23)),
                    StrokeThickness = 1.5,
                };
                Canvas.SetLeft(dot, lastX - 3);
                Canvas.SetTop(dot, lastY - 3);
                HRTrendCanvas.Children.Add(dot);
            }

            // Valoare maxima (sus stanga)
            var maxLabel = new TextBlock
            {
                Text = $"{(int)maxVal}",
                Foreground = new SolidColorBrush(WpfColor.FromArgb(150, 139, 148, 158)),
                FontSize = 9,
                FontFamily = new FontFamily("Segoe UI"),
            };
            Canvas.SetLeft(maxLabel, 2);
            Canvas.SetTop(maxLabel, 0);
            HRTrendCanvas.Children.Add(maxLabel);

            // Valoare minima (jos stanga)
            var minLabel = new TextBlock
            {
                Text = $"{(int)minVal}",
                Foreground = new SolidColorBrush(WpfColor.FromArgb(150, 139, 148, 158)),
                FontSize = 9,
                FontFamily = new FontFamily("Segoe UI"),
            };
            Canvas.SetLeft(minLabel, 2);
            Canvas.SetTop(minLabel, canvasHeight - 12);
            HRTrendCanvas.Children.Add(minLabel);

            // Valoare curenta (dreapta)
            var currentLabel = new TextBlock
            {
                Text = $"{(int)values[^1]}",
                Foreground = new SolidColorBrush(WpfColor.FromRgb(255, 107, 107)),
                FontSize = 9,
                FontWeight = System.Windows.FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI"),
            };
            Canvas.SetLeft(currentLabel, canvasWidth - 24);
            Canvas.SetTop(currentLabel, canvasHeight / 2 - 6);
            HRTrendCanvas.Children.Add(currentLabel);

            // Eticheta timp stanga
            var timeLabel = new TextBlock
            {
                Text = "60s",
                Foreground = new SolidColorBrush(WpfColor.FromArgb(80, 139, 148, 158)),
                FontSize = 8,
                FontFamily = new FontFamily("Segoe UI"),
            };
            Canvas.SetLeft(timeLabel, 2);
            Canvas.SetTop(timeLabel, canvasHeight / 2 - 4);
            HRTrendCanvas.Children.Add(timeLabel);
        }

        private void PlayPauseBtn_Click(object sender, RoutedEventArgs e)
        {
            _isPlaying = !_isPlaying;
            if (_isPlaying)
            {
                PlayPauseBtn.Content = "⏸  PAUSE";
                StatusDot.Fill = new SolidColorBrush(WpfColor.FromRgb(63, 185, 80));
                StatusText.Text = "LIVE";
                StatusText.Foreground = new SolidColorBrush(WpfColor.FromRgb(63, 185, 80));
            }
            else
            {
                PlayPauseBtn.Content = "▶  PLAY";
                StatusDot.Fill = new SolidColorBrush(WpfColor.FromRgb(139, 148, 158));
                StatusText.Text = "PAUSED";
                StatusText.Foreground = new SolidColorBrush(WpfColor.FromRgb(139, 148, 158));
            }
        }

        private void ResetBtn_Click(object sender, RoutedEventArgs e)
        {
            _currentIndex = 0;
            _ecgSeries.Points.Clear();
            _rPeaks.Points.Clear();
            _pPeaks.Points.Clear();
            _qPoints.Points.Clear();
            _sPoints.Points.Clear();
            _tPeaks.Points.Clear();
            _tachycardiaFrames = 0;
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

        private void ExportBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

                string fileName =
                    $"Raport_ECG_{_patientName.Replace(" ", "_")}_{DateTime.Now:yyyyMMdd_HHmm}.pdf";
                string savePath = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    fileName
                );

                QuestPDF
                    .Fluent.Document.Create(container =>
                    {
                        container.Page(page =>
                        {
                            page.Size(QuestPDF.Helpers.PageSizes.A4);
                            page.Margin(40);
                            page.PageColor(QuestPDF.Helpers.Colors.White);

                            page.Header().Element(ComposeHeader);
                            page.Content().Element(ComposeContent);
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

                MessageBox.Show(
                    $"Raport salvat pe Desktop:\n{fileName}",
                    "Export Reușit",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information
                );
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Eroare export: {ex.Message}",
                    "Eroare",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
        }

        private void ComposeHeader(QuestPDF.Infrastructure.IContainer container)
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

        private void ComposeContent(QuestPDF.Infrastructure.IContainer container)
        {
            container.Column(col =>
            {
                // Date pacient
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

                // Parametri ECG
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

                        // Header
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

                // Interpretare automata
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
                            findings.Add(
                                "• Ritm cardiac neregulat detectat — variabilitate R-R crescută."
                            );

                        if (qtcAlert)
                            findings.Add(
                                $"• QTc prelungit ({QTcValue.Text} ms) — risc potențial de aritmie. Consultați cardiolog."
                            );

                        if (int.TryParse(PRValue.Text, out int pr))
                        {
                            if (pr > 200)
                                findings.Add(
                                    $"• Interval PR prelungit ({pr} ms) — posibil bloc AV grad I."
                                );
                            else if (pr < 120)
                                findings.Add($"• Interval PR scurt ({pr} ms).");
                        }

                        if (int.TryParse(HRVValue.Text, out int hrv) && hrv < 20)
                            findings.Add(
                                $"• HRV scăzut ({hrv} ms) — poate indica stres sau disfuncție autonomă."
                            );

                        if (findings.Count == 0)
                            findings.Add(
                                "• Parametrii ECG în limite normale. Nu s-au detectat anomalii semnificative."
                            );

                        findings.Add(
                            "\n⚠ Acest raport este generat automat și nu substituie consultul medical de specialitate."
                        );

                        foreach (var f in findings)
                            interp.Item().Text(f).FontSize(10).FontColor("#1a1a2e");
                    });

                // Semnatura
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
