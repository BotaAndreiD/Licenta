using System.Windows;
using System.Windows.Input;

namespace ECGMonitor
{
    public partial class PatientDialog : Window
    {
        public string PatientName { get; private set; } = "";
        public string PatientAge { get; private set; } = "";
        public string PatientSex { get; private set; } = "";
        public string PatientId { get; private set; } = "";
        public string DoctorName { get; private set; } = "";
        public string ClinicalNotes { get; private set; } = "";

        public PatientDialog()
        {
            InitializeComponent();
        }

        private void StartBtn_Click(object sender, RoutedEventArgs e)
        {
            PatientName = NameBox.Text.Trim();
            PatientAge = AgeBox.Text.Trim();
            PatientSex =
                (SexBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString()
                ?? "";
            PatientId = IdBox.Text.Trim();
            DoctorName = DoctorBox.Text.Trim();
            ClinicalNotes = NotesBox.Text.Trim();
            DialogResult = true;
            Close();
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            this.DragMove();
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
