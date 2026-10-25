using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;

namespace ByeDPI
{
    static class Native
    {
        public static bool IsAdmin()
        {
            using (var id = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }

        // ---- Job object: child processes die together with the app ----

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr CreateJobObject(IntPtr attributes, string name);

        [DllImport("kernel32.dll")]
        static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
        const int JobObjectExtendedLimitInformation = 9;
        static IntPtr job = IntPtr.Zero;
        static readonly object jobLock = new object();

        public static void AddToJob(Process p)
        {
            lock (jobLock)
            {
                if (job == IntPtr.Zero)
                {
                    job = CreateJobObject(IntPtr.Zero, null);
                    var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                    info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                    int len = Marshal.SizeOf(info);
                    IntPtr ptr = Marshal.AllocHGlobal(len);
                    try
                    {
                        Marshal.StructureToPtr(info, ptr, false);
                        SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)len);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(ptr);
                    }
                }
                if (!AssignProcessToJobObject(job, p.Handle))
                    AppLog.Warn("App", "AssignProcessToJobObject failed: " + Marshal.GetLastWin32Error());
            }
        }

        // ---- Routing table ----

        [DllImport("iphlpapi.dll")]
        static extern int GetIpForwardTable(IntPtr table, ref int size, bool order);

        // Interface of the lowest-metric IPv4 default route, i.e. the real
        // uplink that byedpi must use so its traffic does not loop into the tunnel
        public static int GetDefaultRouteInterface(int excludeIndex)
        {
            string gateway;
            return GetDefaultRoute(excludeIndex, out gateway);
        }

        public static int GetDefaultRoute(int excludeIndex, out string gateway)
        {
            gateway = null;
            int size = 0;
            GetIpForwardTable(IntPtr.Zero, ref size, true);
            if (size <= 0) return -1;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                if (GetIpForwardTable(buf, ref size, true) != 0) return -1;
                int n = Marshal.ReadInt32(buf);
                int best = -1;
                uint bestMetric = uint.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    // MIB_IPFORWARDROW: 14 DWORDs
                    IntPtr row = IntPtr.Add(buf, 4 + i * 56);
                    uint dest = (uint)Marshal.ReadInt32(row, 0);
                    uint mask = (uint)Marshal.ReadInt32(row, 4);
                    int ifIndex = Marshal.ReadInt32(row, 16);
                    uint metric = (uint)Marshal.ReadInt32(row, 36);
                    if (dest != 0 || mask != 0 || ifIndex == excludeIndex) continue;
                    if (metric < bestMetric)
                    {
                        bestMetric = metric;
                        best = ifIndex;
                        gateway = new System.Net.IPAddress((long)(uint)Marshal.ReadInt32(row, 12)).ToString();
                    }
                }
                return best;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        [DllImport("dnsapi.dll")]
        static extern bool DnsFlushResolverCache();

        public static void FlushDns()
        {
            try
            {
                DnsFlushResolverCache();
            }
            catch (Exception e)
            {
                AppLog.Warn("App", "DnsFlushResolverCache: " + e.Message);
            }
        }

        // ---- Named objects reachable from both elevated and normal instances ----

        [StructLayout(LayoutKind.Sequential)]
        struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            public bool bInheritHandle;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
            string sddl, uint revision, out IntPtr sd, IntPtr size);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateMutexW(ref SECURITY_ATTRIBUTES sa, bool initialOwner, string name);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateEventW(ref SECURITY_ATTRIBUTES sa, bool manualReset, bool initialState, string name);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr OpenEventW(uint access, bool inherit, string name);

        [DllImport("kernel32.dll")]
        public static extern bool SetEvent(IntPtr handle);

        [DllImport("kernel32.dll")]
        public static extern uint WaitForSingleObject(IntPtr handle, uint ms);

        [DllImport("kernel32.dll")]
        public static extern bool ReleaseMutex(IntPtr handle);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll")]
        static extern IntPtr LocalFree(IntPtr mem);

        // Everyone, any integrity level: lets a normal instance signal an elevated one
        const string OpenSddl = "D:(A;;GA;;;WD)S:(ML;;NW;;;LW)";

        static T WithOpenSecurity<T>(Func<SECURITY_ATTRIBUTES, T> create)
        {
            IntPtr sd;
            ConvertStringSecurityDescriptorToSecurityDescriptorW(OpenSddl, 1, out sd, IntPtr.Zero);
            try
            {
                var sa = new SECURITY_ATTRIBUTES();
                sa.nLength = Marshal.SizeOf(sa);
                sa.lpSecurityDescriptor = sd;
                return create(sa);
            }
            finally
            {
                if (sd != IntPtr.Zero) LocalFree(sd);
            }
        }

        public static IntPtr CreateOpenMutex(string name)
        {
            return WithOpenSecurity(sa => CreateMutexW(ref sa, false, name));
        }

        public static IntPtr CreateOpenEvent(string name)
        {
            return WithOpenSecurity(sa => CreateEventW(ref sa, false, false, name));
        }

        // ---- Dark title bar ----

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public static void SetDarkTitleBar(IntPtr hwnd, bool dark)
        {
            try
            {
                int v = dark ? 1 : 0;
                // DWMWA_USE_IMMERSIVE_DARK_MODE (20), older Windows 10 builds used 19
                if (DwmSetWindowAttribute(hwnd, 20, ref v, 4) != 0)
                    DwmSetWindowAttribute(hwnd, 19, ref v, 4);
            }
            catch (Exception)
            {
            }
        }
    }

    static class SingleInstance
    {
        const string MutexName = @"Local\ByeDPI-Windows-Instance";
        const string EventName = @"Local\ByeDPI-Windows-Show";
        const uint EVENT_MODIFY_STATE = 0x0002;
        static IntPtr mutex = IntPtr.Zero;
        static IntPtr showEvent = IntPtr.Zero;

        public static bool Acquire(int waitMs)
        {
            mutex = Native.CreateOpenMutex(MutexName);
            if (mutex == IntPtr.Zero) return true;
            uint r = Native.WaitForSingleObject(mutex, (uint)waitMs);
            // WAIT_OBJECT_0 or WAIT_ABANDONED (previous instance crashed)
            if (r == 0 || r == 0x80) return true;
            Native.CloseHandle(mutex);
            mutex = IntPtr.Zero;
            return false;
        }

        public static void Release()
        {
            if (mutex != IntPtr.Zero)
            {
                Native.ReleaseMutex(mutex);
                Native.CloseHandle(mutex);
                mutex = IntPtr.Zero;
            }
        }

        public static void SignalExisting()
        {
            IntPtr e = Native.OpenEventW(EVENT_MODIFY_STATE, false, EventName);
            if (e == IntPtr.Zero) return;
            Native.SetEvent(e);
            Native.CloseHandle(e);
        }

        // Runs onShow (on a worker thread) whenever another instance is launched
        public static void ListenForShow(Action onShow)
        {
            showEvent = Native.CreateOpenEvent(EventName);
            if (showEvent == IntPtr.Zero) return;
            var t = new Thread(() =>
            {
                while (true)
                {
                    if (Native.WaitForSingleObject(showEvent, 0xFFFFFFFF) != 0) return;
                    onShow();
                }
            });
            t.IsBackground = true;
            t.Start();
        }
    }
}
