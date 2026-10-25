using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;

namespace ByeDPI
{
    class StrategyResult
    {
        public string Args;
        public bool Baseline, Current, Running, Done;
        public int Ok, Total;
        public double Seconds;
        public string Error;
        public readonly List<string> Failed = new List<string>();
    }

    // Tries byedpi argument sets against real sites, each through its own
    // ciadpi instance, and reports which sites complete a TLS handshake and
    // answer HTTP. Names are resolved with the configured DNS server, so the
    // ISP's DNS block pages do not hide the DPI result.
    class StrategyTester
    {
        // stable.dl2.discordapp.net is the Discord app's update server. It is hosted on
        // Google Cloud, which drops out-of-band (-o/-q) data that Cloudflare accepts
        public static readonly string[] DefaultSites = {
            "discord.com", "gateway.discord.gg", "discord.gg", "stable.dl2.discordapp.net",
            "www.roblox.com", "www.wattpad.com"
        };

        // Fake packets (-f) are left out: they stall on Windows
        public static readonly string[] DefaultStrategies = {
            "-s1 -r1+s", "-r1+s", "-d1 -r1+s", "-d2 -r1+s", "-d1+s -r1+s",
            "-s1 -At -d2 -At -r1+s -An", "-o1", "-q1", "-o1 -r1+s", "-d1", "-s1"
        };

        const int SiteTimeoutMs = 6000;
        const int MaxParallel = 3;
        const SslProtocols Tls12 = (SslProtocols)3072;
        const SslProtocols Tls13 = (SslProtocols)12288;

        public static volatile bool Running;

        public readonly List<StrategyResult> Results = new List<StrategyResult>();
        public readonly List<string> Unresolved = new List<string>();
        public event Action<StrategyResult> Updated;
        public event Action<string> Finished;

        readonly CancellationTokenSource cts = new CancellationTokenSource();
        readonly List<Process> active = new List<Process>();

        public void Start(List<string> strategies, string current, List<string> sites, string dns)
        {
            Running = true;
            Results.Add(new StrategyResult { Args = "", Baseline = true });
            if (!string.IsNullOrWhiteSpace(current) && !strategies.Contains(current.Trim()))
                Results.Add(new StrategyResult { Args = current.Trim(), Current = true });
            foreach (var s in strategies)
                Results.Add(new StrategyResult { Args = s, Current = current != null && s == current.Trim() });
            var t = new Thread(() => Run(sites, dns));
            t.IsBackground = true;
            t.Start();
        }

        public void Cancel()
        {
            cts.Cancel();
            lock (active)
                foreach (var p in active) Kill(p);
        }

        void Run(List<string> sites, string dns)
        {
            string message = null;
            try
            {
                var targets = new List<KeyValuePair<string, IPAddress>>();
                foreach (var site in sites)
                {
                    IPAddress ip = Resolve(site, dns);
                    if (ip != null) targets.Add(new KeyValuePair<string, IPAddress>(site, ip));
                    else Unresolved.Add(site);
                }
                AppLog.Info("Tester", "Testing " + (Results.Count - 1) + " strategies on " +
                    string.Join(", ", targets.Select(t => t.Key + "=" + t.Value)));
                if (targets.Count == 0)
                    throw new Exception("none of the sites could be resolved, check the DNS setting and your connection");

                var queue = new Queue<StrategyResult>(Results);
                var workers = new List<Thread>();
                for (int i = 0; i < MaxParallel; i++)
                {
                    var w = new Thread(() =>
                    {
                        while (!cts.IsCancellationRequested)
                        {
                            StrategyResult r;
                            lock (queue)
                            {
                                if (queue.Count == 0) return;
                                r = queue.Dequeue();
                            }
                            TestStrategy(r, targets);
                        }
                    });
                    w.IsBackground = true;
                    w.Start();
                    workers.Add(w);
                }
                foreach (var w in workers) w.Join();
                if (cts.IsCancellationRequested) message = "Test stopped";
            }
            catch (Exception e)
            {
                message = "Test failed: " + e.Message;
                AppLog.Error("Tester", e.Message);
            }
            finally
            {
                Running = false;
            }
            var h = Finished;
            if (h != null) h(message);
        }

