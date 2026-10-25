using System;
using System.Collections.Generic;
using System.Text;

namespace ByeDPI
{
    // In-memory log (app + ciadpi + tunnel output) for "Save logs"
    static class AppLog
    {
        const int MaxLines = 5000;
        static readonly LinkedList<string> lines = new LinkedList<string>();

        static void Add(string level, string tag, string msg)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + level + " " + tag + ": " + msg;
            lock (lines)
            {
                lines.AddLast(line);
                while (lines.Count > MaxLines) lines.RemoveFirst();
            }
            System.Diagnostics.Debug.WriteLine(line);
        }

        public static void Info(string tag, string msg) { Add("I", tag, msg); }
        public static void Warn(string tag, string msg) { Add("W", tag, msg); }
        public static void Error(string tag, string msg) { Add("E", tag, msg); }

        public static string Dump()
        {
            var sb = new StringBuilder();
            lock (lines)
                foreach (var l in lines) sb.Append(l).Append("\r\n");
            return sb.ToString();
        }

        public static string Tail(string tag, int count)
        {
            var result = new List<string>();
            lock (lines)
            {
                for (var n = lines.Last; n != null && result.Count < count; n = n.Previous)
                    if (n.Value.Contains(" " + tag + ": ")) result.Insert(0, n.Value.Substring(n.Value.IndexOf(": ") + 2));
            }
            return string.Join("\n", result);
        }
    }
}
