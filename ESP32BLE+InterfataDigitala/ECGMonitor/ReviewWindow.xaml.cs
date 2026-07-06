using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Series;

namespace ECGMonitor
{
    public class EventListItem
    {
        public EcgEvent Event { get; set; } = new();
        public string TypeAndTime =>
            $"{Event.Type}  ({Event.StartTime:F1}s – {Event.EndTime:F1}s)";
        public string Detail => Event.Detail;
        public string ColorHex => EventTypeStyle.HexFor(Event.Type);
    }

    public class EventTypeFilter
    {
        public string Type { get; set; } = "";
        public string Label { get; set; } = "";
        public bool IsChecked { get; set; } = true;
        public string ColorHex => EventTypeStyle.HexFor(Type);
    }

    public static class EventTypeStyle
    {
        private static readonly Dictionary<string, (string Hex, OxyColor Oxy)> Styles = new()
        {
            ["Tahicardie"] = ("#FFB432", OxyColor.FromRgb(255, 180, 50)),
            ["Bradicardie"] = ("#50C8FF", OxyColor.FromRgb(80, 200, 255)),
            ["Extrasistolă"] = ("#D2A8FF", OxyColor.FromRgb(210, 168, 255)),
            ["Ritm neregulat"] = ("#FF6B6B", OxyColor.FromRgb(255, 107, 107)),
            ["Pauză"] = ("#79C0FF", OxyColor.FromRgb(121, 192, 255)),
        };

        private static readonly (string Hex, OxyColor Oxy) Fallback = (
            "#8B949E",
            OxyColor.FromRgb(139, 148, 158)
        );

        public static string HexFor(string type) =>
            Styles.TryGetValue(type, out var s) ? s.Hex : Fallback.Hex;

        public static OxyColor OxyFor(string type) =>
            Styles.TryGetValue(type, out var s) ? s.Oxy : Fallback.Oxy;
    }

    public partial class ReviewWindow : Window
    {
        private readonly EcgSession _session;
        private readonly PlotModel _plotModel = new();
        private readonly LineSeries _series = new();
        private LinearAxis _xAxis = new();
        private LinearAxis _yAxis = new();
        private List<EventTypeFilter> _typeFilters = new();
        private double _annotationYMin;
        private double _annotationYMax;

        public ReviewWindow(EcgSession session)
        {
            InitializeComponent();
            WindowMaximizeFix.Apply(this);
            Window_StateChanged(this, EventArgs.Empty);
            _session = session;
            HeaderText.Text =
                $"{session.PatientName}  —  {session.RecordedAt:dd.MM.yyyy HH:mm}  —  durată {(int)session.DurationSeconds}s  —  puls mediu {session.AvgHeartRate} bpm";

            SetupPlot();
            LoadData();
            LoadEvents();
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
                TitleColor = OxyColor.FromArgb(255, 139, 148, 158),
                TextColor = OxyColor.FromArgb(255, 139, 148, 158),
                MajorGridlineStyle = LineStyle.Solid,
                MajorGridlineColor = OxyColor.FromArgb(35, 139, 0, 0),
                AxislineColor = OxyColor.FromArgb(80, 48, 54, 61),
                IsZoomEnabled = true,
                IsPanEnabled = true,
            };

            _yAxis = new LinearAxis
            {
                Position = AxisPosition.Left,
                Title = "Amplitude (mV)",
                TitleColor = OxyColor.FromArgb(255, 139, 148, 158),
                TextColor = OxyColor.FromArgb(255, 139, 148, 158),
                MajorGridlineStyle = LineStyle.Solid,
                MajorGridlineColor = OxyColor.FromArgb(35, 139, 0, 0),
                AxislineColor = OxyColor.FromArgb(80, 48, 54, 61),
                IsZoomEnabled = true,
                IsPanEnabled = true,
            };

            _series.Color = OxyColor.FromArgb(255, 0, 210, 130);
            _series.StrokeThickness = 1.0;

            _plotModel.Axes.Add(_xAxis);
            _plotModel.Axes.Add(_yAxis);
            _plotModel.Series.Add(_series);
            PlotView.Model = _plotModel;
        }

        private void LoadData()
        {
            for (int i = 0; i < _session.Times.Count; i++)
                _series.Points.Add(new DataPoint(_session.Times[i], _session.Amplitudes[i]));
            _plotModel.InvalidatePlot(true);
        }

        private void LoadEvents()
        {
            if (_session.Amplitudes.Count > 0)
            {
                // RectangleAnnotation cu limite infinite pe Y produce NullReferenceException in OxyPlot
                // (GetClippingRect) - folosim limite finite, suficient de largi cat sa acopere tot semnalul.
                _annotationYMin = _session.Amplitudes.Min();
                _annotationYMax = _session.Amplitudes.Max();
            }

            _typeFilters = _session
                .Events.Select(ev => ev.Type)
                .Distinct()
                .Select(t => new EventTypeFilter { Type = t, Label = t, IsChecked = true })
                .ToList();
            FilterList.ItemsSource = _typeFilters;

            ApplyFilter();
        }

        private void Filter_Changed(object sender, RoutedEventArgs e)
        {
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            var checkedTypes = _typeFilters.Where(f => f.IsChecked).Select(f => f.Type).ToHashSet();

            var filtered = _session.Events.Where(ev => checkedTypes.Contains(ev.Type)).ToList();

            EventsList.ItemsSource = filtered.Select(ev => new EventListItem { Event = ev }).ToList();

            _plotModel.Annotations.Clear();
            double margin = Math.Max(0.1, (_annotationYMax - _annotationYMin) * 0.5);
            foreach (var ev in filtered)
            {
                var color = EventTypeStyle.OxyFor(ev.Type);
                _plotModel.Annotations.Add(
                    new RectangleAnnotation
                    {
                        MinimumX = ev.StartTime,
                        MaximumX = ev.EndTime,
                        MinimumY = _annotationYMin - margin,
                        MaximumY = _annotationYMax + margin,
                        Fill = OxyColor.FromArgb(50, color.R, color.G, color.B),
                        StrokeThickness = 0,
                    }
                );
            }

            _plotModel.InvalidatePlot(true);
        }

        private void EventsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (EventsList.SelectedItem is EventListItem item)
            {
                double margin = Math.Max(2.0, (item.Event.EndTime - item.Event.StartTime) * 0.5);
                _xAxis.Zoom(item.Event.StartTime - margin, item.Event.EndTime + margin);
                _plotModel.InvalidatePlot(false);
            }
        }

        private void ShowAllBtn_Click(object sender, RoutedEventArgs e)
        {
            _xAxis.Reset();
            _yAxis.Reset();
            _plotModel.InvalidatePlot(false);
        }

        private void MinimizeBtn_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void MaximizeBtn_Click(object sender, RoutedEventArgs e)
        {
            WindowState =
                WindowState == WindowState.Maximized
                    ? WindowState.Normal
                    : WindowState.Maximized;
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Window_StateChanged(object sender, EventArgs e)
        {
            MaximizeBtn.Content = WindowState == WindowState.Maximized ? "❐" : "▢";
        }
    }
}