        void TestStrategy(StrategyResult r, List<KeyValuePair<string, IPAddress>> targets)
        {
            r.Running = true;
            Raise(r);
            Process p = null;
            try
            {
                int port = FreePort();
                var args = r.Baseline ? new List<string>() : ByeDpiArgs.FromCommandLine(r.Args);
                // getopt keeps the last -i/-p, so these override any in the strategy
                args.AddRange(new[] { "-i", "127.0.0.1", "-p", port.ToString() });
                var stderr = new StringBuilder();
                p = StartCiadpi(args, stderr);
                if (!WaitListening(p, port))
                {
                    string err = stderr.ToString().Trim();
                    r.Error = err.Length > 0 ? err.Split('\n')[0].Trim() : "ciadpi did not start";
                    return;
                }
                r.Total = targets.Count;
                var times = new List<double>();
                var threads = targets.Select(t => new Thread(() =>
                {
                    var sw = Stopwatch.StartNew();
                    string why = TestSite(port, t.Key, t.Value, cts.Token);
                    lock (r)
                    {
                        if (why == null) { r.Ok++; times.Add(sw.Elapsed.TotalSeconds); }
                        else r.Failed.Add(t.Key + " (" + why + ")");
                    }
                })).ToList();
                foreach (var t in threads) { t.IsBackground = true; t.Start(); }
                foreach (var t in threads) t.Join();
                r.Seconds = times.Count > 0 ? times.Average() : 0;
                r.Failed.Sort();
            }
            catch (Exception e)
            {
                r.Error = e.Message;
            }
            finally
            {
                if (p != null)
                {
                    Kill(p);
                    lock (active) active.Remove(p);
                }
                r.Running = false;
                r.Done = true;
                AppLog.Info("Tester", (r.Baseline ? "(no desync)" : r.Args) + ": " + r.Ok + "/" + r.Total +
                    (r.Error != null ? " error " + r.Error : "") +
                    (r.Failed.Count > 0 ? " failed " + string.Join(", ", r.Failed) : ""));
                Raise(r);
            }
        }

        Process StartCiadpi(List<string> args, StringBuilder stderr)
        {
            string exe = Path.Combine(ByeDpiService.BinDir, "ciadpi.exe");
            var psi = new ProcessStartInfo(exe, ByeDpiArgs.Join(args));
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            var p = new Process();
            p.StartInfo = psi;
            p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };
            p.OutputDataReceived += (s, e) => { };
            p.Start();
            Native.AddToJob(p);
            p.BeginErrorReadLine();
            p.BeginOutputReadLine();
            lock (active) active.Add(p);
            if (cts.IsCancellationRequested) Kill(p);
            return p;
        }

