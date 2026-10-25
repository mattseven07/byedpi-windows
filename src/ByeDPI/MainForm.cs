using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ByeDPI
{
    class MainForm : Form
    {
        class Page
        {
            public string Title;
            public Control View;
            public Func<ContextMenuStrip> Menu;
        }

        readonly ByeDpiService service = ByeDpiService.Instance;
        readonly TopBar bar = new TopBar();
        readonly Panel content = new Panel();
        readonly HomeView home = new HomeView();
        readonly Toast toast = new Toast();
        readonly Stack<Page> pages = new Stack<Page>();
        readonly NotifyIcon tray = new NotifyIcon();
        readonly ToolStripMenuItem trayToggle;
        readonly bool connectOnStart, startHidden;
        bool exiting, trayHintShown;

        static readonly string[] ThemeNames = { "System", "Light", "Dark" };
        static readonly string[] ThemeValues = { "system", "light", "dark" };
        static readonly string[] ModeNames = { "VPN", "Proxy" };
        static readonly string[] ModeValues = { "vpn", "proxy" };
        static readonly string[] MethodNames = { "None", "Split", "Disorder", "Fake", "Out-of-band", "Disordered out-of-band" };
        static readonly string[] MethodValues = { "none", "split", "disorder", "fake", "oob", "disoob" };
        static readonly string[] HostsNames = { "Disable", "Blacklist", "Whitelist" };
        static readonly string[] HostsValues = { "disable", "blacklist", "whitelist" };

        // Strategies verified on Windows; fake packets (-f) depend on the TCP
        // stack re-reading a temp file and often stall on Windows 11, and
        // out-of-band data (-o/-q) is dropped by Google-hosted servers such as
        // the Discord app's update server
        static readonly string[][] Presets = {
            new[] { "Split + TLS record split", "-s1 -r1+s" },
            new[] { "TLS record split at SNI", "-r1+s" },
            new[] { "Disorder + TLS record split", "-d1 -r1+s" },
            new[] { "Disorder at SNI + TLS record split", "-d1+s -r1+s" },
        };

        public MainForm(bool connect, bool hidden)
        {
            connectOnStart = connect;
            startHidden = hidden;
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) Theme.Scale = g.DpiX / 96f;

            Text = "ByeDPI";
            AutoScaleMode = AutoScaleMode.None;
            Font = Theme.Font(10.5f);
            ClientSize = new Size(Theme.S(400), Theme.S(690));
            MinimumSize = new Size(Theme.S(340), Theme.S(480));
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            var icon = LoadIcon(0);
            if (icon != null) Icon = icon;

            content.Dock = DockStyle.Fill;
            bar.Dock = DockStyle.Top;
            Controls.Add(content);
            Controls.Add(bar);
            Controls.Add(toast);

            home.Dock = DockStyle.Fill;
            home.ButtonClicked += OnButton;
            bar.BackClicked += GoBack;
            bar.SettingsClicked += OpenSettings;
            bar.MoreClicked += () =>
            {
                var m = pages.Peek().Menu;
                if (m != null) m().Show(bar, bar.MenuAnchor, ToolStripDropDownDirection.BelowLeft);
            };

            var trayMenu = MenuRenderer.Create();
            trayToggle = MenuRenderer.Item("Connect", OnButton);
            trayMenu.Items.Add(trayToggle);
            trayMenu.Items.Add(MenuRenderer.Item("Open ByeDPI", ShowFromTray));
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(MenuRenderer.Item("Exit", ExitApp));
            tray.ContextMenuStrip = trayMenu;
            tray.Icon = LoadIcon(SystemInformation.SmallIconSize.Width) ?? Icon;
            tray.Text = "ByeDPI";
            tray.Visible = true;
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowFromTray(); };

            Push(new Page { Title = "ByeDPI", View = home, Menu = HomeMenu });

            service.Init(SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext());
            service.StatusChanged += UpdateStatus;
            service.Failed += msg => ShowToast(msg);
            Settings.Changed += UpdateStatus;
            Settings.SaveFailed += msg => ShowToast("Settings could not be saved: " + msg);
            Theme.Changed += ApplyTheme;
            SystemEvents.SessionEnding += (s, e) => service.StopNow();
            SingleInstance.ListenForShow(() => BeginInvoke((Action)ShowFromTray));

            ApplyTheme();
            UpdateStatus();
        }

        static Icon LoadIcon(int size)
        {
            var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("ByeDPI.app.ico");
            if (s == null) return null;
            using (s) return size > 0 ? new Icon(s, size, size) : new Icon(s);
        }

        protected override void SetVisibleCore(bool value)
        {
            // --tray: start without showing the window
            if (startHidden && !IsHandleCreated)
            {
                CreateHandle();
                value = false;
            }
            base.SetVisibleCore(value);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.SetDarkTitleBar(Handle, Theme.Dark);
            if (connectOnStart) BeginInvoke((Action)OnButton);
        }

        void ApplyTheme()
        {
            BackColor = Theme.Bg;
            content.BackColor = Theme.Bg;
            if (IsHandleCreated) Native.SetDarkTitleBar(Handle, Theme.Dark);
            foreach (var p in pages)
            {
                var list = p.View as PrefList;
                if (list != null) list.RefreshRows();
                p.View.Invalidate();
            }
            bar.Invalidate();
            // the title bar repaints only after a frame change
            if (Visible && WindowState == FormWindowState.Normal)
            {
                Width += 1;
                Width -= 1;
            }
        }

        // ---------------- status / start / stop ----------------

        void UpdateStatus()
        {
            if (InvokeRequired) { BeginInvoke((Action)UpdateStatus); return; }
            bool running = service.Status == AppStatus.Running;
            Mode mode = running ? service.RunningMode : Prefs.Mode;
            string ip;
            int port;
            var args = PreviewArgs();
            ByeDpiArgs.Normalize(args, out ip, out port);
            home.AddressText = ip + ":" + port;
            string strategy = running && service.ActiveArgs != null ? service.ActiveArgs
                : Settings.GetBool("byedpi_enable_cmd_settings", false) ? ByeDpiArgs.Display(PreviewArgs()) : "UI editor settings";
            home.DetailText = "Strategy: " + (strategy.Trim().Length > 0 ? strategy : "none (no desync)");

            if (mode == Mode.VPN)
            {
                home.StatusText = running ? "Connected" : "Disconnected";
                home.ButtonText = running ? "Disconnect" : "Connect";
            }
            else
            {
                home.StatusText = running ? "Proxy is up" : "Proxy is down";
                home.ButtonText = running ? "Stop" : "Start";
            }
            if (service.Busy)
                home.StatusText = running ? "Disconnecting…" : (mode == Mode.VPN ? "Connecting…" : "Starting…");
            home.ButtonEnabled = !service.Busy;
            home.Invalidate();

            trayToggle.Text = home.ButtonText;
            trayToggle.Enabled = !service.Busy;
            string t = "ByeDPI – " + home.StatusText;
            tray.Text = t.Length > 63 ? t.Substring(0, 63) : t;
        }

        static List<string> PreviewArgs()
        {
            return Settings.GetBool("byedpi_enable_cmd_settings", false)
                ? ByeDpiArgs.FromCommandLine(Settings.Get("byedpi_cmd_args", ""))
                : ByeDpiArgs.FromUi(true);
        }

        void OnButton()
        {
            if (service.Busy) return;
            if (StrategyTester.Running)
            {
                ShowFromTray();
                ShowToast("Please wait for the strategy test to finish");
                return;
            }
            if (service.Status == AppStatus.Running)
            {
                service.Stop();
                return;
            }
            Mode mode = Prefs.Mode;
            if (mode == Mode.VPN && !Native.IsAdmin())
            {
                // Windows counterpart of the Android VPN permission request
                ShowFromTray();
                bool ok = ConfirmDialog.Show(this, "VPN mode",
                    "VPN mode creates a virtual network adapter, which needs administrator rights.\n\n" +
                    "Restart ByeDPI as administrator and connect?", "Restart");
                if (ok && Program.RelaunchElevated("--connect"))
                {
                    ExitApp();
                    return;
                }
                ShowToast("VPN permission denied");
                return;
            }
            service.Start(mode);
        }

        public void ShowToast(string text)
        {
            if (InvokeRequired) { BeginInvoke((Action<string>)ShowToast, text); return; }
            if (!Visible)
            {
                tray.ShowBalloonTip(5000, "ByeDPI", text, ToolTipIcon.Warning);
                return;
            }
            toast.Show(text);
        }

        // ---------------- navigation ----------------

        void Push(Page p)
        {
            if (pages.Count > 0) content.Controls.Remove(pages.Peek().View);
            pages.Push(p);
            ShowPage();
        }

        void GoBack()
        {
            if (pages.Count <= 1) return;
            var old = pages.Pop();
            content.Controls.Remove(old.View);
            old.View.Dispose();
            ShowPage();
            UpdateStatus();
        }

        void ShowPage()
        {
            var p = pages.Peek();
            p.View.Dock = DockStyle.Fill;
            content.Controls.Add(p.View);
            bar.Title = p.Title;
            bar.ShowBack = pages.Count > 1;
            bar.ShowSettings = pages.Count == 1;
            bar.ShowMore = p.Menu != null;
            bar.Invalidate();
            var list = p.View as PrefList;
            if (list != null) list.RefreshRows();
            p.View.Focus();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            if (!Draw.KeyboardCues && (key == Keys.Tab || key == Keys.Up || key == Keys.Down))
            {
                Draw.KeyboardCues = true;
                pages.Peek().View.Invalidate();
            }
            if ((keyData == Keys.Escape || keyData == (Keys.Alt | Keys.Left) || keyData == Keys.Back) && pages.Count > 1)
            {
                GoBack();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        ContextMenuStrip HomeMenu()
        {
            var m = MenuRenderer.Create();
            m.Items.Add(MenuRenderer.Item("Settings", OpenSettings));
            m.Items.Add(MenuRenderer.Item("Save logs", SaveLogs));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(MenuRenderer.Item("Exit", ExitApp));
            return m;
        }

        void SaveLogs()
        {
            using (var d = new SaveFileDialog())
            {
                d.FileName = "byedpi.log";
                d.Filter = "Log files (*.log)|*.log|Text files (*.txt)|*.txt|All files (*.*)|*.*";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    File.WriteAllText(d.FileName, AppLog.Dump());
                }
                catch (Exception e)
                {
                    ShowToast("Failed to collect logs: " + e.Message);
                }
            }
        }

        // ---------------- settings screens ----------------

        void OpenSettings()
        {
            if (service.Status != AppStatus.Halted || service.Busy)
            {
                ShowToast("Please stop the " + (service.RunningMode == Mode.VPN ? "VPN" : "proxy") +
                    " before changing settings");
                return;
            }
            if (pages.Count > 1) return;
            Push(new Page { Title = "Settings", View = BuildMainSettings(), Menu = SettingsMenu });
        }

        ContextMenuStrip SettingsMenu()
        {
            var m = MenuRenderer.Create();
            m.Items.Add(MenuRenderer.Item("Reset", () =>
            {
                if (!ConfirmDialog.Show(this, "Reset", "Reset all settings to their defaults?", "Reset")) return;
                Settings.Clear();
                Theme.Apply("system");
                while (pages.Count > 2) GoBack();
            }));
            return m;
        }

        PrefList BuildMainSettings()
        {
            var l = new PrefList();
            Func<bool> vpn = () => Prefs.Mode == Mode.VPN;

            l.Category("General");
            l.Add(ListRow("Theme", "app_theme", "system", ThemeNames, ThemeValues, v => Theme.Apply(v)));
            l.Add(ListRow("Mode", "byedpi_mode", "vpn", ModeNames, ModeValues, null));
            l.Add(EditRow("DNS", "dns_ip", "1.1.1.1", false, IsDnsIp, vpn));
            l.Add(new PrefRow
            {
                Kind = RowKind.Switch, Title = "IPv6", Visible = vpn,
                IsChecked = () => Settings.GetBool("ipv6_enable", false),
                SetChecked = v => Settings.SetBool("ipv6_enable", v)
            });
            l.Add(new PrefRow
            {
                Kind = RowKind.Switch, Title = "Set as system proxy", Visible = () => !vpn(),
                Summary = () => "Browsers, Discord and other apps that use the Windows proxy settings",
                IsChecked = () => Settings.GetBool("win_system_proxy", true),
                SetChecked = v => Settings.SetBool("win_system_proxy", v)
            });
            l.Divider();

            Func<bool> cmd = () => Settings.GetBool("byedpi_enable_cmd_settings", false);
            l.Category("ByeDPI");
            l.Add(new PrefRow
            {
                Kind = RowKind.Switch, Title = "Use command line settings",
                IsChecked = cmd, SetChecked = v => Settings.SetBool("byedpi_enable_cmd_settings", v)
            });
            l.Add(new PrefRow
            {
                Kind = RowKind.Item, Title = "UI editor", Enabled = () => !cmd(),
                Click = () => Push(new Page { Title = "UI editor", View = BuildUiEditor() })
            });
            l.Add(new PrefRow
            {
                Kind = RowKind.Item, Title = "Command line editor", Enabled = cmd,
                Click = () => Push(new Page { Title = "Command line editor", View = BuildCmdEditor(), Menu = CmdMenu })
            });
            l.Divider();

            l.Category("About");
            l.Add(new PrefRow
            {
                Kind = RowKind.Item, Title = "Version",
                Summary = () => Program.Version + " (Windows port, byedpi 0.13)"
            });
            l.Add(new PrefRow
            {
                Kind = RowKind.Item, Title = "Source code", Icon = Theme.Glyph.Code,
                Click = () => Program.OpenUrl(Program.SourceUrl)
            });
            return l;
        }

        PrefList BuildCmdEditor()
        {
            var l = new PrefList();
            l.Add(new PrefRow
            {
                Kind = RowKind.Item, Title = "Documentation", Icon = Theme.Glyph.Docs,
                Click = () => Program.OpenUrl(Program.DocsUrl)
            });
            l.Divider();
            l.Add(EditRow("Command line arguments", "byedpi_cmd_args", "", true, null, null));
            l.Add(new PrefRow
            {
                Kind = RowKind.Item, Title = "Find a working strategy", Icon = Theme.Glyph.Search,
                Summary = () => "Try strategies against Discord and blocked sites, then pick one",
                Click = () => Push(new Page { Title = "Strategy test", View = BuildTester(), Menu = TesterMenu })
            });
            l.Add(new PrefRow
            {
                Kind = RowKind.Item, Title = "Fake packets on Windows",
                Visible = () => UsesFake(ByeDpiArgs.FromCommandLine(Settings.Get("byedpi_cmd_args", ""))),
                Summary = () => "-f / --fake often stalls connections on Windows 11. " +
                    "If sites stop loading, pick a preset from the ⋮ menu.",
                Enabled = () => false
            });
            return l;
        }

        static bool UsesFake(List<string> args)
        {
            foreach (var a in args)
                if (a == "--fake" || a.StartsWith("--fake=") || (a.StartsWith("-f") && !a.StartsWith("--"))) return true;
            return false;
        }

        ContextMenuStrip CmdMenu()
        {
            var m = MenuRenderer.Create();
            foreach (var p in Presets)
            {
                string args = p[1];
                m.Items.Add(MenuRenderer.Item("Preset: " + p[0] + "   " + args, () =>
                {
                    Settings.Set("byedpi_cmd_args", args);
                    ((PrefList)pages.Peek().View).RefreshRows();
                }));
            }
            return m;
        }

        // ---------------- strategy tester ----------------

        const string TesterSitesKey = "win_tester_sites";
        const string TesterStrategiesKey = "win_tester_strategies";

        static List<string> Lines(string key, string[] defaults)
        {
            string v = Settings.Get(key, null) ?? string.Join("\n", defaults);
            return v.Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0 && !s.StartsWith("#")).Distinct().ToList();
        }

        void EditLines(string title, string key, string[] defaults)
        {
            string v = InputDialog.Show(this, title, string.Join("\n", Lines(key, defaults)), true,
                s => s.Trim().Length > 0);
            if (v != null) Settings.Set(key, v.Trim());
        }

        ContextMenuStrip TesterMenu()
        {
            var m = MenuRenderer.Create();
            m.Items.Add(MenuRenderer.Item("Restore default sites and strategies", () =>
            {
                if (StrategyTester.Running) return;
                Settings.Set(TesterSitesKey, null);
                Settings.Set(TesterStrategiesKey, null);
                ((PrefList)pages.Peek().View).RefreshRows();
            }));
            return m;
        }

        PrefList BuildTester()
        {
            var l = new PrefList();
            StrategyTester tester = null;
            string status = null;
            Func<bool> idle = () => !StrategyTester.Running;
            Func<string> dns = () => Settings.Get("dns_ip", "1.1.1.1").Trim();

            l.Add(new PrefRow
            {
                Kind = RowKind.Item, Title = "Sites", Enabled = idle,
                Summary = () => string.Join(", ", Lines(TesterSitesKey, StrategyTester.DefaultSites)),
                Click = () => EditLines("Sites (one per line)", TesterSitesKey, StrategyTester.DefaultSites)
            });
            l.Add(new PrefRow
            {
                Kind = RowKind.Item, Title = "Strategies", Enabled = idle,
                Summary = () => Lines(TesterStrategiesKey, StrategyTester.DefaultStrategies).Count +
                    " argument sets, plus your current command line",
                Click = () => EditLines("Strategies (one per line)", TesterStrategiesKey, StrategyTester.DefaultStrategies)
            });
            var start = l.Add(new PrefRow { Kind = RowKind.Item, Title = "Start test", Icon = Theme.Glyph.Play });
            start.IconColor = () => Theme.Accent;
            start.Summary = () =>
            {
                if (tester != null && StrategyTester.Running)
                    return "Tested " + tester.Results.Count(r => r.Done) + " of " + tester.Results.Count + "…";
                return status ?? "Names are resolved with " + (dns().Length > 0 ? dns() : "the system DNS") +
                    ". The test takes up to a minute.";
            };
            l.Category("Results", () => tester != null);
            start.Click = () =>
            {
                if (StrategyTester.Running)
                {
                    if (tester != null) tester.Cancel();
                    return;
                }
                l.RemoveAll(r => r.Tag is StrategyResult);
                var t = tester = new StrategyTester();
                t.Updated += r => SafeInvoke(l, l.RefreshRows);
                t.Finished += msg => SafeInvoke(l, () =>
                {
                    start.Title = "Start test";
                    start.Icon = Theme.Glyph.Play;
                    status = msg ?? TesterSummary(t);
                    // best first: most sites loaded, then fastest; the baseline stays on top
                    var sorted = t.Results.Where(r => !r.Baseline).OrderByDescending(r => r.Ok)
                        .ThenBy(r => r.Error != null).ThenBy(r => r.Seconds).ToList();
                    l.RemoveAll(r => r.Tag is StrategyResult);
                    l.Add(ResultRow(t.Results[0]));
                    foreach (var r in sorted) l.Add(ResultRow(r));
                    l.RefreshRows();
                });
                string current = Settings.GetBool("byedpi_enable_cmd_settings", false)
                    ? Settings.Get("byedpi_cmd_args", "") : null;
                t.Start(Lines(TesterStrategiesKey, StrategyTester.DefaultStrategies), current,
                    Lines(TesterSitesKey, StrategyTester.DefaultSites), dns());
                foreach (var r in t.Results) l.Add(ResultRow(r));
                start.Title = "Stop test";
                start.Icon = Theme.Glyph.Stop;
                status = null;
            };
            l.Disposed += (s, e) => { if (tester != null) tester.Cancel(); };
            return l;
        }

        static string TesterSummary(StrategyTester t)
        {
            var baseline = t.Results[0];
            var tested = t.Results.Where(r => !r.Baseline && r.Error == null).ToList();
            int best = tested.Count > 0 ? tested.Max(r => r.Ok) : 0;
            int total = baseline.Total;
            string s;
            if (total > 0 && baseline.Ok == total)
                s = "All sites load even without bypass, so they don't seem to be blocked on this network right now.";
            else if (best == 0)
                s = "No strategy loaded any site. Add other strategies, or check the DNS setting.";
            else
                s = "Done. The best strategies load " + best + " of " + total + " sites. Select one to use it.";
            if (t.Unresolved.Count > 0) s += " Could not resolve: " + string.Join(", ", t.Unresolved) + ".";
            return s;
        }

        PrefRow ResultRow(StrategyResult r)
        {
            var row = new PrefRow
            {
                Kind = RowKind.Item, Tag = r,
                Title = r.Baseline ? "Without bypass" : r.Args,
                Summary = () => ResultText(r),
                IconOf = () => !r.Done ? (r.Running ? Theme.Glyph.Sync : Theme.Glyph.Clock)
                    : r.Baseline ? ""
                    : r.Error != null || r.Ok == 0 ? Theme.Glyph.Cancel
                    : r.Ok == r.Total ? Theme.Glyph.Check : Theme.Glyph.Warning,
                IconColor = () => !r.Done || r.Baseline ? Theme.TextSecondary
                    : r.Error != null || r.Ok == 0 ? Theme.Bad
                    : r.Ok == r.Total ? Theme.Good : Theme.Warn
            };
            if (!r.Baseline) row.Click = () => UseStrategy(r);
            return row;
        }

        static string ResultText(StrategyResult r)
        {
            if (r.Running) return "Testing…";
            if (!r.Done) return "Waiting";
            if (r.Error != null) return "Could not start: " + r.Error;
            string s = r.Ok + " of " + r.Total + " sites";
            if (r.Baseline) s += " load without bypass";
            else if (r.Ok > 0) s += " · " + r.Seconds.ToString("0.0") + " s";
            if (r.Current) s += " · current";
            if (r.Failed.Count > 0) s += "\n" + (r.Baseline ? "Blocked: " : "Failed: ") + string.Join(", ", r.Failed);
            return s;
        }

        void UseStrategy(StrategyResult r)
        {
            if (!r.Done || r.Error != null) return;
            string msg = (r.Ok < r.Total ? "This strategy loaded " + r.Ok + " of " + r.Total + " sites.\n\n" : "") +
                "Use these command line arguments?\n\n" + r.Args +
                (Settings.GetBool("byedpi_enable_cmd_settings", false) ? "" : "\n\nCommand line settings will be turned on.");
            if (!ConfirmDialog.Show(this, "Use strategy", msg, "Use")) return;
            Settings.Set("byedpi_cmd_args", r.Args);
            Settings.SetBool("byedpi_enable_cmd_settings", true);
            GoBack();
            ShowToast("Command line arguments saved");
        }

        static void SafeInvoke(Control c, Action a)
        {
            if (c.IsDisposed || !c.IsHandleCreated) return;
            try
            {
                c.BeginInvoke(a);
            }
            catch (InvalidOperationException)
            {
            }
        }

        PrefList BuildUiEditor()
        {
            var l = new PrefList();
            Func<string> method = () => Settings.Get("byedpi_desync_method", "disorder");
            Func<string> hostsMode = () => Settings.Get("byedpi_hosts_mode", "disable");
            Func<bool> http = () => Settings.GetBool("byedpi_desync_http", true);
            Func<bool> https = () => Settings.GetBool("byedpi_desync_https", true);
            Func<bool> udp = () => Settings.GetBool("byedpi_desync_udp", false);
            Func<bool> all = () => !http() && !https() && !udp();
            Func<bool> tlsrec = () => (all() || https()) && Settings.GetBool("byedpi_tlsrec_enabled", false);

            l.Add(new PrefRow
            {
                Kind = RowKind.Item, Title = "What does all of this mean?", Icon = Theme.Glyph.Docs,
                Click = () => Program.OpenUrl(Program.DocsUrl)
            });

            l.Category("Proxy");
            l.Add(EditRow("Listen address", "byedpi_proxy_ip", "127.0.0.1", false, IsIp, null));
            l.Add(EditRow("Port", "byedpi_proxy_port", "1080", false, IntIn(1, 65535), null));
            l.Add(EditRow("Maximum number of connections", "byedpi_max_connections", "512", false, IntIn(1, short.MaxValue), null));
            l.Add(EditRow("Buffer size", "byedpi_buffer_size", "16384", false, IntIn(1, int.MaxValue / 4), null));
            l.Add(CheckRow("No domain", "byedpi_no_domain", false, null));
            l.Add(Unsupported("TCP Fast Open"));

            l.Category("Desync");
            l.Add(ListRow("Hosts", "byedpi_hosts_mode", "disable", HostsNames, HostsValues, null));
            l.Add(EditRow("Hosts blacklist", "byedpi_hosts_blacklist", "", true, null, () => hostsMode() == "blacklist"));
            l.Add(EditRow("Hosts whitelist", "byedpi_hosts_whitelist", "", true, null, () => hostsMode() == "whitelist"));
            l.Add(EditRow("Default TTL", "byedpi_default_ttl", "0", false, IntIn(0, 255), null));
            l.Add(ListRow("Desync method", "byedpi_desync_method", "disorder", MethodNames, MethodValues, null));
            l.Add(EditRow("Split position", "byedpi_split_position", "1", false, IntIn(int.MinValue, int.MaxValue), () => method() != "none"));
            l.Add(CheckRow("Split at host", "byedpi_split_at_host", false, null, () => method() != "none"));
            l.Add(Unsupported("Drop SACK"));
            l.Add(EditRow("TTL of fake packets", "byedpi_fake_ttl", "8", false, IntIn(1, 255), () => method() == "fake"));
            l.Add(EditRow("Fake offset", "byedpi_fake_offset", "0", false, IntIn(0, int.MaxValue), () => method() == "fake"));
            l.Add(EditRow("SNI of fake packet", "byedpi_fake_sni", "www.iana.org", false, null, () => method() == "fake"));
            l.Add(EditRow("OOB Data", "byedpi_oob_data", "a", false, v => v.Length == 1,
                () => method() == "oob" || method() == "disoob"));

            l.Add(new PrefRow { Kind = RowKind.Category, Title = "Protocols" });
            l.Add(new PrefRow
            {
                Kind = RowKind.Item, Title = "Uncheck all to desync all traffic", Enabled = () => false
            });
            l.Add(CheckRow("Desync HTTP", "byedpi_desync_http", true, null));
            l.Add(CheckRow("Desync HTTPS", "byedpi_desync_https", true, null));
            l.Add(CheckRow("Desync UDP", "byedpi_desync_udp", false, null));

            Func<bool> httpOn = () => all() || http();
            l.Category("HTTP");
            l.Add(CheckRow("Host mixed case", "byedpi_host_mixed_case", false, httpOn));
            l.Add(CheckRow("Domain mixed case", "byedpi_domain_mixed_case", false, httpOn));
            l.Add(CheckRow("Host remove spaces", "byedpi_host_remove_spaces", false, httpOn));

            l.Category("HTTPS");
            l.Add(CheckRow("Split TLS record", "byedpi_tlsrec_enabled", false, () => all() || https()));
            var pos = EditRow("TLS record split position", "byedpi_tlsrec_position", "0", false,
                IntIn(2 * short.MinValue, 2 * short.MaxValue), null);
            pos.Enabled = tlsrec;
            l.Add(pos);
            l.Add(CheckRow("Split TLS record at SNI", "byedpi_tlsrec_at_sni", false, tlsrec));

            l.Category("UDP");
            var fakes = EditRow("UDP fake count", "byedpi_udp_fake_count", "0", false, IntIn(0, int.MaxValue), null);
            fakes.Enabled = () => all() || udp();
            l.Add(fakes);

            l.Divider();
            l.Add(new PrefRow
            {
                Kind = RowKind.Item, Title = "Resulting command line",
                Summary = () => ByeDpiArgs.Display(ByeDpiArgs.FromUi(true)),
                Click = () =>
                {
                    Clipboard.SetText(ByeDpiArgs.Join(ByeDpiArgs.FromUi(true)));
                    ShowToast("Copied to clipboard");
                }
            });
            return l;
        }

        // ---------------- row factories ----------------

        PrefRow ListRow(string title, string key, string def, string[] names, string[] values, Action<string> after)
        {
            Func<int> index = () => Math.Max(0, Array.IndexOf(values, Settings.Get(key, def)));
            return new PrefRow
            {
                Kind = RowKind.Item, Title = title,
                Summary = () => names[index()],
                Click = () =>
                {
                    int i = ChoiceDialog.Show(this, title, names, index());
                    if (i < 0) return;
                    Settings.Set(key, values[i]);
                    if (after != null) after(values[i]);
                }
            };
        }

        PrefRow EditRow(string title, string key, string def, bool multiline, Func<string, bool> validate, Func<bool> visible)
        {
            return new PrefRow
            {
                Kind = RowKind.Item, Title = title,
                Visible = visible ?? (() => true),
                Summary = () =>
                {
                    string v = Settings.Get(key, def);
                    return v.Trim().Length == 0 ? "Not set" : v;
                },
                Click = () =>
                {
                    string v = InputDialog.Show(this, title, Settings.Get(key, def), multiline, validate);
                    if (v != null) Settings.Set(key, v);
                }
            };
        }

        static PrefRow CheckRow(string title, string key, bool def, Func<bool> enabled, Func<bool> visible = null)
        {
            return new PrefRow
            {
                Kind = RowKind.Check, Title = title,
                Enabled = enabled ?? (() => true),
                Visible = visible ?? (() => true),
                IsChecked = () => Settings.GetBool(key, def),
                SetChecked = v => Settings.SetBool(key, v)
            };
        }

        static PrefRow Unsupported(string title)
        {
            return new PrefRow
            {
                Kind = RowKind.Check, Title = title, Summary = () => "Linux only, not available on Windows",
                Enabled = () => false, IsChecked = () => false
            };
        }

        static Func<string, bool> IntIn(int min, int max)
        {
            return v =>
            {
                long n;
                return long.TryParse(v, out n) && n >= min && n <= max;
            };
        }

        static bool IsIp(string v)
        {
            IPAddress a;
            return IPAddress.TryParse(v, out a);
        }

        // checkNotLocalIp: empty is allowed and means "use the system DNS"
        static bool IsDnsIp(string v)
        {
            if (v.Length == 0) return true;
            IPAddress a;
            if (!IPAddress.TryParse(v, out a)) return false;
            return !IPAddress.IsLoopback(a) && !a.Equals(IPAddress.Any) && !a.Equals(IPAddress.IPv6Any);
        }

        // ---------------- window / tray ----------------

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing && service.Status == AppStatus.Running)
            {
                // keep running in the tray, like the Android foreground service
                e.Cancel = true;
                Hide();
                if (!trayHintShown)
                {
                    trayHintShown = true;
                    tray.ShowBalloonTip(3000, "ByeDPI", "ByeDPI is still running. Use the tray icon to open or exit.",
                        ToolTipIcon.Info);
                }
                return;
            }
            exiting = true;
            service.StopNow();
            tray.Visible = false;
            base.OnFormClosing(e);
        }

        void ShowFromTray()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        void ExitApp()
        {
            exiting = true;
            Close();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) tray.Dispose();
            base.Dispose(disposing);
        }
    }

    class ConfirmDialog : ThemedDialog
    {
        ConfirmDialog(string title, string message, string ok)
            : base(title)
        {
            var label = new Label { Text = message, Dock = DockStyle.Fill, ForeColor = Theme.Text, Font = Theme.Font(10.5f) };
            var header = new Label { Text = title, Dock = DockStyle.Top, Height = Theme.S(36), Font = Theme.Font(13f), ForeColor = Theme.Text };
            int h = TextRenderer.MeasureText(message, label.Font, new Size(Theme.S(360), int.MaxValue), TextFormatFlags.WordBreak).Height;
            ClientSize = new Size(Theme.S(400), h + Theme.S(120));
            Controls.Add(label);
            Controls.Add(header);
            label.BringToFront();
            var okButton = AddButton(ok, DialogResult.OK);
            AddButton("Cancel", DialogResult.Cancel);
            Shown += (s, e) => okButton.Focus();
        }

        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (keyData == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); return true; }
            return base.ProcessDialogKey(keyData);
        }

        public static bool Show(IWin32Window owner, string title, string message, string ok)
        {
            using (var d = new ConfirmDialog(title, message, ok)) return d.ShowDialog(owner) == DialogResult.OK;
        }
    }
}
