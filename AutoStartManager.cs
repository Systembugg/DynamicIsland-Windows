using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace DynamicIsland
{
    public static class AutoStartManager
    {
        private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "DynamicIsland";

        public static bool IsAutoStartEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
                return key?.GetValue(AppName) != null;
            }
            catch
            {
                return false;
            }
        }

        public static void SetAutoStart(bool enable)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
                if (key == null) return;

                if (enable)
                {
                    string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                    if (!string.IsNullOrEmpty(exePath))
                    {
                        key.SetValue(AppName, $"\"{exePath}\"");
                    }
                }
                else
                {
                    key.DeleteValue(AppName, false);
                }
            }
            catch { }
        }
    }
}
