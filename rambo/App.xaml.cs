using System;
using System.Threading;     // Required for Mutex
using System.Windows;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox; // Required for Application, MessageBox, StartupEventArgs, ExitEventArgs

namespace rambo
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        
        private const string AppMutexName = "Global\\{2B8769ED-0317-4768-8543-B2E6AC866E11}";
        private static Mutex _appMutex;
        // -----------------------------

        protected override void OnStartup(StartupEventArgs e)
        {
            bool createdNew; // Flag to indicate if this instance created the mutex

            // Attempt to create or open the named mutex.
            // Request initial ownership (true).
            // 'createdNew' will be true if this is the first instance, false otherwise.
            _appMutex = new Mutex(true, AppMutexName, out createdNew);

            if (!createdNew)
            {
                // Another instance already holds the mutex.
                MessageBox.Show("app is already open dummy!", // Your requested message
                                "Rambo Optimizer - Already Running",
                                MessageBoxButton.OK,
                                MessageBoxImage.Exclamation); // Use Exclamation icon

                // Shutdown this new instance immediately.
                Application.Current.Shutdown();
                return; // Exit the OnStartup method early to prevent window loading
            }

            // --- This is the first instance, proceed with normal startup ---

            // Optional: Explicitly keep the mutex alive for the duration of the application.
            // Storing it in a static field usually suffices, but this doesn't hurt.
            // GC.KeepAlive(_appMutex);

            // Let the base application class continue its startup sequence.
            // This will typically load the window defined in App.xaml's StartupUri (MainWindow.xaml).
            base.OnStartup(e);

            // Alternative if you remove StartupUri="MainWindow.xaml" from App.xaml:
            // If you prefer to control window creation explicitly after the mutex check:
            // 1. Remove StartupUri="MainWindow.xaml" from your App.xaml file.
            // 2. Uncomment the following lines:
            // MainWindow mainWindow = new MainWindow();
            // this.MainWindow = mainWindow; // Set the main window property
            // mainWindow.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // Release and close the mutex when the application exits cleanly.
            // This allows another instance to start afterward if this one closes.
            if (_appMutex != null)
            {
                try
                {
                    // Release the mutex only if this instance owns it (which it should if createdNew was true)
                    // Note: Releasing a mutex you don't own throws an exception.
                    // A robust check might involve checking ownership state if complex scenarios arise,
                    // but for simple single-instance pattern, direct release is common.
                     _appMutex.ReleaseMutex();
                }
                catch (ApplicationException ex)
                {
                    // Handle cases where mutex might not be owned (less likely here)
                    System.Diagnostics.Debug.WriteLine($"Mutex release warning: {ex.Message}");
                }
                catch (ObjectDisposedException)
                {
                    // Mutex might already be disposed if shutdown is complex
                }
                finally
                {
                     _appMutex.Close(); // Dispose of the mutex handle
                     _appMutex = null;
                }
            }

            base.OnExit(e);
        }
    }
}
