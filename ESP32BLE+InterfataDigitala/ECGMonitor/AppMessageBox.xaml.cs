using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ECGMonitor
{
    public partial class AppMessageBox : Window
    {
        public bool Result { get; private set; }

        private AppMessageBox(
            string message,
            string title,
            bool isConfirm,
            string primaryText,
            string secondaryText,
            Color accentColor
        )
        {
            InitializeComponent();
            TitleText.Text = title;
            MessageText.Text = message;
            PrimaryBtn.Content = primaryText;
            IconDot.Fill = new SolidColorBrush(accentColor);

            if (isConfirm)
            {
                SecondaryBtn.Visibility = Visibility.Visible;
                SecondaryBtn.Content = secondaryText;
            }

            MouseLeftButtonDown += (s, e) =>
            {
                if (e.OriginalSource is not Button)
                    DragMove();
            };
        }

        private void Primary_Click(object sender, RoutedEventArgs e)
        {
            Result = true;
            Close();
        }

        private void Secondary_Click(object sender, RoutedEventArgs e)
        {
            Result = false;
            Close();
        }

        public static bool Show(
            Window owner,
            string message,
            string title,
            bool isConfirm = false,
            string primaryText = "OK",
            string secondaryText = "Anulează"
        )
        {
            var accent = isConfirm
                ? Color.FromRgb(255, 180, 50)
                : Color.FromRgb(88, 166, 255);
            return Show(owner, message, title, accent, isConfirm, primaryText, secondaryText);
        }

        public static bool ShowError(Window owner, string message, string title = "Eroare")
        {
            return Show(owner, message, title, Color.FromRgb(255, 107, 107));
        }

        private static bool Show(
            Window owner,
            string message,
            string title,
            Color accent,
            bool isConfirm = false,
            string primaryText = "OK",
            string secondaryText = "Anulează"
        )
        {
            var box = new AppMessageBox(message, title, isConfirm, primaryText, secondaryText, accent)
            {
                Owner = owner,
            };
            box.ShowDialog();
            return box.Result;
        }
    }
}
