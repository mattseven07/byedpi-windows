using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Windows.Forms;

namespace ByeDPI
{
    static class Program
    {
        // Version of the Android app this port mirrors
        public const string Version = "1.2.0";
        public const string SourceUrl = "https://github.com/dovecoteescapee/ByeDPIAndroid";
        public const string DocsUrl = "https://github.com/hufrea/byedpi/blob/v0.13/README.md";

        [STAThread]
        static void Main(string[] args)
        {
            bool connect = args.Contains("--connect");
            bool relaunch = args.Contains("--relaunch");
            bool tray = args.Contains("--tray");

            if (!SingleInstance.Acquire(relaunch ? 15000 : 0))
            {
                SingleInstance.SignalExisting();
                return;
            }
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                AppLog.Info("App", "ByeDPI " + Version + " for Windows, admin=" + Native.IsAdmin());

                Settings.Load();
                SystemProxy.RecoverAfterCrash();
                if (Native.IsAdmin()) ByeDpiService.CleanupLeftovers();
                Theme.Apply(Settings.Get("app_theme", "system"));

                Application.Run(new MainForm(connect, tray));
            }
            finally
            {
                SingleInstance.Release();
            }
        }

        // Equivalent of the Android VPN permission prompt: VPN mode needs an
        // elevated process to create the Wintun adapter and routes
        public static bool RelaunchElevated(string extraArgs)
        {
            try
            {
                var psi = new ProcessStartInfo(Application.ExecutablePath, "--relaunch " + extraArgs);
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                Process.Start(psi);
                return true;
            }
            catch (Win32Exception e)
            {
                AppLog.Warn("App", "Elevation declined: " + e.Message);
                return false;
            }
        }

        public static void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception e)
            {
                AppLog.Warn("App", "Cannot open " + url + ": " + e.Message);
            }
        }
    }
}
