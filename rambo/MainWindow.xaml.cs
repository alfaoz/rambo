using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input; // Required for MouseButtonEventArgs
using System.Windows.Media; // For SolidColorBrush
using System.Windows.Threading; // For DispatcherTimer
using Microsoft.Win32; // For Registry access
using System.Windows.Controls; // Required for Button

namespace MemoryOptimizerWPF
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        #region P/Invoke Declarations (Unchanged)

        [DllImport("psapi.dll", SetLastError = true)]
        static extern bool EmptyWorkingSet(IntPtr hProcess);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern IntPtr GetCurrentProcess();

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;

            public MEMORYSTATUSEX()
            {
                this.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        #endregion

        #region Constants and Variables (Unchanged)

        // --- CLI Theme Colors (Resolved from Resources) ---
        private SolidColorBrush _cliGreen;
        private SolidColorBrush _cliRed;
        private SolidColorBrush _cliBlue; // For Info
        private SolidColorBrush _cliYellow; // For Warning
        private SolidColorBrush _cliForeground; // Default text

        // --- Memory Info ---
        private ulong _totalPhysicalMemoryBytes;
        private ulong _availableMemoryBytes;
        private ulong _usedMemoryBytes;
        private uint _percentUsed;

        // --- State ---
        private bool _isAdmin = false;
        private bool _isOptimizing = false;
        private DispatcherTimer _refreshTimer;
        private const string AppRegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string AppRegistryValueName = "SimitsMemoryOptimizerWPF_CLI"; // Unique name

        // --- Process List ---
        public class ProcessInfo
        {
            public string Name { get; set; }
            public long MemoryBytes { get; set; }
            public double MemoryMB => BytesToMB(MemoryBytes);
            public int Id { get; set; }
        }
        private List<ProcessInfo> _processes = new List<ProcessInfo>();
        public List<ProcessInfo> Processes
        {
            get => _processes;
            set { _processes = value; OnPropertyChanged(nameof(Processes)); }
        }

        #endregion

        #region Initialization and Window Events (Unchanged except for WindowState logic)

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;
            // Adjust button content based on initial state (optional, handled in XAML for now)
            // UpdateMaximizeRestoreButtonContent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Resolve CLI colors from resources AFTER InitializeComponent
            try
            {
                _cliGreen = (SolidColorBrush)FindResource("CliGreen");
                _cliRed = (SolidColorBrush)FindResource("CliRed");
                _cliBlue = (SolidColorBrush)FindResource("CliBlue");
                _cliYellow = (SolidColorBrush)FindResource("CliYellow");
                _cliForeground = (SolidColorBrush)FindResource("CliForeground");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading color resources: {ex.Message}\nUsing fallback colors.", "Resource Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                // Fallback colors if resources are missing/incorrect
                _cliGreen = Brushes.LimeGreen;
                _cliRed = Brushes.Red;
                _cliBlue = Brushes.DodgerBlue;
                _cliYellow = Brushes.Yellow;
                _cliForeground = Brushes.DimGray; // Or Brushes.LightGray
            }

            _isAdmin = IsAdministrator();
            UpdateAdminStatusUI();

            if (!_isAdmin)
            {
                SetStatus("Warning: Running without Administrator privileges. Optimization may be limited.", _cliYellow);
            }

            if (!UpdateMemoryInfo()) // Initial memory info load
            {
                SetStatus("CRITICAL: Could not retrieve initial memory info. Please restart.", _cliRed);
                OptimizeButton.IsEnabled = false;
                return;
            }

            // Setup timer for periodic refresh
            _refreshTimer = new DispatcherTimer();
            _refreshTimer.Interval = TimeSpan.FromMilliseconds(750); // Refresh interval
            _refreshTimer.Tick += RefreshTimer_Tick;
            _refreshTimer.Start();

            LoadRunOnBootSetting();
            RefreshProcessList(); // Initial load of process list

            // Update Maximize button content based on initial state
             UpdateMaximizeRestoreButtonContent();
             this.StateChanged += MainWindow_StateChanged; // Hook state changed event
        }

         private void Window_Closing(object sender, CancelEventArgs e)
        {
            _refreshTimer?.Stop();
        }

        private void RefreshTimer_Tick(object sender, EventArgs e)
        {
            // Timer only updates memory info/bar
            if (!_isOptimizing)
            {
                UpdateMemoryInfo();
            }
        }

        // NEW: Handle window state changes to update button content
        private void MainWindow_StateChanged(object sender, EventArgs e)
        {
            UpdateMaximizeRestoreButtonContent();
        }

        #endregion

        #region Core Logic (Memory, Admin, Optimization) (Unchanged)

        private bool IsAdministrator()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    WindowsPrincipal principal = new WindowsPrincipal(identity);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => SetStatus($"Error checking admin status: {ex.Message}", _cliYellow ?? Brushes.Yellow));
                return false;
            }
        }

        private void RestartAsAdmin()
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Process.GetCurrentProcess().MainModule.FileName,
                UseShellExecute = true,
                Verb = "runas"
            };
            try
            {
                Process.Start(startInfo);
                Application.Current.Shutdown();
            }
            catch (Win32Exception ex) { SetStatus($"Error restarting (Win32): {ex.Message}", _cliRed); }
            catch (Exception ex) { SetStatus($"Error restarting: {ex.Message}", _cliRed); }
        }


        private bool UpdateMemoryInfo()
        {
            try
            {
                MEMORYSTATUSEX memStatus = new MEMORYSTATUSEX();
                if (GlobalMemoryStatusEx(ref memStatus))
                {
                    _totalPhysicalMemoryBytes = memStatus.ullTotalPhys;
                    _availableMemoryBytes = memStatus.ullAvailPhys;
                    _usedMemoryBytes = _totalPhysicalMemoryBytes - _availableMemoryBytes;
                    _percentUsed = memStatus.dwMemoryLoad;

                    if (_totalPhysicalMemoryBytes == 0)
                    {
                        SetStatus("Error: Could not read total physical memory.", _cliRed);
                        return false;
                    }

                    // Update UI elements (must be done on UI thread)
                    Dispatcher.Invoke(() => {
                        UpdateMemoryDetailUI(); // Updates header text
                        UpdateMemoryTextBar();  // Updates the text-based bar
                    });

                    return true;
                }
                else
                {
                    int errorCode = Marshal.GetLastWin32Error();
                    SetStatus($"Error reading memory status (Code: {errorCode})", _cliRed);
                    return false;
                }
            }
            catch (Exception ex)
            {
                SetStatus($"Exception updating memory info: {ex.Message}", _cliRed);
                return false;
            }
        }

        private async void PerformStandardOptimization()
        {
            if (_isOptimizing) return;

            _isOptimizing = true;
            SetStatus("Status: Optimizing memory... Please wait.", _cliBlue); // Use Info color
            OptimizeButton.IsEnabled = false;
            OptimizeButton.Content = "optimizing...";

            ulong memBefore = _availableMemoryBytes;
            int optimizedCount = 0;
            int failedCount = 0;
            StringBuilder detailedErrors = new StringBuilder();

            await Task.Run(() =>
            {
                Process[] processes = null;
                try { processes = Process.GetProcesses(); }
                catch (Exception ex) { Dispatcher.Invoke(() => SetStatus($"ERR: GetProcesses: {ex.Message}", _cliRed)); failedCount++; processes = Array.Empty<Process>(); }

                int currentProcessId = -1;
                try { currentProcessId = Process.GetCurrentProcess().Id; } catch { /* ignore */ }

                foreach (Process process in processes)
                {
                    if (process.Id == 0 || process.Id == currentProcessId) { process.Dispose(); continue; }
                    IntPtr handle = IntPtr.Zero; bool success = false; string processName = "N/A"; int processId = -1;
                    try
                    {
                        processId = process.Id; processName = process.ProcessName; handle = process.Handle; success = EmptyWorkingSet(handle);
                        if (success) { optimizedCount++; } else { failedCount++; }
                    }
                    catch (InvalidOperationException) { failedCount++; } catch (Win32Exception ex) when (ex.NativeErrorCode == 5) { failedCount++; }
                    catch (Win32Exception ex) { failedCount++; detailedErrors.AppendLine($"Win32 {processName}({processId}): {ex.NativeErrorCode}"); }
                    catch (Exception ex) { failedCount++; detailedErrors.AppendLine($"ERR {processName}({processId}): {ex.GetType().Name}"); }
                    finally { process.Dispose(); }
                }
                try { GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true); GC.WaitForPendingFinalizers(); } catch (Exception gcEx) { Debug.WriteLine($"GC Err: {gcEx.Message}"); }
                try { IntPtr currentProcHandle = GetCurrentProcess(); EmptyWorkingSet(currentProcHandle); } catch (Exception ex) { Debug.WriteLine($"Self Err: {ex.Message}"); }
            }); // End Task.Run

            UpdateMemoryInfo();
            RefreshProcessList();

            ulong memAfter = _availableMemoryBytes;
            long memoryChangeBytes = (long)memAfter - (long)memBefore;

            string resultMessage;
            SolidColorBrush resultColor;

            if (memoryChangeBytes > 1 * 1024 * 1024)
            {
                resultMessage = $"Status: OK. Freed: {BytesToMB(memoryChangeBytes):N0} MB.";
                resultColor = _cliGreen;
            }
            else if (memoryChangeBytes < -1 * 1024 * 1024)
            {
                resultMessage = $"Status: WARN. Usage Increased: {BytesToMB(Math.Abs(memoryChangeBytes)):N0} MB.";
                resultColor = _cliYellow;
            }
            else
            {
                 if (memoryChangeBytes > 0) { resultMessage = $"Status: OK. Freed ~{BytesToMB(memoryChangeBytes):N1} MB."; }
                 else { resultMessage = $"Status: OK. Little memory change detected."; }
                 resultColor = _cliBlue;
            }

            if (failedCount > 0)
            {
                resultMessage += $" ({failedCount} errors)";
                if (resultColor == _cliGreen || resultColor == _cliBlue) resultColor = _cliYellow;
                if (failedCount > optimizedCount && optimizedCount == 0) resultColor = _cliRed;

                if (detailedErrors.Length > 0) { Debug.WriteLine($"--- Opt Errors ---\n{detailedErrors}"); }
            }

            SetStatus(resultMessage, resultColor);

            _isOptimizing = false;
            OptimizeButton.IsEnabled = true;
            OptimizeButton.Content = "_Optimize Memory";
        }


        private async void RefreshProcessList()
        {
            List<ProcessInfo> currentProcesses = await Task.Run(() =>
            {
                var processInfos = new List<ProcessInfo>();
                Process[] processes = null;
                try
                {
                    processes = Process.GetProcesses();
                    int currentProcId = Process.GetCurrentProcess().Id;
                    foreach(var p in processes)
                    {
                        if (p.Id == 0 || p.Id == currentProcId) { p.Dispose(); continue; }
                        try { processInfos.Add(new ProcessInfo { Id = p.Id, Name = p.ProcessName, MemoryBytes = p.WorkingSet64 }); }
                        catch (Win32Exception) { /* Ignore */ } catch (InvalidOperationException) { /* Ignore */ }
                        catch (Exception ex) { Debug.WriteLine($"ProcList Err {p.Id}: {ex.Message}"); }
                        finally { p.Dispose(); }
                    }
                    return processInfos.OrderByDescending(pi => pi.MemoryBytes).Take(5).ToList();
                }
                catch (Exception ex) { Dispatcher.Invoke(() => SetStatus($"ERR: Refresh ProcList: {ex.Message}", _cliRed)); return new List<ProcessInfo>(); }
                finally { if (processes != null) { foreach (var p in processes) { try { p.Dispose(); } catch { } } } }
            });

            Processes = currentProcesses;
            // ProcessListView.ItemsSource = Processes; // Setting ItemsSource in XAML via Binding is preferred
        }

        #endregion

        #region UI Update Methods (SetStatus, Admin, Detail unchanged)

        private void SetStatus(string message, SolidColorBrush color)
        {
            Dispatcher.Invoke(() =>
            {
                StatusText.Text = message;
                StatusText.Foreground = color ?? _cliBlue;
            });
        }

        private void UpdateAdminStatusUI()
        {
             Dispatcher.Invoke(() => {
                 AdminStatusText.Text = _isAdmin ? "[Admin]" : "[User]";
                 AdminStatusText.Foreground = _isAdmin ? _cliGreen : _cliYellow;
                 AdminStatusText.ToolTip = _isAdmin ? "Running with Administrator privileges." : "Running as standard user. Some operations might be limited.";
             });
        }

        private void UpdateMemoryDetailUI()
        {
            TotalRamText.Text = $"Total RAM: {BytesToGB(_totalPhysicalMemoryBytes):N1} GB";
        }

        // === UPDATED METHOD for Text-Based Bar ===
        private void UpdateMemoryTextBar()
        {
            const int barWidth = 25;
            const char fillChar = '█';
            const char emptyChar = '\u2591'; // Was '░'

            int filledBlocks = (int)Math.Round((barWidth * _percentUsed) / 100.0);
            filledBlocks = Math.Clamp(filledBlocks, 0, barWidth);

            StringBuilder bar = new StringBuilder("[");
            bar.Append(fillChar, filledBlocks);
            bar.Append(emptyChar, barWidth - filledBlocks);
            bar.Append(']');

            string barText = $"RAM Usage: {bar} {_percentUsed,3}%  (Used: {BytesToGB(_usedMemoryBytes):N1} / {BytesToGB(_totalPhysicalMemoryBytes):N1} GB)";

            MemoryBarText.Text = barText;

            if (_percentUsed > 85) MemoryBarText.Foreground = _cliRed;
            else if (_percentUsed > 65) MemoryBarText.Foreground = _cliYellow;
            else MemoryBarText.Foreground = _cliForeground;
        }
        // === END UPDATED METHOD ===

        // NEW: Method to update Maximize/Restore button content
        private void UpdateMaximizeRestoreButtonContent()
        {
            if (this.WindowState == WindowState.Maximized)
            {
                MaximizeRestoreButton.Content = "\uE923"; // Restore icon
                MaximizeRestoreButton.ToolTip = "Restore";
                MaximizeRestoreButton.Tag = "Restore"; // Update tag for potential different styling/logic
            }
            else
            {
                MaximizeRestoreButton.Content = "\uE922"; // Maximize icon
                MaximizeRestoreButton.ToolTip = "Maximize";
                MaximizeRestoreButton.Tag = "Maximize"; // Update tag
            }
        }

        #endregion

        #region Event Handlers (Buttons, CheckBox)

        private void OptimizeButton_Click(object sender, RoutedEventArgs e)
        {
            PerformStandardOptimization();
        }

         private void RunOnBootCheckBox_Changed(object sender, RoutedEventArgs e)
        {
             bool enable = RunOnBootCheckBox.IsChecked ?? false;
             SetRunOnBoot(enable);
        }

        // === NEW EVENT HANDLERS for Custom Window Chrome ===

        /// <summary>
        /// Handles clicks on Minimize, Maximize/Restore, and Close buttons.
        /// Uses the Button's Tag property to determine the action.
        /// </summary>
        private void WindowButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tag)
            {
                switch (tag)
                {
                    case "Minimize":
                        this.WindowState = WindowState.Minimized;
                        break;
                    case "Maximize":
                        this.WindowState = WindowState.Maximized;
                        // UpdateMaximizeRestoreButtonContent(); // Handled by StateChanged event
                        break;
                    case "Restore":
                        this.WindowState = WindowState.Normal;
                        // UpdateMaximizeRestoreButtonContent(); // Handled by StateChanged event
                        break;
                    case "Close":
                        this.Close();
                        // Or Application.Current.Shutdown();
                        break;
                }
            }
        }

        /// <summary>
        /// Handles MouseLeftButtonDown on the title bar Grid to enable dragging.
        /// </summary>
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Ensure DragMove is called only for the primary mouse button
            // and avoid issues with button clicks being misinterpreted as drags.
            if (e.ChangedButton == MouseButton.Left)
            {
                // Check if the original source wasn't one of the window control buttons
                if (!(e.OriginalSource is Button))
                {
                     try
                     {
                         this.DragMove();
                     }
                     catch (InvalidOperationException)
                     {
                         // This can happen if the window is not in a state that allows dragging (e.g., maximized)
                         // Or if DragMove is called inappropriately. Usually safe to ignore here.
                     }
                }
            }
        }
        // === END NEW EVENT HANDLERS ===


        #endregion

        #region Run on Boot Logic (Registry) - Unchanged

        private void LoadRunOnBootSetting()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(AppRegistryKey, false))
                {
                    if (key != null)
                    {
                        string currentValue = key.GetValue(AppRegistryValueName) as string;
                        // Ensure path comparison handles quotes correctly
                        string expectedPath = $"\"{Process.GetCurrentProcess().MainModule.FileName}\"";
                        RunOnBootCheckBox.IsChecked = !string.IsNullOrEmpty(currentValue) && currentValue.Equals(expectedPath, StringComparison.OrdinalIgnoreCase);
                    }
                    else { RunOnBootCheckBox.IsChecked = false; }
                }
            }
            catch (Exception ex) { SetStatus($"WARN: Reading startup setting: {ex.Message}", _cliYellow); RunOnBootCheckBox.IsChecked = false; RunOnBootCheckBox.IsEnabled = false; }
        }

        private void SetRunOnBoot(bool enable)
        {
            try
            {
                // Ensure we have write access
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(AppRegistryKey, true))
                {
                    if (key == null)
                    {
                        // Attempt to create the key if it doesn't exist
                        using (RegistryKey baseKey = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion", true))
                        {
                            baseKey?.CreateSubKey("Run"); // Create Run key if needed
                        }
                        // Try opening again after creation attempt
                        using (RegistryKey retryKey = Registry.CurrentUser.OpenSubKey(AppRegistryKey, true))
                        {
                             if (retryKey == null) { throw new Exception("Could not open or create the Run registry key."); }
                             SetRegistryValueInternal(retryKey, enable);
                        }
                    }
                    else
                    {
                        SetRegistryValueInternal(key, enable);
                    }
                }
                 SetStatus(enable ? "Status: Enabled run on startup." : "Status: Disabled run on startup.", _cliBlue);
            }
            catch (UnauthorizedAccessException) { SetStatus("ERR: Permission denied modifying startup.", _cliRed); RunOnBootCheckBox.IsChecked = !enable; } // Revert checkbox state on error
            catch (Exception ex) { SetStatus($"ERR: Modifying startup: {ex.Message}", _cliRed); RunOnBootCheckBox.IsChecked = !enable; } // Revert checkbox state on error
        }

        private void SetRegistryValueInternal(RegistryKey key, bool enable)
        {
             string executablePath = Process.GetCurrentProcess().MainModule.FileName;
             if (enable)
             {
                 // Add quotes around the path for robustness
                 key.SetValue(AppRegistryValueName, $"\"{executablePath}\"", RegistryValueKind.String);
             }
             else
             {
                 // Check if the value exists before trying to delete
                 if (key.GetValue(AppRegistryValueName) != null)
                 {
                    key.DeleteValue(AppRegistryValueName, false); // Do not throw if not found
                 }
             }
        }

        #endregion

        #region Utility Methods - Unchanged

        public static float BytesToMB(long bytes) { if (bytes <= 0) return 0; return bytes / (1024f * 1024f); }
        public static float BytesToMB(ulong bytes) { if (bytes == 0) return 0; return bytes / (1024f * 1024f); }
        public static float BytesToGB(long bytes) { if (bytes <= 0) return 0; return bytes / (1024f * 1024f * 1024f); }
        public static float BytesToGB(ulong bytes) { if (bytes == 0) return 0; return bytes / (1024f * 1024f * 1024f); }

        #endregion

        #region INotifyPropertyChanged Implementation - Unchanged
        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged(string propertyName)
        {
            // Ensure property changes are raised on the UI thread if needed by bindings
            Dispatcher.Invoke(() => {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            });
        }
        #endregion
    }
}
