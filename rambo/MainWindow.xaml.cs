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

// Required Namespaces for Tray Icon
using System.Windows.Forms; // Requires System.Windows.Forms reference
using System.Drawing;       // Requires System.Drawing reference
using System.Drawing.Imaging;
using System.IO; // For PixelFormat
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;
using PixelFormat = System.Drawing.Imaging.PixelFormat; // Alias to avoid conflict with System.Drawing.Brushes
// using Point = System.Windows.Point; // Alias if needed

namespace rambo
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        #region P/Invoke Declarations

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

        // Required for cleaning up dynamically created icons
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        extern static bool DestroyIcon(IntPtr handle);

        #endregion

        #region Constants and Variables

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
        private const string AppRegistryValueName = "rambo";
        private WindowState _previousWindowState = WindowState.Normal; // Store previous state for restore

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

        // --- Tray Icon ---
        private NotifyIcon _notifyIcon;
        private bool _isExplicitlyClosing = false; // Flag to prevent closing issues when minimizing
        private System.Drawing.Icon _currentIcon; // Store the current icon to dispose its handle


        #endregion

        #region Initialization and Window Events

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;
            // UpdateMaximizeRestoreButtonContent(); // Called in Loaded
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
                // Don't setup tray icon if memory info failed critically
                return;
            }

            SetupTrayIcon(); // Initialize the Tray Icon

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
             _previousWindowState = this.WindowState; // Store initial state
        }

         private void Window_Closing(object sender, CancelEventArgs e)
        {
            // If minimizing via custom close button (optional) or just normal close
             if (!_isExplicitlyClosing)
             {
                 // Optional: Add a setting to control minimize vs close on X button
                 // If you want X to minimize, handle the WindowButton_Click for "Close" differently
                 // and set e.Cancel = true;
                 // Example: If a setting 'MinimizeOnClose' is true:
                 // if (MinimizeOnCloseSetting) {
                 //    e.Cancel = true; // Prevent closing
                 //    // Manually trigger minimize to tray
                 //    this.Hide();
                 //    this.ShowInTaskbar = false;
                 //    _notifyIcon.Visible = true;
                 // }
             }

             // Cleanup resources
            _refreshTimer?.Stop();
            _notifyIcon?.Dispose(); // Remove icon from tray
            DestroyCurrentIconHandle(); // Clean up the last generated icon handle
        }

        private void RefreshTimer_Tick(object sender, EventArgs e)
        {
            // Timer only updates memory info/bar/tray
            if (!_isOptimizing)
            {
                if (UpdateMemoryInfo()) // Update memory info and check success
                {
                    // Update tray icon only if memory info was successful
                    // Dispatcher.Invoke is used to ensure UI updates happen on the UI thread,
                    // although NotifyIcon updates are often marshalled automatically. Better safe.
                     Dispatcher.Invoke(() => UpdateTrayIcon((int)_percentUsed));
                }
            }
        }

        // MODIFIED v2: Handle window state changes for standard minimize/restore
        private void MainWindow_StateChanged(object sender, EventArgs e)
        {
            // This event handles state changes triggered by:
            // 1. Taskbar icon clicks (minimize/restore)
            // 2. System actions (Win+D, etc.)
            // 3. Programmatic changes to WindowState (e.g., restoring from tray)

            if (WindowState == WindowState.Minimized)
            {
                // Standard Minimize (e.g., via Taskbar):
                // The system automatically hides the window when WindowState becomes Minimized.
                // We just need to ensure the taskbar icon remains visible.
                // *** DO NOT CALL this.Hide() here ***
                this.ShowInTaskbar = true;
            }
            else // Handles Normal and Maximized states (when restoring from Minimized or changing between Normal/Maximized)
            {
                // Store the new non-minimized state when it changes
                 if (WindowState != WindowState.Minimized)
                 {
                    _previousWindowState = WindowState;
                 }

                // Ensure window and taskbar icon are visible when not minimized
                // Note: this.Show() might be redundant if the system already showed it, but ensures visibility.
                this.Show();
                this.ShowInTaskbar = true;
            }

            // Update maximize/restore button content whenever state changes
            UpdateMaximizeRestoreButtonContent();
        }

        #endregion

        #region Core Logic (Memory, Admin, Optimization)

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
                Verb = "runas" // Prompt for elevation
            };
            try
            {
                Process.Start(startInfo);
                // Use Application.Current.Shutdown() for WPF apps
                _isExplicitlyClosing = true; // Prevent potential closing issues
                System.Windows.Application.Current.Shutdown();
            }
            catch (Win32Exception ex)
            { // User might cancel UAC prompt
                SetStatus($"Restart cancelled or failed (Win32): {ex.Message}", _cliYellow);
            }
            catch (Exception ex)
            {
                SetStatus($"Error restarting as admin: {ex.Message}", _cliRed);
            }
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
                        return false; // Indicate failure
                    }

                    // Update UI elements (must be done on UI thread)
                    Dispatcher.Invoke(() => {
                        UpdateMemoryDetailUI(); // Updates header text
                        UpdateMemoryTextBar();  // Updates the text-based bar
                    });
                    // Note: Tray icon is updated in the timer tick *after* this returns true.
                    return true; // Indicate success
                }
                else
                {
                    int errorCode = Marshal.GetLastWin32Error();
                    SetStatus($"Error reading memory status (Code: {errorCode})", _cliRed);
                    return false; // Indicate failure
                }
            }
            catch (Exception ex)
            {
                SetStatus($"Exception updating memory info: {ex.Message}", _cliRed);
                return false; // Indicate failure
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

            // Perform optimization on a background thread
            await Task.Run(() =>
            {
                Process[] processes = null;
                try
                {
                    processes = Process.GetProcesses();
                }
                catch (Exception ex)
                {
                    // Update UI on the main thread if reporting error getting processes
                    Dispatcher.Invoke(() => SetStatus($"ERR: GetProcesses: {ex.Message}", _cliRed));
                    failedCount++; // Count this as a failure
                    processes = Array.Empty<Process>(); // Ensure loop doesn't crash
                }

                int currentProcessId = -1;
                try { currentProcessId = Process.GetCurrentProcess().Id; } catch { /* ignore */ }

                foreach (Process process in processes)
                {
                    // Skip System Idle Process (ID 0) and the optimizer itself
                    if (process.Id == 0 || process.Id == currentProcessId)
                    {
                        process.Dispose(); // Dispose the process object
                        continue;
                    }

                    IntPtr handle = IntPtr.Zero;
                    bool success = false;
                    string processName = "N/A";
                    int processId = -1;

                    try
                    {
                        processId = process.Id;
                        processName = process.ProcessName;
                        handle = process.Handle; // Get process handle
                        success = EmptyWorkingSet(handle); // Attempt to trim working set
                        if (success)
                        {
                            optimizedCount++;
                        }
                        else
                        {
                            // Could fail due to permissions or process exiting
                            failedCount++;
                            // Optionally log GetLastError() here if needed
                        }
                    }
                    catch (InvalidOperationException) { failedCount++; } // Process likely exited
                    catch (Win32Exception ex) when (ex.NativeErrorCode == 5) { failedCount++; } // Access Denied (permissions)
                    catch (Win32Exception ex)
                    { // Other Win32 errors
                        failedCount++;
                        detailedErrors.AppendLine($"Win32 {processName}({processId}): {ex.NativeErrorCode}");
                    }
                    catch (Exception ex)
                    { // Other general exceptions
                        failedCount++;
                        detailedErrors.AppendLine($"ERR {processName}({processId}): {ex.GetType().Name}");
                    }
                    finally
                    {
                        process.Dispose(); // Dispose the process object
                    }
                } // End foreach process

                // Perform garbage collection on the optimizer itself
                try
                {
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true);
                    GC.WaitForPendingFinalizers();
                } catch (Exception gcEx) { Debug.WriteLine($"GC Err: {gcEx.Message}"); }

                // Try to trim the optimizer's own working set
                try
                {
                    IntPtr currentProcHandle = GetCurrentProcess();
                    EmptyWorkingSet(currentProcHandle);
                    // No need to close currentProcHandle, GetCurrentProcess() returns a pseudo-handle
                } catch (Exception ex) { Debug.WriteLine($"Self Trim Err: {ex.Message}"); }

            }); // End Task.Run

            // Update UI back on the main thread
            UpdateMemoryInfo(); // Refresh memory stats after optimization
            RefreshProcessList(); // Refresh process list

            ulong memAfter = _availableMemoryBytes;
            long memoryChangeBytes = (long)memAfter - (long)memBefore; // Use signed long for difference

            string resultMessage;
            SolidColorBrush resultColor;

            // Determine result message and color based on memory change
            if (memoryChangeBytes > 1 * 1024 * 1024) // Freed more than 1 MB
            {
                resultMessage = $"Status: OK. Freed: {BytesToMB(memoryChangeBytes):N0} MB.";
                resultColor = _cliGreen;
            }
            else if (memoryChangeBytes < -1 * 1024 * 1024) // Usage increased by more than 1 MB (unlikely but possible)
            {
                resultMessage = $"Status: WARN. Usage Increased: {BytesToMB(Math.Abs(memoryChangeBytes)):N0} MB.";
                resultColor = _cliYellow;
            }
            else // Little change or small amount freed
            {
                 if (memoryChangeBytes > 0) { resultMessage = $"Status: OK. Freed ~{BytesToMB(memoryChangeBytes):N1} MB."; }
                 else { resultMessage = $"Status: OK. Little memory change detected."; }
                 resultColor = _cliBlue;
            }

            // Append error count if any failures occurred
            if (failedCount > 0)
            {
                resultMessage += $" ({failedCount} errors)";
                // Adjust color based on errors
                if (resultColor == _cliGreen || resultColor == _cliBlue) resultColor = _cliYellow; // Downgrade success/info to warning
                if (failedCount > optimizedCount && optimizedCount == 0) resultColor = _cliRed; // If only errors occurred, show red

                // Log detailed errors to debug output if captured
                if (detailedErrors.Length > 0)
                {
                    Debug.WriteLine($"--- Optimization Errors ---\n{detailedErrors}");
                }
            }

            SetStatus(resultMessage, resultColor); // Update status bar

            // Re-enable button and reset text
            _isOptimizing = false;
            OptimizeButton.IsEnabled = true;
            OptimizeButton.Content = ">_optimize memory";
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
                        // Skip Idle process and self
                        if (p.Id == 0 || p.Id == currentProcId) { p.Dispose(); continue; }

                        try
                        {
                            // Ensure process hasn't exited before accessing properties
                            if (!p.HasExited)
                            {
                                processInfos.Add(new ProcessInfo
                                {
                                    Id = p.Id,
                                    Name = p.ProcessName,
                                    MemoryBytes = p.WorkingSet64 // Physical memory usage
                                });
                            }
                        }
                        // Catch specific exceptions for processes that might be inaccessible or exiting
                        catch (Win32Exception) { /* Ignore processes we can't access */ }
                        catch (InvalidOperationException) { /* Ignore processes that exited */ }
                        catch (Exception ex)
                        { // Log unexpected errors
                            Debug.WriteLine($"ProcList Err {p.Id}: {ex.Message}");
                        }
                        finally
                        {
                             p.Dispose(); // Dispose each process object
                        }
                    }
                    // Order by memory usage descending and take top 10
                    return processInfos.OrderByDescending(pi => pi.MemoryBytes).Take(9).ToList();
                }
                catch (Exception ex)
                { // Catch error getting the process list itself
                    Dispatcher.Invoke(() => SetStatus($"ERR: Refresh ProcList: {ex.Message}", _cliRed));
                    return new List<ProcessInfo>(); // Return empty list on failure
                }
                finally
                { // Ensure all process objects are disposed even if Task fails early
                    if (processes != null) { foreach (var p in processes) { try { p.Dispose(); } catch { } } }
                }
            });

            // Update the UI bound collection on the main thread
            Processes = currentProcesses;
            // ProcessListView.ItemsSource = Processes; // Binding handles this
        }

        #endregion

        #region UI Update Methods

        private void SetStatus(string message, SolidColorBrush color)
        {
            // Ensure UI updates happen on the Dispatcher thread
            Dispatcher.Invoke(() =>
            {
                StatusText.Text = message;
                StatusText.Foreground = color ?? _cliBlue; // Use blue as default if color is null
            });
        }

        private void UpdateAdminStatusUI()
        {
             Dispatcher.Invoke(() => {
                 AdminStatusText.Text = _isAdmin ? "[admin]" : "[user]";
                 AdminStatusText.Foreground = _isAdmin ? _cliGreen : _cliYellow;
                 AdminStatusText.ToolTip = _isAdmin ? "Running with Administrator privileges." : "Running as standard user. Some operations might be limited.";
             });
        }

        private void UpdateMemoryDetailUI()
        {
            // Assumes Dispatcher.Invoke is handled by caller (UpdateMemoryInfo)
            TotalRamText.Text = $"total ram: {BytesToGB(_totalPhysicalMemoryBytes):N1} gb";
        }

        private void UpdateMemoryTextBar()
        {
            // Assumes Dispatcher.Invoke is handled by caller (UpdateMemoryInfo)
            const int barWidth = 25; // Width of the text bar in characters
            const char fillChar = '█'; // Character for filled part
            const char emptyChar = '\u2591'; // Character for empty part (light shade)

            // Calculate number of filled blocks
            int filledBlocks = (int)Math.Round((barWidth * _percentUsed) / 100.0);
            filledBlocks = Math.Clamp(filledBlocks, 0, barWidth); // Ensure value is within 0 to barWidth

            // Build the bar string
            StringBuilder bar = new StringBuilder("[");
            bar.Append(fillChar, filledBlocks);
            bar.Append(emptyChar, barWidth - filledBlocks);
            bar.Append(']');

            // Format the final text
            string barText = $"ram usage: {bar} {_percentUsed,3}%  (used: {BytesToGB(_usedMemoryBytes):N1} / {BytesToGB(_totalPhysicalMemoryBytes):N1} gb)";

            MemoryBarText.Text = barText;

            // Set color based on usage percentage
            if (_percentUsed > 85) MemoryBarText.Foreground = _cliRed;
            else if (_percentUsed > 65) MemoryBarText.Foreground = _cliYellow;
            else MemoryBarText.Foreground = _cliForeground; // Default foreground color
        }

        private void UpdateMaximizeRestoreButtonContent()
        {
            // Assumes Dispatcher.Invoke is handled by caller if needed,
            // but typically called from UI events or StateChanged which are already on UI thread.
            if (this.WindowState == WindowState.Maximized)
            {
                MaximizeRestoreButton.Content = "\uE923"; // Restore icon (Segoe MDL2 Assets)
                MaximizeRestoreButton.ToolTip = "Restore";
                MaximizeRestoreButton.Tag = "Restore"; // Update tag for styling/logic if needed
            }
            else // Normal or Minimized
            {
                MaximizeRestoreButton.Content = "\uE922"; // Maximize icon (Segoe MDL2 Assets)
                MaximizeRestoreButton.ToolTip = "Maximize";
                MaximizeRestoreButton.Tag = "Maximize"; // Update tag
            }
        }

        // Method to generate and update the tray icon dynamically
        private void UpdateTrayIcon(int percentage)
        {
            if (_notifyIcon == null) return; // Guard against calls before initialization

            const int iconSize = 32; // Use 16, 32, 48, or 64
            IntPtr hIcon = IntPtr.Zero; // Handle for the generated icon

            try
            {
                // Create a bitmap to draw on (using 32bpp ARGB for transparency)
                using (var bitmap = new Bitmap(iconSize, iconSize, PixelFormat.Format32bppArgb))
                using (var graphics = Graphics.FromImage(bitmap)) // Get graphics context
                {
                    // --- Drawing Logic ---
                    graphics.Clear(System.Drawing.Color.Transparent); // Start with a transparent background

                    // Determine bar color based on percentage
                    System.Drawing.Brush barBrush;
                    if (percentage > 85) barBrush = System.Drawing.Brushes.Red;        // High usage
                    else if (percentage > 65) barBrush = System.Drawing.Brushes.Yellow; // Medium usage
                    else barBrush = System.Drawing.Brushes.LimeGreen;                    // Low usage

                    // Calculate bar height (filling from bottom up)
                    int barHeight = (int)Math.Round((double)iconSize * percentage / 100.0);
                    barHeight = Math.Clamp(barHeight, 0, iconSize); // Ensure height is within bounds

                    // Draw the vertical bar if height > 0
                    if (barHeight > 0)
                    {
                        // Fill rectangle from bottom edge up to calculated height
                        graphics.FillRectangle(barBrush, 0, iconSize - barHeight, iconSize, barHeight);
                    }

                    // Optional: Draw a border
                    // graphics.DrawRectangle(System.Drawing.Pens.DimGray, 0, 0, iconSize - 1, iconSize - 1);
                    // --- End Drawing ---

                    // Get the HICON handle from the bitmap (crucial step)
                    hIcon = bitmap.GetHicon();

                } // Bitmap and Graphics objects are disposed here

                // Create System.Drawing.Icon from the handle if successful
                if (hIcon != IntPtr.Zero)
                {
                     // Create a managed Icon object from the handle.
                     // Icon.FromHandle duplicates the handle, so we are responsible for destroying the original (hIcon)
                     // AND the handle inside the newIcon object later.
                     System.Drawing.Icon newIcon = System.Drawing.Icon.FromHandle(hIcon);

                     // IMPORTANT: Clean up the *previous* icon's handle *before* assigning the new one
                     // This prevents GDI resource leaks.
                     DestroyCurrentIconHandle();

                     // Assign the new icon to the NotifyIcon
                     _notifyIcon.Icon = newIcon;
                     _currentIcon = newIcon; // Store the new icon so we can dispose its handle later

                     // Update tooltip text dynamically
                      _notifyIcon.Text = $"RAM Usage: {percentage}%";
                }
            }
            catch (Exception ex)
            {
                 Debug.WriteLine($"Error updating tray icon: {ex.Message}");
                 // If an error occurred after GetHicon but before assigning, destroy the handle.
                 if (hIcon != IntPtr.Zero && _currentIcon == null) // Check if newIcon wasn't assigned
                 {
                    DestroyIcon(hIcon); // Clean up the raw handle
                 }
            }
            finally
            {
                // IMPORTANT: If Icon.FromHandle succeeded, newIcon (and _currentIcon) now owns the handle.
                // We MUST NOT destroy hIcon here in that case because DestroyIcon would invalidate the handle
                // still owned by _currentIcon. The handle will be destroyed when _currentIcon is disposed or replaced.
                // If Icon.FromHandle failed or wasn't reached, hIcon might still be non-zero if GetHicon succeeded.
                // The catch block handles cleanup in case of exceptions during Icon.FromHandle.
                // If GetHicon succeeded but FromHandle wasn't called (e.g., error before it), hIcon needs cleanup.
                // This check tries to handle that unlikely scenario without double-destroying.
                if (hIcon != IntPtr.Zero && (_currentIcon == null || _currentIcon.Handle != hIcon))
                {
                    // This condition means GetHicon gave us a handle (hIcon != IntPtr.Zero),
                    // but either _currentIcon is null (meaning Icon.FromHandle failed or wasn't called)
                    // OR _currentIcon exists but holds a *different* handle (meaning the assignment failed after FromHandle,
                    // or this is an old hIcon from a previous failed attempt).
                    // In these specific cases, hIcon is likely leaked and needs to be destroyed.
                    // Debug.WriteLine("Destroying potentially leaked hIcon in finally block.");
                    DestroyIcon(hIcon);
                }
            }
        }

        // Helper to destroy the GDI handle of the currently assigned icon
        private void DestroyCurrentIconHandle()
        {
             if (_currentIcon != null)
             {
                 try
                 {
                     // Destroy the HICON handle owned by the Icon object
                     // Note: This assumes the Icon object doesn't internally manage this correctly on Dispose,
                     // which might be overly cautious, but safer for preventing GDI leaks.
                     DestroyIcon(_currentIcon.Handle);
                 }
                 catch (Exception ex)
                 {
                     Debug.WriteLine($"Error destroying icon handle: {ex.Message}");
                 }
                 finally
                 {
                    _currentIcon.Dispose(); // Dispose the managed Icon object
                    _currentIcon = null;
                 }
             }
        }

        #endregion

        #region Event Handlers (Buttons, CheckBox, Tray Icon)

        private void OptimizeButton_Click(object sender, RoutedEventArgs e)
        {
            PerformStandardOptimization();
        }

         private void RunOnBootCheckBox_Changed(object sender, RoutedEventArgs e)
        {
             bool enable = RunOnBootCheckBox.IsChecked ?? false;
             SetRunOnBoot(enable);
        }

        // Handles clicks on Minimize, Maximize/Restore, and Close buttons
        private void WindowButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tag)
            {
                switch (tag)
                {
                    case "Minimize":
                        // --- Minimize to Tray Behavior ---
                        // Hide window and taskbar icon directly
                        this.Hide();
                        this.ShowInTaskbar = false;
                        _notifyIcon.Visible = true; // Ensure tray icon is visible
                        // DO NOT change WindowState here.
                        break;
                    case "Maximize":
                        // Store current state before maximizing (only if it's Normal)
                        if (this.WindowState == WindowState.Normal)
                        {
                            _previousWindowState = this.WindowState;
                        }
                        this.WindowState = WindowState.Maximized;
                        // UpdateMaximizeRestoreButtonContent(); // Handled by StateChanged event
                        break;
                    case "Restore":
                        // Restore to previous state (should be Normal if currently Maximized)
                        this.WindowState = WindowState.Normal; // Use Normal directly, or _previousWindowState if more complex logic needed
                        // UpdateMaximizeRestoreButtonContent(); // Handled by StateChanged event
                        break;
                    case "Close":
                        _isExplicitlyClosing = true; // Signal intentional close
                        this.Close(); // Trigger Window_Closing event
                        // Or Application.Current.Shutdown();
                        break;
                }
            }
        }

        // Handles MouseLeftButtonDown on the title bar Grid to enable dragging
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Ensure DragMove is called only for the primary mouse button
            // and avoid issues with button clicks being misinterpreted as drags.
            if (e.ChangedButton == MouseButton.Left)
            {
                // Check if the original source wasn't one of the window control buttons
                // This prevents trying to drag when clicking Minimize/Maximize/Close
                if (!(e.OriginalSource is Button))
                {
                     try
                     {
                         this.DragMove();
                     }
                     catch (InvalidOperationException)
                     {
                         // This can happen if the window is not in a state that allows dragging
                         // (e.g., maximized, or if DragMove is called inappropriately). Usually safe to ignore.
                     }
                }
            }
        }

        // --- Tray Icon Event Handlers ---

        private void NotifyIcon_MouseClick(object sender, System.Windows.Forms.MouseEventArgs e)
        {
            // Restore window on left-click
            if (e.Button == MouseButtons.Left)
            {
                RestoreWindow();
            }
            // Right-click automatically shows the ContextMenuStrip if assigned
        }

        // Optional: Restore on double-click too
        //private void NotifyIcon_MouseDoubleClick(object sender, System.Windows.Forms.MouseEventArgs e)
        //{
        //    RestoreWindow();
        //}

        // Handles "Show Rambo" context menu item click
        private void ShowMenuItem_Click(object sender, EventArgs e)
        {
            RestoreWindow();
        }

        // Handles "Exit" context menu item click
        private void ExitMenuItem_Click(object sender, EventArgs e)
        {
            _isExplicitlyClosing = true; // Signal that we are intentionally closing
            System.Windows.Application.Current.Shutdown(); // Cleanly shut down the WPF application
        }

        // Helper method to restore the window from tray or context menu
        private void RestoreWindow()
        {
            this.Show(); // Make window visible FIRST
            this.ShowInTaskbar = true; // Ensure it's back in the taskbar
            // Restore to the last known non-minimized state (Normal or Maximized)
            this.WindowState = _previousWindowState;
            this.Activate(); // Bring window to the foreground and give it focus
            // Optional: Ensure Topmost is false if it was set during minimization
            // this.Topmost = false;
        }

        #endregion

        #region Tray Icon Setup

        private void SetupTrayIcon()
        {
             _notifyIcon = new NotifyIcon();
             _notifyIcon.MouseClick += NotifyIcon_MouseClick;
             //_notifyIcon.MouseDoubleClick += NotifyIcon_MouseDoubleClick; // Uncomment for double-click restore

             // Load default icon from Embedded Resources
             try
             {
                  // IMPORTANT: Replace "rambo_icon.ico" with the actual name of your icon file
                  // Ensure its Build Action is set to "Resource" in Properties window
                  // Also ensure the namespace 'rambo' matches your project's default namespace if the icon is in the root.
                  // If the icon is in a subfolder (e.g., "Assets"), the path would be "pack://application:,,,/Assets/rambo_icon.ico"
                  // Or adjust the path based on your project structure.
                  const string iconResourcePath = "pack://application:,,,/rambo_icon.ico"; // CHECK THIS PATH
                  var iconUri = new Uri(iconResourcePath, UriKind.RelativeOrAbsolute);
                  var iconStreamInfo = System.Windows.Application.GetResourceStream(iconUri);

                  if(iconStreamInfo?.Stream != null)
                  {
                        using(var iconStream = iconStreamInfo.Stream) // Ensure stream is disposed
                        {
                             // Create icon from stream
                             _notifyIcon.Icon = new System.Drawing.Icon(iconStream);
                        }
                  }
                  else
                  {
                        throw new Exception($"Icon resource stream was null for path: {iconResourcePath}. Ensure Build Action is 'Resource' and path is correct.");
                  }
             }
             catch (Exception ex)
             {
                 Debug.WriteLine($"Error loading default tray icon: {ex.Message}");
                 SetStatus("WARN: Could not load app icon.", _cliYellow);
                 // Fallback: Try generating a simple initial icon if loading fails
                 UpdateTrayIcon(0); // Generate a blank icon
             }

             _notifyIcon.Text = "Rambo Memory Optimizer"; // Initial tooltip text

             // Create and assign Context Menu
             _notifyIcon.ContextMenuStrip = CreateContextMenu();

             _notifyIcon.Visible = true; // Make the icon visible in the tray
        }

        // Creates the right-click context menu for the tray icon
        private ContextMenuStrip CreateContextMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();

            // Show/Restore option
            ToolStripMenuItem showItem = new ToolStripMenuItem("Show Rambo");
            showItem.Click += ShowMenuItem_Click;
            // Optional: Make 'Show' the default action on double-click (bold)
            // showItem.Font = new System.Drawing.Font(showItem.Font, System.Drawing.FontStyle.Bold);
            menu.Items.Add(showItem);

            // Separator line
            menu.Items.Add(new ToolStripSeparator());

            // Exit option
            ToolStripMenuItem exitItem = new ToolStripMenuItem("Exit");
            exitItem.Click += ExitMenuItem_Click;
            menu.Items.Add(exitItem);

            return menu;
        }

        #endregion

        #region Run on Boot Logic (Registry)

        private void LoadRunOnBootSetting()
        {
            try
            {
                // Open the Run key (read-only)
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(AppRegistryKey, false))
                {
                    if (key != null)
                    {
                        string currentValue = key.GetValue(AppRegistryValueName) as string;
                        // Get the expected path with quotes
                        string expectedPath = $"\"{Process.GetCurrentProcess().MainModule.FileName}\"";
                        // Compare ignoring case
                        RunOnBootCheckBox.IsChecked = !string.IsNullOrEmpty(currentValue) && currentValue.Equals(expectedPath, StringComparison.OrdinalIgnoreCase);
                    }
                    else
                    {   // Key doesn't exist
                        RunOnBootCheckBox.IsChecked = false;
                    }
                }
            }
            catch (Exception ex)
            {
                SetStatus($"WARN: Reading startup setting: {ex.Message}", _cliYellow);
                RunOnBootCheckBox.IsChecked = false;
                RunOnBootCheckBox.IsEnabled = false; // Disable checkbox if registry access fails
            }
        }

        private void SetRunOnBoot(bool enable)
        {
            RegistryKey key = null; // Declare outside try
            try
            {
                // Open the Run key with write access, creating it if it doesn't exist
                key = Registry.CurrentUser.CreateSubKey(AppRegistryKey, true); // true for write access

                if (key == null)
                {
                    throw new Exception($"Could not open or create the Run registry key: {AppRegistryKey}");
                }

                // Set or delete the value
                SetRegistryValueInternal(key, enable);

                 // Update status based on action
                 SetStatus(enable ? "Status: Enabled run on startup." : "Status: Disabled run on startup.", _cliBlue);
            }
            catch (UnauthorizedAccessException)
            { // Permissions issue
                SetStatus("ERR: Permission denied modifying startup.", _cliRed);
                RunOnBootCheckBox.IsChecked = !enable; // Revert checkbox state on error
            }
            catch (Exception ex)
            { // Other errors
                SetStatus($"ERR: Modifying startup: {ex.Message}", _cliRed);
                RunOnBootCheckBox.IsChecked = !enable; // Revert checkbox state on error
            }
            finally
            {
                 key?.Dispose(); // Ensure the key handle is released
            }
        }

        // Internal helper to set or delete the registry value
        private void SetRegistryValueInternal(RegistryKey key, bool enable)
        {
             string executablePath = Process.GetCurrentProcess().MainModule.FileName;
             if (enable)
             {
                 // Add quotes around the path for robustness, especially if path contains spaces
                 key.SetValue(AppRegistryValueName, $"\"{executablePath}\"", RegistryValueKind.String);
             }
             else
             {
                 // Check if the value exists before trying to delete to avoid exceptions
                 if (key.GetValue(AppRegistryValueName) != null)
                 {
                    key.DeleteValue(AppRegistryValueName, false); // false = do not throw if not found
                 }
             }
        }

        #endregion

        #region Utility Methods

        public static float BytesToMB(long bytes) { if (bytes <= 0) return 0; return bytes / (1024f * 1024f); }
        public static float BytesToMB(ulong bytes) { if (bytes == 0) return 0; return bytes / (1024f * 1024f); }
        public static float BytesToGB(long bytes) { if (bytes <= 0) return 0; return bytes / (1024f * 1024f * 1024f); }
        public static float BytesToGB(ulong bytes) { if (bytes == 0) return 0; return bytes / (1024f * 1024f * 1024f); }

        #endregion

        #region INotifyPropertyChanged Implementation
        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged(string propertyName)
        {
            // Ensure property changes are raised on the UI thread for bindings
            Dispatcher.Invoke(() => {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            });
        }
        #endregion
    }
}
