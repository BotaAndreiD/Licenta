using System.Windows;

namespace ECGMonitor
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var splash = new SplashScreen();
            bool? connected = splash.ShowDialog();
            if (connected != true)
            {
                Shutdown();
                return;
            }

            var main = new MainWindow();
            MainWindow = main;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            main.Show();
        }
    }
}
