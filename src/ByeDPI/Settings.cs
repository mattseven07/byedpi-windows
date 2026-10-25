using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ByeDPI
{
    // Key/value store using the same keys as the Android SharedPreferences,
    // persisted to %APPDATA%\ByeDPI\settings.ini
    static class Settings
    {
        public static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ByeDPI");
        static readonly string FilePath = Path.Combine(Dir, "settings.ini");
        static readonly Dictionary<string, string> values = new Dictionary<string, string>();
        static readonly object sync = new object();

        public static event Action Changed;
        public static event Action<string> SaveFailed;

        public static void Load()
        {
            lock (sync)
            {
                values.Clear();
                if (!File.Exists(FilePath)) return;
                foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0 || line.StartsWith("#")) continue;
                    values[line.Substring(0, eq)] = Unescape(line.Substring(eq + 1));
                }
            }
        }

        static void Save()
        {
            var sb = new StringBuilder();
            foreach (var kv in values)
                sb.Append(kv.Key).Append('=').Append(Escape(kv.Value)).Append("\r\n");
            var encoding = new UTF8Encoding(false);
            try
            {
                Directory.CreateDirectory(Dir);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), encoding);
                try
                {
                    // don't fail the save just because attributes/ACLs can't be carried over
                    if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null, true);
                    else File.Move(tmp, FilePath);
                }
                catch (Exception e)
                {
                    AppLog.Warn("App", "Replacing settings file failed (" + e.Message + "), writing it directly");
                    File.WriteAllText(FilePath, sb.ToString(), encoding);
                    try { File.Delete(tmp); } catch (Exception) { }
                }
            }
            catch (Exception e)
            {
                AppLog.Error("App", "Cannot save settings: " + e.Message);
                var h = SaveFailed;
                if (h != null) h(e.Message);
            }
        }

        public static bool Contains(string key)
        {
            lock (sync) return values.ContainsKey(key);
        }

        public static string Get(string key, string def)
        {
            lock (sync)
            {
                string v;
                return values.TryGetValue(key, out v) ? v : def;
            }
        }

        public static bool GetBool(string key, bool def)
        {
            string v = Get(key, null);
            return v == null ? def : v == "true";
        }

        public static int GetInt(string key, int def)
        {
            int r;
            return int.TryParse(Get(key, null), out r) ? r : def;
        }

        public static void Set(string key, string value)
        {
            lock (sync)
            {
                if (value == null) values.Remove(key);
                else values[key] = value;
                Save();
            }
            var h = Changed;
            if (h != null) h();
        }

        public static void SetBool(string key, bool value)
        {
            Set(key, value ? "true" : "false");
        }

        // "Reset" in the settings menu; keeps Windows-internal state such as
        // the saved system proxy that must still be restored
        public static void Clear()
        {
            lock (sync)
            {
                var keep = new Dictionary<string, string>();
                foreach (var kv in values)
                    if (kv.Key.StartsWith("internal_")) keep[kv.Key] = kv.Value;
                values.Clear();
                foreach (var kv in keep) values[kv.Key] = kv.Value;
                Save();
            }
            var h = Changed;
            if (h != null) h();
        }

        static string Escape(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n");
        }

        static string Unescape(string s)
        {
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length)
                {
                    char n = s[++i];
                    sb.Append(n == 'n' ? '\n' : n == 'r' ? '\r' : n);
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }

    enum Mode { VPN, Proxy }

    static class Prefs
    {
        public static Mode Mode
        {
            get { return Settings.Get("byedpi_mode", "vpn") == "proxy" ? Mode.Proxy : Mode.VPN; }
        }
    }
}
