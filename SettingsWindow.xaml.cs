using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace DynamicIsland
{
    public partial class SettingsWindow : Window
    {
        [DllImport("psapi.dll")]
        static extern int EmptyWorkingSet(IntPtr hwProc);

        private readonly MainWindow mainWindow;
        private readonly DispatcherTimer ramTimer;

        public SettingsWindow(MainWindow main)
        {
            InitializeComponent();
            mainWindow = main;

            ChkAutoStart.IsChecked = AutoStartManager.IsAutoStartEnabled();
            CmbDisplayMode.SelectedIndex = mainWindow.CurrentDisplayMode == ShapeDisplayMode.Notch ? 0 : 1;
            ChkIdleFace.IsChecked = mainWindow.IsIdleFaceEnabled;

            UpdateRamUsage();

            ramTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            ramTimer.Tick += (s, e) => UpdateRamUsage();
            ramTimer.Start();

            Closed += (s, e) => ramTimer.Stop();
        }

        private void UpdateRamUsage()
        {
            try
            {
                using var proc = Process.GetCurrentProcess();
                proc.Refresh();
                long mb = proc.WorkingSet64 / (1024 * 1024);
                TxtRamUsage.Text = $"{mb} MB";
                TxtRamUsage.Foreground = mb < 50 ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x30, 0xD1, 0x58)) :
                                                   new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x95, 0x00));
            }
            catch { }
        }

        private void ChkAutoStart_Changed(object sender, RoutedEventArgs e)
        {
            AutoStartManager.SetAutoStart(ChkAutoStart.IsChecked == true);
        }

        private void CmbDisplayMode_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (mainWindow == null) return;
            if (CmbDisplayMode.SelectedIndex == 0)
            {
                mainWindow.SetMode(ShapeDisplayMode.Notch);
            }
            else if (CmbDisplayMode.SelectedIndex == 1)
            {
                mainWindow.SetMode(ShapeDisplayMode.Island);
            }
        }

        private void ChkIdleFace_Changed(object sender, RoutedEventArgs e)
        {
            if (mainWindow == null) return;
            mainWindow.IsIdleFaceEnabled = ChkIdleFace.IsChecked == true;
        }

        private void BtnOptimizeRam_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                EmptyWorkingSet(Process.GetCurrentProcess().Handle);
                UpdateRamUsage();
            }
            catch { }
        }

        private void BtnClose_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            this.Close();
        }
    }
}
