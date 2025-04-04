// App.xaml.cs
using System;
using System.Threading;     // Required for Mutex
using System.Windows;       // Required for Application, MessageBox, StartupEventArgs, ExitEventArgs

namespace rambo
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        // --- Single Instance Mutex ---
        // IMPORTANT: Replace this GUID with one you generate! (Tools -> Create GUID -> Registry Format -> Copy without {})
        // The "Global\" prefix makes it system-wide, needed if running under different user sessions.
        private const string AppMutexName = "Global\\{272EAAC3-7223-4EFE-ACEF-10E0EA37F557}"; 
        private static Mutex _appMutex;
        // -----------------------------

        protected override void OnStartup(StartupEventArgs e)
        {
            bool createdNew;

            // Attempt to create or open the named mutex
            _appMutex = new Mutex(true, AppMutexName, out createdNew);

            if (!createdNew)
            {
                // Another instance already holds the mutex.
                MessageBox.Show("simit's memory optimizer is already open ya dummy!", // Your requested message
                                "stupidity!",
                                MessageBoxButton.OK,
                                MessageBoxImage.Exclamation); // Or Information/Warning

                // Shutdown this new instance immediately.
                Application.Current.Shutdown();
                return; // Exit the OnStartup method early
            }

            // --- This is the first instance, proceed with normal startup ---

            // Keep the mutex alive for the duration of the application
            GC.KeepAlive(_appMutex);

            // Let the base application class continue its startup sequence
            // (This will typically load the window defined in App.xaml's StartupUri)
            base.OnStartup(e);

            // Alternative if you remove StartupUri from App.xaml:
            // MainWindow mainWindow = new MainWindow();
            // this.MainWindow = mainWindow;
            // mainWindow.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // Release the mutex when the application exits
            // This allows another instance to start afterward if this one closes.
            _appMutex?.ReleaseMutex();
            _appMutex?.Close(); // Dispose of the mutex handle
            _appMutex = null;

            base.OnExit(e);
        }
    }
}