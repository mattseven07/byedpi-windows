using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ByeDPI
{
    enum AppStatus { Halted, Running }

    // Windows counterpart of ByeDpiProxyService + ByeDpiVpnService:
    //  Proxy mode: ciadpi.exe + (optionally) the Windows system proxy
    //  VPN mode:   ciadpi.exe + hev-socks5-tunnel on a Wintun adapter that
    //              captures all IPv4 (and optionally IPv6) traffic
    class ByeDpiService
    {
        public static readonly ByeDpiService Instance = new ByeDpiService();

        public const string TunName = "ByeDPI";
        const string TunGuid = "3f9a6d2e-8b41-4c1f-a5e7-0d2c6b9e7a14";
        const string TunIPv4 = "198.18.0.1";
        const string TunIPv6 = "fd00::1";
        const string NrptComment = "ByeDPI";
        const string NrptKey = "internal_nrpt_active";
        const string DnsRouteKey = "internal_dns_route";
        const string FirewallKey = "internal_fw_rule";
        const string FirewallRule = "ByeDPI tunnel - block LAN discovery";

        public AppStatus Status { get; private set; }
        public Mode RunningMode { get; private set; }
        public bool Busy { get; private set; }
        // Desync arguments of the current session, shown on the main screen
        public string ActiveArgs { get; private set; }

        public event Action StatusChanged;
        public event Action<string> Failed;

        SynchronizationContext ui;
        readonly object sync = new object();
        Process ciadpi, tunnel;
        bool stopping, proxySet;
        int outIf = -1, tunIf = -1;
        List<string> vpnArgs;
        string listenIp;
        int listenPort;
        System.Threading.Timer netTimer;

        public static string BinDir
        {
            get
            {
                string dir = Path.GetDirectoryName(typeof(ByeDpiService).Assembly.Location);
                string bin = Path.Combine(dir, "bin");
                return Directory.Exists(bin) ? bin : dir;
            }
        }

        public void Init(SynchronizationContext context)
        {
            ui = context;
            NetworkChange.NetworkAddressChanged += (s, e) =>
            {
                if (Status != AppStatus.Running || RunningMode != Mode.VPN) return;
                if (netTimer != null) netTimer.Dispose();
                netTimer = new System.Threading.Timer(_ => CheckUplink(), null, 1500, Timeout.Infinite);
            };
        }

        public void Start(Mode mode)
        {
            if (Busy || Status == AppStatus.Running) return;
            Busy = true;
            RaiseStatus();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string error = null;
                lock (sync)
                {
                    try
                    {
                        stopping = false;
                        if (mode == Mode.VPN) StartVpn();
                        else StartProxy();
                    }
                    catch (Exception e)
                    {
                        error = e.Message;
                        AppLog.Error("Service", "Failed to start " + mode + ": " + e.Message);
                        StopLocked();
                    }
                }
                Post(() =>
                {
                    Busy = false;
                    RunningMode = mode;
                    Status = error == null ? AppStatus.Running : AppStatus.Halted;
                    RaiseStatus();
                    if (error != null) RaiseFailed(mode, error);
                });
            });
        }

        public void Stop()
        {
            if (Busy || Status != AppStatus.Running) return;
            Busy = true;
            RaiseStatus();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                lock (sync) StopLocked();
                Post(() =>
                {
                    Busy = false;
                    Status = AppStatus.Halted;
                    RaiseStatus();
                });
            });
        }

        // Used when the app exits or Windows shuts down
        public void StopNow()
        {
            lock (sync) StopLocked();
            Status = AppStatus.Halted;
        }

        // ---------------- Proxy mode ----------------

        void StartProxy()
        {
            var args = ByeDpiArgs.Build();
            ActiveArgs = ByeDpiArgs.Display(args);
            ByeDpiArgs.Normalize(args, out listenIp, out listenPort);
            StartCiadpi(args);
            if (Settings.GetBool("win_system_proxy", true))
            {
                string host = ConnectHost(listenIp);
                SystemProxy.Enable(host.Contains(":") ? "[" + host + "]" : host, listenPort);
                proxySet = true;
            }
        }

        // ---------------- VPN mode ----------------

        void StartVpn()
        {
            if (!Native.IsAdmin())
                throw new Exception("administrator rights are required for VPN mode");
            CleanupLeftovers();

            string gateway;
            outIf = Native.GetDefaultRoute(-1, out gateway);
            if (outIf <= 0) throw new Exception("no network connection");

            vpnArgs = ByeDpiArgs.Build();
            ActiveArgs = ByeDpiArgs.Display(vpnArgs);
            ByeDpiArgs.Normalize(vpnArgs, out listenIp, out listenPort);
            // every connection of the PC goes through byedpi in VPN mode, and a
            // desktop keeps far more of them open than a phone
            if (ByeDpiArgs.FindOption(vpnArgs, 'c', "max-conn") == null)
            {
                vpnArgs.Add("-c");
                vpnArgs.Add("2048");
            }
            StartCiadpi(WithUplink(vpnArgs));

            bool ipv6 = Settings.GetBool("ipv6_enable", false);
            if (ipv6 && !UplinkHasIPv6(outIf))
            {
                AppLog.Warn("Service", "IPv6 is enabled but the network has no IPv6 route, tunneling IPv4 only");
                ipv6 = false;
            }
            string dns = Settings.Get("dns_ip", "1.1.1.1").Trim();
            string config = WriteTunnelConfig(ConnectHost(listenIp), listenPort, ipv6);

            string exe = Path.Combine(BinDir, "hev-socks5-tunnel.exe");
            if (!File.Exists(exe)) throw new Exception("hev-socks5-tunnel.exe not found in " + BinDir);
            AddDiscoveryBlock();
            AppLog.Info("Service", "Starting tunnel, uplink interface " + outIf);
            tunnel = StartChild(exe, ByeDpiArgs.Quote(config.Replace('\\', '/')), "tunnel");
            tunIf = WaitForAdapter(20000);

            Netsh("interface ipv4 delete route prefix=224.0.0.0/4 interface=" + tunIf + " store=active");
            Netsh("interface ipv4 delete route prefix=255.255.255.255/32 interface=" + tunIf + " store=active");
            Netsh("interface ipv4 set interface interface=" + tunIf + " metric=1");
            Netsh("interface ipv4 add route prefix=0.0.0.0/1 interface=" + tunIf + " metric=0 store=active");
            Netsh("interface ipv4 add route prefix=128.0.0.0/1 interface=" + tunIf + " metric=0 store=active");
            if (ipv6)
            {
                Netsh("interface ipv6 set interface interface=" + tunIf + " metric=1");
                Netsh("interface ipv6 add route prefix=::/1 interface=" + tunIf + " metric=0 store=active");
                Netsh("interface ipv6 add route prefix=8000::/1 interface=" + tunIf + " metric=0 store=active");
            }
            if (dns.Length > 0)
            {
                IPAddress addr;
                bool v6 = IPAddress.TryParse(dns, out addr) && addr.AddressFamily == AddressFamily.InterNetworkV6;
                Netsh("interface " + (v6 ? "ipv6" : "ipv4") + " set dnsservers name=" + tunIf +
                    " source=static address=" + dns + " register=none validate=no");
                if (!v6) AddDnsRoute(dns, outIf, gateway);
                AddNrpt(dns);
            }
            Native.FlushDns();
            AppLog.Info("Service", "VPN is up: tunnel interface " + tunIf + ", DNS " + (dns.Length > 0 ? dns : "system"));
        }

        // byedpi's own connections must leave through the real uplink,
        // the same job addDisallowedApplication() does on Android
        static List<string> WithUplink(List<string> args, int index)
        {
            var a = new List<string>(args);
            if (ByeDpiArgs.FindOption(a, 'Z', "out-if") == null)
            {
                a.Add("--out-if");
                a.Add(index.ToString());
            }
            return a;
        }

        List<string> WithUplink(List<string> args)
        {
            return WithUplink(args, outIf);
        }

        static string WriteTunnelConfig(string socksHost, int socksPort, bool ipv6)
        {
            // Same tunnel settings as ByeDpiVpnService.startTun2Socks
            var sb = new StringBuilder();
            sb.Append("tunnel:\n");
            sb.Append("  name: ").Append(TunName).Append('\n');
            sb.Append("  guid: ").Append(TunGuid).Append('\n');
            sb.Append("  mtu: 8500\n");
            sb.Append("  ipv4: ").Append(TunIPv4).Append('\n');
            if (ipv6) sb.Append("  ipv6: '").Append(TunIPv6).Append("'\n");
            sb.Append("socks5:\n");
            sb.Append("  port: ").Append(socksPort).Append('\n');
            sb.Append("  address: '").Append(socksHost).Append("'\n");
            sb.Append("  udp: 'udp'\n");
            sb.Append("misc:\n");
            sb.Append("  task-stack-size: 81920\n");
            sb.Append("  log-file: stderr\n");
            sb.Append("  log-level: warn\n");

            string dir = Path.Combine(Path.GetTempPath(), "ByeDPI");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "tunnel.yml");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            return path;
        }

        static bool UplinkHasIPv6(int index)
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                var props = ni.GetIPProperties();
                IPv4InterfaceProperties v4;
                try { v4 = props.GetIPv4Properties(); }
                catch (NetworkInformationException) { continue; }
                if (v4 == null || v4.Index != index) continue;
                return props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetworkV6) &&
                    props.UnicastAddresses.Any(u => u.Address.AddressFamily == AddressFamily.InterNetworkV6 &&
                        !u.Address.IsIPv6LinkLocal);
            }
            return false;
        }

        int WaitForAdapter(int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (tunnel.HasExited)
                    throw new Exception("tunnel exited: " + LastLines("tunnel"));
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.Name != TunName) continue;
                    var props = ni.GetIPProperties();
                    if (!props.UnicastAddresses.Any(u => u.Address.ToString() == TunIPv4)) continue;
                    var v4 = props.GetIPv4Properties();
                    if (v4 != null) return v4.Index;
                }
                Thread.Sleep(200);
            }
            throw new Exception("timed out waiting for the " + TunName + " network adapter");
        }

        void AddNrpt(string dns)
        {
            // Pins all name resolution to the chosen DNS server, otherwise Windows
            // may also ask the router's (often censored) resolver in parallel
            string cmd = "Add-DnsClientNrptRule -Namespace '.' -NameServers '" + dns +
                "' -Comment '" + NrptComment + "' -ErrorAction Stop | Out-Null";
            if (PowerShell(cmd) == 0) Settings.SetBool(NrptKey, true);
        }

        public static void RemoveNrpt(bool onlyIfMarked)
        {
            if (onlyIfMarked && !Settings.GetBool(NrptKey, false)) return;
            PowerShell("Get-DnsClientNrptRule | Where-Object { $_.Comment -eq '" + NrptComment +
                "' } | Remove-DnsClientNrptRule -Force");
            Settings.Set(NrptKey, null);
        }

        // DNS queries go straight to the DNS server through the real uplink.
        // Windows uses a new source port for every query, and tunneling each one
        // as its own SOCKS5 UDP association quickly exhausts byedpi's connection pool.
        // byedpi only relays plain DNS anyway, so the answers are the same.
        static void AddDnsRoute(string dns, int ifIndex, string gateway)
        {
            if (string.IsNullOrEmpty(gateway) || gateway == "0.0.0.0") return;
            string route = "prefix=" + dns + "/32 interface=" + ifIndex + " nexthop=" + gateway;
            Netsh("interface ipv4 add route " + route + " metric=0 store=active");
            Settings.Set(DnsRouteKey, route);
        }

        static void RemoveDnsRoute()
        {
            string route = Settings.Get(DnsRouteKey, null);
            if (route == null) return;
            Netsh("interface ipv4 delete route " + route + " store=active");
            Settings.Set(DnsRouteKey, null);
        }

        // A new network adapter receives a burst of SSDP/LLMNR/mDNS/NetBIOS discovery
        // from Windows and apps. Each packet would become its own SOCKS5 UDP association
        // and exhaust the tunnel within seconds, so discovery from the tunnel address is
        // blocked. The rule matches the fixed tunnel address and can be added before
        // the adapter exists.
        static void AddDiscoveryBlock()
        {
            RemoveDiscoveryBlock(false);
            Netsh("advfirewall firewall add rule name=\"" + FirewallRule + "\" dir=out action=block protocol=UDP " +
                "localip=" + TunIPv4 + " remoteip=224.0.0.0/4,255.255.255.255," + TunIPv4);
            Settings.SetBool(FirewallKey, true);
        }

        static void RemoveDiscoveryBlock(bool onlyIfMarked)
        {
            if (onlyIfMarked && !Settings.GetBool(FirewallKey, false)) return;
            int code;
            Run("netsh.exe", "advfirewall firewall delete rule name=\"" + FirewallRule + "\"", out code);
            Settings.Set(FirewallKey, null);
        }

        // NRPT rule, DNS host route and firewall rule survive a crash; the adapter
        // and its routes do not
        public static void CleanupLeftovers()
        {
            RemoveNrpt(true);
            RemoveDnsRoute();
            RemoveDiscoveryBlock(true);
        }

        void CheckUplink()
        {
            lock (sync)
            {
                if (tunnel == null || stopping) return;
                string gateway;
                int idx = Native.GetDefaultRoute(tunIf, out gateway);
                if (idx <= 0 || idx == outIf) return;
                AppLog.Info("Service", "Network changed, uplink interface " + outIf + " -> " + idx);
                outIf = idx;
                var old = ciadpi;
                ciadpi = null;
                Kill(old);
                try
                {
                    StartCiadpi(WithUplink(vpnArgs));
                    string dns = Settings.Get("dns_ip", "1.1.1.1").Trim();
                    IPAddress addr;
                    if (Settings.Contains(DnsRouteKey) && IPAddress.TryParse(dns, out addr) &&
                        addr.AddressFamily == AddressFamily.InterNetwork)
                    {
                        RemoveDnsRoute();
                        AddDnsRoute(dns, outIf, gateway);
                    }
                }
                catch (Exception e)
                {
                    AppLog.Error("Service", "Restart after network change failed: " + e.Message);
                    StopLocked();
                    Post(() =>
                    {
                        Status = AppStatus.Halted;
                        RaiseStatus();
                        RaiseFailed(Mode.VPN, e.Message);
                    });
                }
            }
        }

        // ---------------- common ----------------

        void StartCiadpi(List<string> args)
        {
            string exe = Path.Combine(BinDir, "ciadpi.exe");
            if (!File.Exists(exe)) throw new Exception("ciadpi.exe not found in " + BinDir);

            var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
            if (listeners.Any(l => l.Port == listenPort))
                throw new Exception("port " + listenPort + " is already in use by another program");

            string cmd = ByeDpiArgs.Join(args);
            AppLog.Info("Service", "Starting ciadpi " + cmd);
            ciadpi = StartChild(exe, cmd, "ciadpi");

            var target = new IPEndPoint(IPAddress.Parse(ConnectHost(listenIp)), listenPort);
            var sw = Stopwatch.StartNew();
            while (true)
            {
                if (ciadpi.HasExited)
                    throw new Exception("ciadpi exited: " + LastLines("ciadpi"));
                if (CanConnect(target))
                {
                    Thread.Sleep(100);
                    if (!ciadpi.HasExited) return;
                }
                if (sw.ElapsedMilliseconds > 5000)
                    throw new Exception("ciadpi is not accepting connections on " + target);
                Thread.Sleep(100);
            }
        }

        static bool CanConnect(IPEndPoint ep)
        {
            using (var s = new Socket(ep.AddressFamily, SocketType.Stream, ProtocolType.Tcp))
            {
                try
                {
                    var ar = s.BeginConnect(ep, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(300)) return false;
                    s.EndConnect(ar);
                    return true;
                }
                catch (SocketException)
                {
                    return false;
                }
            }
        }

        static string ConnectHost(string ip)
        {
            if (ip == "0.0.0.0") return "127.0.0.1";
            if (ip == "::") return "::1";
            return ip;
        }

        static string LastLines(string tag)
        {
            Thread.Sleep(200);
            string tail = AppLog.Tail(tag, 4);
            return tail.Length > 0 ? tail : "no output";
        }

        Process StartChild(string exe, string args, string tag)
        {
            var psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.WorkingDirectory = Path.GetDirectoryName(exe);
            var p = new Process();
            p.StartInfo = psi;
            p.EnableRaisingEvents = true;
            p.OutputDataReceived += (s, e) => { if (e.Data != null) AppLog.Info(tag, e.Data); };
            p.ErrorDataReceived += (s, e) => { if (e.Data != null) AppLog.Warn(tag, e.Data); };
            p.Exited += (s, e) => ThreadPool.QueueUserWorkItem(_ => OnChildExited(p, tag));
            p.Start();
            Native.AddToJob(p);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            return p;
        }

        void OnChildExited(Process p, string tag)
        {
            string reason;
            lock (sync)
            {
                if (stopping || (p != ciadpi && p != tunnel)) return;
                reason = tag + " exited unexpectedly (code " + p.ExitCode + ")";
                AppLog.Error("Service", reason);
                StopLocked();
            }
            Post(() =>
            {
                Status = AppStatus.Halted;
                RaiseStatus();
                RaiseFailed(RunningMode, reason);
            });
        }

        void StopLocked()
        {
            stopping = true;
            bool wasVpn = tunnel != null;
            if (Settings.GetBool(NrptKey, false)) RemoveNrpt(false);
            RemoveDnsRoute();
            RemoveDiscoveryBlock(true);
            // Killing the tunnel removes the Wintun adapter together with its routes and DNS
            Kill(tunnel);
            tunnel = null;
            Kill(ciadpi);
            ciadpi = null;
            if (proxySet)
            {
                SystemProxy.Restore();
                proxySet = false;
            }
            if (wasVpn) Native.FlushDns();
            tunIf = outIf = -1;
        }

        static void Kill(Process p)
        {
            if (p == null) return;
            try
            {
                if (!p.HasExited)
                {
                    p.Kill();
                    p.WaitForExit(3000);
                }
            }
            catch (Exception e)
            {
                AppLog.Warn("Service", "Kill: " + e.Message);
            }
        }

        static void Netsh(string args)
        {
            int code;
            string output = Run("netsh.exe", args, out code);
            if (code != 0) AppLog.Warn("netsh", args + " -> " + code + " " + output.Trim());
        }

        static int PowerShell(string command)
        {
            int code;
            string output = Run("powershell.exe",
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command " + ByeDpiArgs.Quote(command), out code);
            if (code != 0) AppLog.Warn("PowerShell", command + " -> " + code + " " + output.Trim());
            return code;
        }

        static string Run(string exe, string args, out int exitCode)
        {
            var psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using (var p = Process.Start(psi))
            {
                var err = p.StandardError.ReadToEndAsync();
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(30000);
                exitCode = p.HasExited ? p.ExitCode : -1;
                return output + err.Result;
            }
        }

        void Post(Action a)
        {
            if (ui != null) ui.Post(_ => a(), null);
            else a();
        }

        void RaiseStatus()
        {
            var h = StatusChanged;
            if (h != null) h();
        }

        void RaiseFailed(Mode mode, string reason)
        {
            var h = Failed;
            if (h != null) h("Failed to start " + (mode == Mode.VPN ? "VPN" : "Proxy") + ": " + reason);
        }
    }
}
