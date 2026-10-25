// Checks that ByeDPI's Settings.Set persists to settings.ini.
// Usage: SettingsTest.exe <ByeDPI.exe>
using System;
using System.IO;
using System.Reflection;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

static class SettingsTest
{
    const BindingFlags Any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    static int Main(string[] args)
    {
        var asm = Assembly.LoadFrom(args[0]);
        var s = asm.GetType("ByeDPI.Settings");
        string file = Path.Combine((string)s.GetField("Dir", Any).GetValue(null), "settings.ini");
        Console.WriteLine("file: " + file + "  before: " + File.GetLastWriteTime(file).ToString("HH:mm:ss.fff"));
        s.GetMethod("Load", Any).Invoke(null, null);
        s.GetMethod("Set", Any).Invoke(null, new object[] { "zz_save_test", "1" });
        Console.WriteLine("after set: " + File.GetLastWriteTime(file).ToString("HH:mm:ss.fff") +
            "  contains key: " + File.ReadAllText(file).Contains("zz_save_test"));
        s.GetMethod("Set", Any).Invoke(null, new object[] { "zz_save_test", null });
        Console.WriteLine("after remove: contains key: " + File.ReadAllText(file).Contains("zz_save_test"));
        Console.WriteLine(asm.GetType("ByeDPI.AppLog").GetMethod("Dump", Any).Invoke(null, null));
        return 0;
    }
}
