// Drives ByeDpiService from ByeDPI.exe and checks the system side effects.
// Usage: ServiceTest.exe <ByeDPI.exe> <proxy|vpn> "<ciadpi args>" [hold-seconds]
using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using Microsoft.Win32;

static class ServiceTest
{
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static Assembly asm;
    static object service;
    static Type serviceType;

    static int Main(string[] args)
    {
        asm = Assembly.LoadFrom(args[0]);
        string mode = args[1];
        int hold = args.Length > 3 ? int.Parse(args[3]) : 0;

        var settings = asm.GetType("ByeDPI.Settings");
        settings.GetMethod("Load", Any).Invoke(null, null);
        Action<string, string> set = (k, v) => settings.GetMethod("Set", Any).Invoke(null, new object[] { k, v });
        set("byedpi_mode", mode);
        set("byedpi_enable_cmd_settings", "true");
        set("byedpi_cmd_args", args[2]);
        set("dns_ip", "94.140.15.15");

        serviceType = asm.GetType("ByeDPI.ByeDpiService");
        service = serviceType.GetField("Instance", Any).GetValue(null);
        serviceType.GetMethod("Init", Any).Invoke(service, new object[] { null });
        string failure = null;
        var failedEvent = serviceType.GetEvent("Failed", Any);
        Action<string> onFail = m => failure = m;
        failedEvent.GetAddMethod(true).Invoke(service, new object[] { onFail });

        var modeEnum = Enum.Parse(asm.GetType("ByeDPI.Mode"), mode == "vpn" ? "VPN" : "Proxy");
        Console.WriteLine("== before: " + ProxyState());
        serviceType.GetMethod("Start", Any).Invoke(service, new[] { modeEnum });
        WaitIdle();
        Console.WriteLine("== status after start: " + Status() + (failure != null ? "  FAILED: " + failure : ""));
        Console.WriteLine("== proxy: " + ProxyState());
        if (Status() == "Running")
        {
            Shell("tasklist /fi \"imagename eq ciadpi.exe\" /fo csv /nh & tasklist /fi \"imagename eq hev-socks5-tunnel.exe\" /fo csv /nh");
            if (hold > 0) { Console.WriteLine("== holding " + hold + "s"); Thread.Sleep(hold * 1000); }
            else Console.WriteLine("== READY");
            if (hold == 0) Console.ReadLine();
            serviceType.GetMethod("Stop", Any).Invoke(service, null);
            WaitIdle();
            Console.WriteLine("== status after stop: " + Status());
            Console.WriteLine("== proxy: " + ProxyState());
            Shell("tasklist /fi \"imagename eq ciadpi.exe\" /fo csv /nh & tasklist /fi \"imagename eq hev-socks5-tunnel.exe\" /fo csv /nh");
        }
        Console.WriteLine("== log ==");
        Console.WriteLine(asm.GetType("ByeDPI.AppLog").GetMethod("Dump", Any).Invoke(null, null));
        return 0;
    }

    static string Status()
    {
        return serviceType.GetProperty("Status", Any).GetValue(service, null).ToString();
    }

    static void WaitIdle()
    {
        Thread.Sleep(200);
        var busy = serviceType.GetProperty("Busy", Any);
        for (int i = 0; i < 300 && (bool)busy.GetValue(service, null); i++) Thread.Sleep(100);
    }

    static string ProxyState()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
            return "ProxyEnable=" + k.GetValue("ProxyEnable") + " ProxyServer=" + k.GetValue("ProxyServer");
    }

    static void Shell(string cmd)
    {
        var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c " + cmd)
            { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true });
        Console.Write(p.StandardOutput.ReadToEnd());
        p.WaitForExit();
    }
}
