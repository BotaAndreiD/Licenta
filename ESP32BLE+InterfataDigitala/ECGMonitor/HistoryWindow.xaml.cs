using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ECGMonitor
{
    public class SessionListItem
    {
        public string Path { get; set; } = "";
        public EcgSession Session { get; set; } = new();
        public string PatientName => Session.PatientName;
        public string RecordedAtText => Session.RecordedAt.ToString("dd.MM.yyyy HH:mm");
        public string DurationText => $"{(int)Session.DurationSeconds}s";
        public string AvgHeartRate => $"{Session.AvgHeartRate} bpm";
        public string RhythmSummary => Session.RhythmSummary;
    }

    public partial class HistoryWindow : Window
    {
        public HistoryWindow()
        {
            InitializeComponent();
            WindowMaximizeFix.Apply(this);
            ReloadSessions();
        }

        private void MinimizeBtn_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void ReloadSessions()
        {
            var items = SessionStore
                .LoadAll()
                .Select(r => new SessionListItem { Path = r.Path, Session = r.Session })
                .ToList();

            SessionsList.ItemsSource = items;
        }

        private void SessionsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (SessionsList.SelectedItem is SessionListItem item)
            {
                var review = new ReviewWindow(item.Session);
                review.Show();
            }
        }

        private void DeleteBtn_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.DataContext is not SessionListItem item)
                return;

            bool confirm = AppMessageBox.Show(
                this,
                $"Ștergi definitiv înregistrarea pentru {item.PatientName} din {item.RecordedAtText}?",
                "Confirmare ștergere",
                isConfirm: true,
                primaryText: "Șterge",
                secondaryText: "Anulează"
            );

            if (!confirm)
                return;

            SessionStore.Delete(item.Path);
            ReloadSessions();
        }
    }
}
