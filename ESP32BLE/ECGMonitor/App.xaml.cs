using System;
using System.Windows;

namespace ECGMonitor
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                MessageBox.Show(args.ExceptionObject.ToString(), "Eroare fatala");
            };

            DispatcherUnhandledException += (s, args) =>
            {
                MessageBox.Show(args.Exception.ToString(), "Eroare UI");
                args.Handled = true;
            };

            var window = new MainWindow();
            window.Show();
        }
    }
}