        static bool WaitListening(Process p, int port)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 3000)
            {
                if (p.HasExited)
                {
                    Thread.Sleep(100);
                    return false;
                }
                try
                {
                    using (var c = new TcpClient())
                    {
                        c.Connect(IPAddress.Loopback, port);
                        return true;
                    }
                }
                catch (SocketException)
                {
                    Thread.Sleep(100);
                }
            }
            return false;
        }

        // SOCKS5 CONNECT to the site's IP, TLS handshake with its name as SNI
        // (what DPI inspects), certificate validation, then one HTTP request
        static string TestSite(int port, string host, IPAddress ip, CancellationToken ct)
        {
            var tcp = new TcpClient();
            bool timedOut = false;
            string stage = "proxy";
            using (new Timer(_ => { timedOut = true; tcp.Close(); }, null, SiteTimeoutMs, Timeout.Infinite))
            using (ct.Register(() => tcp.Close()))
            {
                try
                {
                    tcp.Connect(IPAddress.Loopback, port);
                    var s = tcp.GetStream();
                    var buf = new byte[512];
                    s.Write(new byte[] { 5, 1, 0 }, 0, 3);
                    ReadExact(s, buf, 2);
                    var req = new byte[10];
                    req[0] = 5; req[1] = 1; req[3] = 1;
                    Array.Copy(ip.GetAddressBytes(), 0, req, 4, 4);
                    req[8] = 443 >> 8; req[9] = 443 & 0xFF;
                    s.Write(req, 0, req.Length);
                    ReadExact(s, buf, 10);
                    if (buf[1] != 0) return "no route";

                    stage = "TLS";
                    using (var ssl = new SslStream(s, false))
                    {
                        ssl.AuthenticateAsClient(host, null, Tls12 | Tls13, false);
                        stage = "HTTP";
                        var http = Encoding.ASCII.GetBytes("HEAD / HTTP/1.1\r\nHost: " + host +
                            "\r\nUser-Agent: Mozilla/5.0\r\nAccept: */*\r\nConnection: close\r\n\r\n");
                        ssl.Write(http);
                        ssl.Flush();
                        int n = ssl.Read(buf, 0, buf.Length);
                        return n >= 5 && Encoding.ASCII.GetString(buf, 0, 5) == "HTTP/" ? null : "no reply";
                    }
                }
                catch (AuthenticationException e)
                {
                    AppLog.Warn("Tester", host + ": " + e.Message);
                    return e.Message.Contains("certificate") ? "bad certificate" : "TLS error";
                }
                catch (Exception e)
                {
                    if (!timedOut && !ct.IsCancellationRequested)
                        AppLog.Warn("Tester", host + " (" + stage + "): " + e.GetType().Name + ": " +
                            (e.InnerException != null ? e.InnerException.Message : e.Message));
                    if (timedOut) return "timeout";
                    if (ct.IsCancellationRequested) return "stopped";
                    var se = (e as SocketException) ?? (e.InnerException as SocketException);
                    if (se != null && se.SocketErrorCode == SocketError.ConnectionReset) return "reset";
                    // DPI cutting the handshake shows up as the connection closing mid-TLS
                    return stage == "TLS" ? "blocked" : stage == "HTTP" ? "no reply" : "proxy error";
                }
                finally
                {
                    tcp.Close();
                }
            }
        }

        static void ReadExact(Stream s, byte[] buf, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = s.Read(buf, got, count - got);
                if (n <= 0) throw new IOException("connection closed");
                got += n;
            }
        }

        static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        static void Kill(Process p)
        {
            try
            {
                if (!p.HasExited) p.Kill();
            }
            catch (Exception)
            {
            }
        }

        void Raise(StrategyResult r)
        {
            var h = Updated;
            if (h != null) h(r);
        }

        // ---- DNS: one A query over UDP, or the system resolver if no server is set ----

        public static IPAddress Resolve(string host, string server)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(server))
                    return Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                var srv = IPAddress.Parse(server.Trim());
                var rnd = new Random();
                int id = rnd.Next(1, 65535);
                var q = new List<byte> { (byte)(id >> 8), (byte)id, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
                foreach (var label in host.TrimEnd('.').Split('.'))
                {
                    q.Add((byte)label.Length);
                    q.AddRange(Encoding.ASCII.GetBytes(label));
                }
                q.AddRange(new byte[] { 0, 0, 1, 0, 1 });
                using (var u = new UdpClient(srv.AddressFamily))
                {
                    u.Client.ReceiveTimeout = 2500;
                    for (int attempt = 0; attempt < 2; attempt++)
                    {
                        u.Send(q.ToArray(), q.Count, new IPEndPoint(srv, 53));
                        try
                        {
                            var from = new IPEndPoint(IPAddress.Any, 0);
                            var r = u.Receive(ref from);
                            if (r.Length < 12 || ((r[0] << 8) | r[1]) != id) continue;
                            return ParseA(r);
                        }
                        catch (SocketException)
                        {
                        }
                    }
                }
            }
            catch (Exception e)
            {
                AppLog.Warn("Tester", "Resolve " + host + ": " + e.Message);
            }
            return null;
        }

        static IPAddress ParseA(byte[] r)
        {
            int qd = (r[4] << 8) | r[5], an = (r[6] << 8) | r[7], pos = 12;
            for (int i = 0; i < qd; i++) pos = SkipName(r, pos) + 4;
            for (int i = 0; i < an && pos + 10 <= r.Length; i++)
            {
                pos = SkipName(r, pos);
                int type = (r[pos] << 8) | r[pos + 1];
                int len = (r[pos + 8] << 8) | r[pos + 9];
                pos += 10;
                if (type == 1 && len == 4 && pos + 4 <= r.Length)
                    return new IPAddress(new[] { r[pos], r[pos + 1], r[pos + 2], r[pos + 3] });
                pos += len;
            }
            return null;
        }

        static int SkipName(byte[] r, int pos)
        {
            while (pos < r.Length)
            {
                byte l = r[pos];
                if (l == 0) return pos + 1;
                if ((l & 0xC0) == 0xC0) return pos + 2;
                pos += l + 1;
            }
            return pos;
        }
    }
}
