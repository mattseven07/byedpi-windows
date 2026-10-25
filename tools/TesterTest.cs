// Runs ByeDPI's StrategyTester headless and prints the results.
// Usage: TesterTest.exe <ByeDPI.exe> <dns> ["strategy1|strategy2|..."]
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

static class TesterTest
{
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    static int Main(string[] args)
    {
        var asm = Assembly.LoadFrom(args[0]);
        var type = asm.GetType("ByeDPI.StrategyTester");
        var tester = Activator.CreateInstance(type, true);
        var sites = new List<string>((string[])type.GetField("DefaultSites", Any).GetValue(null));
        var strategies = args.Length > 2 ? new List<string>(args[2].Split('|'))
            : new List<string>((string[])type.GetField("DefaultStrategies", Any).GetValue(null));
        string finished = "(running)";
        bool done = false;
        Action<string> onFinish = m => { finished = m ?? "ok"; done = true; };
        type.GetEvent("Finished").GetAddMethod().Invoke(tester, new object[] { onFinish });
        var sw = Stopwatch.StartNew();
        type.GetMethod("Start").Invoke(tester, new object[] { strategies, "-s1 -r1+s", sites, args[1] });
        while (!done && sw.Elapsed.TotalSeconds < 180) Thread.Sleep(200);
        Console.WriteLine("finished: " + finished + " in " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s");
        Console.WriteLine("unresolved: " + string.Join(",", (List<string>)type.GetField("Unresolved").GetValue(tester)));
        foreach (var r in (IEnumerable)type.GetField("Results").GetValue(tester))
        {
            var rt = r.GetType();
            Func<string, object> f = n => rt.GetField(n).GetValue(r);
            Console.WriteLine(string.Format("{0,-30} {1}/{2} {3,5:0.00}s {4}{5} {6}",
                (bool)f("Baseline") ? "(baseline)" : f("Args"), f("Ok"), f("Total"), f("Seconds"),
                (bool)f("Current") ? "[current] " : "", f("Error") ?? "",
                string.Join(", ", (List<string>)f("Failed"))));
        }
        var log = asm.GetType("ByeDPI.AppLog").GetMethod("Dump", Any).Invoke(null, null) as string;
        foreach (var line in log.Split('\n'))
            if (line.Contains(" W Tester")) Console.WriteLine(line.Trim());
        return 0;
    }
}
