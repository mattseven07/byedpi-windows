using System;
using System.Runtime.InteropServices;

namespace ByeDPI
{
    // Proxy mode on Windows: point the WinINet system proxy (used by Edge,
    // Chrome, the Discord app and most other programs) at the local SOCKS port.
    // The previous configuration is saved and restored on stop, and after a crash.
    static class SystemProxy
    {
        const int INTERNET_OPTION_REFRESH = 37;
        const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
        const int INTERNET_OPTION_PER_CONNECTION_OPTION = 75;
        const int INTERNET_OPTION_PROXY_SETTINGS_CHANGED = 95;

        const int INTERNET_PER_CONN_FLAGS = 1;
        const int INTERNET_PER_CONN_PROXY_SERVER = 2;
        const int INTERNET_PER_CONN_PROXY_BYPASS = 3;
        const int INTERNET_PER_CONN_AUTOCONFIG_URL = 4;

        const int PROXY_TYPE_DIRECT = 1;
        const int PROXY_TYPE_PROXY = 2;

        const string Bypass = "localhost;127.*;10.*;172.16.*;172.17.*;172.18.*;172.19.*;172.20.*;" +
            "172.21.*;172.22.*;172.23.*;172.24.*;172.25.*;172.26.*;172.27.*;172.28.*;172.29.*;" +
            "172.30.*;172.31.*;192.168.*;<local>";

        [StructLayout(LayoutKind.Sequential)]
        struct INTERNET_PER_CONN_OPTION_LIST
        {
            public int dwSize;
            public IntPtr pszConnection;
            public int dwOptionCount;
            public int dwOptionError;
            public IntPtr pOptions;
        }

        // x64 layout: DWORD option, then an 8-byte aligned union
        [StructLayout(LayoutKind.Explicit, Size = 16)]
        struct INTERNET_PER_CONN_OPTION
        {
            [FieldOffset(0)] public int dwOption;
            [FieldOffset(8)] public int dwValue;
            [FieldOffset(8)] public IntPtr pszValue;
        }

