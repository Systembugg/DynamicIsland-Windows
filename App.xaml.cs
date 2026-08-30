using System;
using System.IO;
using System.Windows;

namespace DynamicIsland
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");

            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                try { File.WriteAllText(logPath, args.ExceptionObject.ToString()); } catch { }
            };

            DispatcherUnhandledException += (s, args) =>
            {
                try { File.WriteAllText(logPath, args.Exception.ToString()); } catch { }
                args.Handled = true;
            };

            TaskScheduler.UnobservedTaskException += (s, args) =>
            {
                try { File.WriteAllText(logPath, args.Exception.ToString()); } catch { }
                args.SetObserved();
            };

            base.OnStartup(e);
        }
    }
}
