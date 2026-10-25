using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ByeDPI
{
    // Builds the ciadpi.exe argument list from settings, the same way the
    // Android app feeds byedpi: either the user's command line
    // (ByeDpiProxyCmdPreferences) or the UI editor values (jniCreateSocket)
    static class ByeDpiArgs
    {
        public const string DefaultIp = "127.0.0.1";
        public const int DefaultPort = 1080;

        // ciadpi v0.13 (Windows build) options that take a value
        const string ShortWithArg = "ipIbcxAuTKHVsdoqftlnOeMragwZ";
        static readonly string[] LongWithArg = {
            "ip", "port", "conn-ip", "buf-size", "max-conn", "debug", "auto", "cache-ttl",
            "timeout", "proto", "hosts", "pf", "split", "disorder", "oob", "disoob", "fake",
            "ttl", "fake-data", "tls-sni", "fake-offset", "oob-data", "mod-http", "tlsrec",
            "udp-fake", "def-ttl", "delay", "out-if"
        };

        public static List<string> Build()
        {
            return Settings.GetBool("byedpi_enable_cmd_settings", false)
                ? FromCommandLine(Settings.Get("byedpi_cmd_args", ""))
                : FromUi();
        }

        // ByeDpiProxyCmdPreferences.cmdToArgs: anything before the first '-'
        // (e.g. a pasted "ciadpi" or "ciadpi.exe") is dropped
        public static List<string> FromCommandLine(string cmd)
        {
            int first = cmd.IndexOf('-');
            string argsStr = (first > 0 ? cmd.Substring(first) : cmd).Trim();
            return ShellSplit(argsStr);
        }

        // Port of ArgumentsUtils.shellSplit
        public static List<string> ShellSplit(string s)
        {
            var tokens = new List<string>();
            bool escaping = false, quoting = false;
            char quoteChar = ' ';
            int lastCloseQuoteIndex = int.MinValue;
            var current = new StringBuilder();

            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (escaping)
                {
                    current.Append(c);
                    escaping = false;
                }
                else if (c == '\\' && !(quoting && quoteChar == '\''))
                {
                    escaping = true;
                }
                else if (quoting && c == quoteChar)
                {
                    quoting = false;
                    lastCloseQuoteIndex = i;
                }
                else if (!quoting && (c == '\'' || c == '"'))
                {
                    quoting = true;
                    quoteChar = c;
                }
                else if (!quoting && char.IsWhiteSpace(c))
                {
                    if (current.Length > 0 || lastCloseQuoteIndex == i - 1)
                    {
                        tokens.Add(current.ToString());
                        current = new StringBuilder();
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            if (current.Length > 0 || lastCloseQuoteIndex == s.Length - 1)
                tokens.Add(current.ToString());
            return tokens;
        }

        // Port of jniCreateSocket (native-lib.c) expressed as ciadpi options;
        // preview builds the same list without writing the hosts file
        public static List<string> FromUi(bool preview = false)
        {
            var a = new List<string>();
            string ip = Settings.Get("byedpi_proxy_ip", DefaultIp);
            a.Add("-i"); a.Add(ip);
            a.Add("-p"); a.Add(Settings.GetInt("byedpi_proxy_port", DefaultPort).ToString());
            a.Add("-c"); a.Add(Settings.GetInt("byedpi_max_connections", 512).ToString());
            a.Add("-b"); a.Add(Settings.GetInt("byedpi_buffer_size", 16384).ToString());

            int defaultTtl = Settings.GetInt("byedpi_default_ttl", 0);
            if (defaultTtl > 0) { a.Add("-g"); a.Add(defaultTtl.ToString()); }
            if (Settings.GetBool("byedpi_no_domain", false)) a.Add("-N");
            // TCP Fast Open and Drop SACK are Linux-only in byedpi v0.13

            string hostsMode = Settings.Get("byedpi_hosts_mode", "disable");
            string hosts = null;
            if (hostsMode == "blacklist") hosts = Settings.Get("byedpi_hosts_blacklist", "");
            else if (hostsMode == "whitelist") hosts = Settings.Get("byedpi_hosts_whitelist", "");
            if (hosts == null || hosts.Trim().Length == 0) hostsMode = "disable";

            if (hostsMode == "whitelist")
            {
                // First group: listed hosts, no desync; the next group applies to the rest
                a.Add("-H"); a.Add(preview ? "hosts.txt" : WriteHostsFile(hosts));
                a.Add("-An");
            }
            if (hostsMode == "blacklist")
            {
                a.Add("-H"); a.Add(preview ? "hosts.txt" : WriteHostsFile(hosts));
            }

            bool http = Settings.GetBool("byedpi_desync_http", true);
            bool https = Settings.GetBool("byedpi_desync_https", true);
            bool udp = Settings.GetBool("byedpi_desync_udp", false);
            string method = Settings.Get("byedpi_desync_method", "disorder");

            int fakeTtl = Settings.GetInt("byedpi_fake_ttl", 8);
            if (method == "fake") { a.Add("-t"); a.Add(fakeTtl.ToString()); }
            int udpFakes = Settings.GetInt("byedpi_udp_fake_count", 0);
            if (udpFakes > 0) { a.Add("-a"); a.Add(udpFakes.ToString()); }

            var proto = new List<string>();
            if (https) proto.Add("t");
            if (http) proto.Add("h");
            if (udp) proto.Add("u");
            if (proto.Count > 0) { a.Add("-K"); a.Add(string.Join(",", proto)); }

            var mod = new List<string>();
            if (Settings.GetBool("byedpi_host_mixed_case", false)) mod.Add("h");
            if (Settings.GetBool("byedpi_domain_mixed_case", false)) mod.Add("d");
            if (Settings.GetBool("byedpi_host_remove_spaces", false)) mod.Add("r");
            if (mod.Count > 0) { a.Add("-M"); a.Add(string.Join(",", mod)); }

            // Same quirk as native-lib.c: "+s" whenever any protocol is selected
            string offsetFlag = (http || https || udp) ? "+s" : "+h";

            if (method != "none")
            {
                string flag = Settings.GetBool("byedpi_split_at_host", false) ? offsetFlag : "";
                string pos = Settings.GetInt("byedpi_split_position", 1) + flag;
                switch (method)
                {
                    case "split": a.Add("-s"); break;
                    case "fake": a.Add("-f"); break;
                    case "oob": a.Add("-o"); break;
                    case "disoob": a.Add("-q"); break;
                    default: a.Add("-d"); break;
                }
                a.Add(pos);
            }

            if (Settings.GetBool("byedpi_tlsrec_enabled", false))
            {
                string flag = Settings.GetBool("byedpi_tlsrec_at_sni", false) ? offsetFlag : "";
                a.Add("-r"); a.Add(Settings.GetInt("byedpi_tlsrec_position", 0) + flag);
            }

            if (method == "fake")
            {
                a.Add("-n"); a.Add(Settings.Get("byedpi_fake_sni", "www.iana.org"));
                int fakeOffset = Settings.GetInt("byedpi_fake_offset", 0);
                if (fakeOffset > 0) { a.Add("-O"); a.Add(fakeOffset.ToString()); }
            }
            if (method == "oob" || method == "disoob")
            {
                string oob = Settings.Get("byedpi_oob_data", "a");
                a.Add("-e"); a.Add(oob.Length > 0 ? oob.Substring(0, 1) : "a");
            }
            return a;
        }

        static string WriteHostsFile(string hosts)
        {
            string dir = Path.Combine(Path.GetTempPath(), "ByeDPI");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "hosts.txt");
            File.WriteAllText(path, hosts.Trim(), new UTF8Encoding(false));
            return path;
        }

        // getopt_long-aware lookup, so "-p1080", "-p 1080", "--port=1080" and
        // "--port 1080" are all recognized and option values are never misread
        public static string FindOption(List<string> args, char shortName, string longName)
        {
            string found = null;
            for (int i = 0; i < args.Count; i++)
            {
                string arg = args[i];
                if (arg == "--") break;
                if (arg.StartsWith("--"))
                {
                    string name = arg.Substring(2), value = null;
                    int eq = name.IndexOf('=');
                    if (eq >= 0) { value = name.Substring(eq + 1); name = name.Substring(0, eq); }
                    bool takesArg = Array.IndexOf(LongWithArg, name) >= 0;
                    if (takesArg && value == null && i + 1 < args.Count) value = args[++i];
                    if (name == longName) found = value;
                }
                else if (arg.StartsWith("-") && arg.Length > 1)
                {
                    for (int j = 1; j < arg.Length; j++)
                    {
                        char c = arg[j];
                        if (ShortWithArg.IndexOf(c) < 0) continue;
                        string value = j + 1 < arg.Length ? arg.Substring(j + 1)
                            : i + 1 < args.Count ? args[++i] : null;
                        if (c == shortName) found = value;
                        break;
                    }
                }
            }
            return found;
        }

        // Effective listen address, defaulting to loopback instead of byedpi's
        // 0.0.0.0 so the proxy is never exposed to the local network by accident
        public static void Normalize(List<string> args, out string ip, out int port)
        {
            ip = FindOption(args, 'i', "ip");
            if (ip == null)
            {
                ip = DefaultIp;
                args.Insert(0, ip);
                args.Insert(0, "-i");
            }
            string p = FindOption(args, 'p', "port");
            if (p == null || !int.TryParse(p, out port)) port = DefaultPort;
        }

        // For on-screen summaries: keeps "-d 1" together when the line wraps
        public static string Display(List<string> args)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < args.Count; i++)
            {
                string a = Quote(args[i]);
                bool optWithValue = args[i].Length == 2 && args[i][0] == '-' && ShortWithArg.IndexOf(args[i][1]) >= 0;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(a);
                if (optWithValue && i + 1 < args.Count) sb.Append(' ').Append(Quote(args[++i]));
            }
            return sb.ToString();
        }

        // Windows command line quoting compatible with the CRT argv parser
        public static string Join(IEnumerable<string> args)
        {
            var sb = new StringBuilder();
            foreach (var arg in args)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(Quote(arg));
            }
            return sb.ToString();
        }

        public static string Quote(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
                return arg;
            var sb = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"') sb.Append('\\', backslashes * 2 + 1);
                else sb.Append('\\', backslashes);
                backslashes = 0;
                sb.Append(c);
            }
            sb.Append('\\', backslashes * 2);
            sb.Append('"');
            return sb.ToString();
        }
    }
}