        [DllImport("wininet.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool InternetSetOptionW(IntPtr hInternet, int option, IntPtr buffer, int length);

        [DllImport("wininet.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool InternetQueryOptionW(IntPtr hInternet, int option, IntPtr buffer, ref int length);

        [DllImport("kernel32.dll")]
        static extern IntPtr GlobalFree(IntPtr mem);

        const string BackupKey = "internal_proxy_backup";

        public static void Enable(string host, int port)
        {
            if (!Settings.Contains(BackupKey))
            {
                string[] cur = Query();
                Settings.Set(BackupKey, string.Join("\u0001", cur));
                AppLog.Info("Proxy", "Saved system proxy: flags=" + cur[0] + " server=" + cur[1]);
            }
            string server = "socks=" + host + ":" + port;
            Apply(PROXY_TYPE_DIRECT | PROXY_TYPE_PROXY, server, Bypass, null);
            AppLog.Info("Proxy", "System proxy set to " + server);
        }

        public static void Restore()
        {
            string backup = Settings.Get(BackupKey, null);
            if (backup == null) return;
            string[] v = backup.Split('\u0001');
            int flags;
            if (v.Length != 4 || !int.TryParse(v[0], out flags)) flags = PROXY_TYPE_DIRECT;
            Apply(flags, Arr(v, 1), Arr(v, 2), Arr(v, 3));
            Settings.Set(BackupKey, null);
            AppLog.Info("Proxy", "System proxy restored");
        }

        public static void RecoverAfterCrash()
        {
            if (Settings.Contains(BackupKey))
            {
                AppLog.Warn("Proxy", "Restoring system proxy left over from a previous run");
                Restore();
            }
        }

        static string Arr(string[] v, int i)
        {
            return i < v.Length && v[i].Length > 0 ? v[i] : null;
        }

        static string[] Query()
        {
            int[] ids = { INTERNET_PER_CONN_FLAGS, INTERNET_PER_CONN_PROXY_SERVER,
                INTERNET_PER_CONN_PROXY_BYPASS, INTERNET_PER_CONN_AUTOCONFIG_URL };
            var result = new[] { PROXY_TYPE_DIRECT.ToString(), "", "", "" };
            int optSize = Marshal.SizeOf(typeof(INTERNET_PER_CONN_OPTION));
            IntPtr opts = Marshal.AllocHGlobal(optSize * ids.Length);
            IntPtr listPtr = IntPtr.Zero;
            try
            {
                for (int i = 0; i < ids.Length; i++)
                {
                    var o = new INTERNET_PER_CONN_OPTION();
                    o.dwOption = ids[i];
                    Marshal.StructureToPtr(o, IntPtr.Add(opts, i * optSize), false);
                }
                var list = new INTERNET_PER_CONN_OPTION_LIST();
                list.dwSize = Marshal.SizeOf(list);
                list.dwOptionCount = ids.Length;
                list.pOptions = opts;
                int size = list.dwSize;
                listPtr = Marshal.AllocHGlobal(size);
                Marshal.StructureToPtr(list, listPtr, false);
                if (!InternetQueryOptionW(IntPtr.Zero, INTERNET_OPTION_PER_CONNECTION_OPTION, listPtr, ref size))
                {
                    AppLog.Warn("Proxy", "InternetQueryOption failed: " + Marshal.GetLastWin32Error());
                    return result;
                }
                for (int i = 0; i < ids.Length; i++)
                {
                    var o = (INTERNET_PER_CONN_OPTION)Marshal.PtrToStructure(
                        IntPtr.Add(opts, i * optSize), typeof(INTERNET_PER_CONN_OPTION));
                    if (i == 0)
                    {
                        result[0] = o.dwValue.ToString();
                    }
                    else if (o.pszValue != IntPtr.Zero)
                    {
                        result[i] = Marshal.PtrToStringUni(o.pszValue) ?? "";
                        GlobalFree(o.pszValue);
                    }
                }
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(opts);
                if (listPtr != IntPtr.Zero) Marshal.FreeHGlobal(listPtr);
            }
        }

        static void Apply(int flags, string server, string bypass, string autoConfigUrl)
        {
            int optSize = Marshal.SizeOf(typeof(INTERNET_PER_CONN_OPTION));
            var strings = new[] { server, bypass, autoConfigUrl };
            int[] ids = { INTERNET_PER_CONN_PROXY_SERVER, INTERNET_PER_CONN_PROXY_BYPASS,
                INTERNET_PER_CONN_AUTOCONFIG_URL };
            IntPtr opts = Marshal.AllocHGlobal(optSize * 4);
            var allocated = new IntPtr[3];
            IntPtr listPtr = IntPtr.Zero;
            try
            {
                var f = new INTERNET_PER_CONN_OPTION();
                f.dwOption = INTERNET_PER_CONN_FLAGS;
                f.dwValue = flags;
                Marshal.StructureToPtr(f, opts, false);
                for (int i = 0; i < 3; i++)
                {
                    var o = new INTERNET_PER_CONN_OPTION();
                    o.dwOption = ids[i];
                    allocated[i] = strings[i] == null ? IntPtr.Zero : Marshal.StringToHGlobalUni(strings[i]);
                    o.pszValue = allocated[i];
                    Marshal.StructureToPtr(o, IntPtr.Add(opts, (i + 1) * optSize), false);
                }
                var list = new INTERNET_PER_CONN_OPTION_LIST();
                list.dwSize = Marshal.SizeOf(list);
                list.dwOptionCount = 4;
                list.pOptions = opts;
                listPtr = Marshal.AllocHGlobal(list.dwSize);
                Marshal.StructureToPtr(list, listPtr, false);
                if (!InternetSetOptionW(IntPtr.Zero, INTERNET_OPTION_PER_CONNECTION_OPTION, listPtr, list.dwSize))
                    AppLog.Error("Proxy", "InternetSetOption failed: " + Marshal.GetLastWin32Error());
                InternetSetOptionW(IntPtr.Zero, INTERNET_OPTION_PROXY_SETTINGS_CHANGED, IntPtr.Zero, 0);
                InternetSetOptionW(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
                InternetSetOptionW(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
            }
            finally
            {
                foreach (var p in allocated)
                    if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
                Marshal.FreeHGlobal(opts);
                if (listPtr != IntPtr.Zero) Marshal.FreeHGlobal(listPtr);
            }
        }
    }
}
