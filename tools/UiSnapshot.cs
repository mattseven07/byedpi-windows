// Renders ByeDPI.exe screens to PNG files for visual checks.
// Usage: UiSnapshot.exe <ByeDPI.exe> <outDir> <dark|light>
using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

static class UiSnapshot
{
    static Form form;
    static string outDir;
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    [STAThread]
    static void Main(string[] args)
    {
        var asm = Assembly.LoadFrom(args[0]);
        outDir = args[1];
        Directory.CreateDirectory(outDir);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        asm.GetType("ByeDPI.Settings").GetMethod("Load", Any).Invoke(null, null);
        asm.GetType("ByeDPI.Theme").GetMethod("Apply", Any).Invoke(null, new object[] { args[2] });

        var mainType = asm.GetType("ByeDPI.MainForm");
        form = (Form)Activator.CreateInstance(mainType, Any, null, new object[] { false, false }, null);
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-3000, 0);
        form.Show();
        Pump();
        Save("1-home");

        mainType.GetMethod("OpenSettings", Any).Invoke(form, null);
        Pump();
        Save("2-settings");

        var pageType = mainType.GetNestedType("Page", Any);
        foreach (var name in new[] { "BuildUiEditor", "BuildCmdEditor" })
        {
            var view = (Control)mainType.GetMethod(name, Any).Invoke(form, null);
            var page = Activator.CreateInstance(pageType, true);
            pageType.GetField("Title", Any).SetValue(page, name == "BuildUiEditor" ? "UI editor" : "Command line editor");
            pageType.GetField("View", Any).SetValue(page, view);
            mainType.GetMethod("Push", Any).Invoke(form, new[] { page });
            Pump();
            Save(name == "BuildUiEditor" ? "3-ui-editor" : "5-cmd-editor");
            if (name == "BuildUiEditor")
            {
                var sc = (ScrollableControl)view;
                sc.AutoScrollPosition = new Point(0, 2000);
                view.Invalidate();
                Pump();
                Save("4-ui-editor-bottom");
            }
            mainType.GetMethod("GoBack", Any).Invoke(form, null);
            Pump();
        }
        // strategy tester: open, press Start, wait for results
        {
            var view = (ScrollableControl)mainType.GetMethod("BuildTester", Any).Invoke(form, null);
            var page = Activator.CreateInstance(pageType, true);
            pageType.GetField("Title", Any).SetValue(page, "Strategy test");
            pageType.GetField("View", Any).SetValue(page, view);
            mainType.GetMethod("Push", Any).Invoke(form, new[] { page });
            Pump();
            Save("8-tester");
            var rows = (System.Collections.IList)view.GetType().GetField("rows", Any).GetValue(view);
            var rowType = asm.GetType("ByeDPI.PrefRow");
            Action startClick = null;
            foreach (var row in rows)
                if ((string)rowType.GetField("Title").GetValue(row) == "Start test")
                    startClick = (Action)rowType.GetField("Click").GetValue(row);
            startClick();
            view.GetType().GetMethod("RefreshRows").Invoke(view, null);
            var running = asm.GetType("ByeDPI.StrategyTester").GetField("Running", Any);
            for (int i = 0; i < 6; i++) Pump();
            Save("9-tester-running");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while ((bool)running.GetValue(null) && sw.Elapsed.TotalSeconds < 120) Pump();
            for (int i = 0; i < 5; i++) Pump();
            Save("10-tester-done");
            view.AutoScrollPosition = new Point(0, 600);
            view.Invalidate();
            Pump();
            Save("11-tester-done-scrolled");
            mainType.GetMethod("GoBack", Any).Invoke(form, null);
            Pump();
        }
        mainType.GetMethod("ShowToast", Any).Invoke(form, new object[] { "Failed to start VPN: example error message" });
        mainType.GetMethod("GoBack", Any).Invoke(form, null);
        Pump();
        Save("6-home-toast");
        var toast = (Control)mainType.GetField("toast", Any).GetValue(form);
        Console.WriteLine("toast visible=" + toast.Visible + " bounds=" + toast.Bounds + " parent=" + toast.Parent.Name + " z=" + form.Controls.GetChildIndex(toast));
        using (var bmp = new Bitmap(Math.Max(1, toast.Width), Math.Max(1, toast.Height)))
        {
            toast.DrawToBitmap(bmp, new Rectangle(Point.Empty, toast.Size));
            bmp.Save(Path.Combine(outDir, "7-toast-only.png"));
        }
        Environment.Exit(0);
    }

    static void Pump()
    {
        for (int i = 0; i < 20; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(15); }
    }

    static void Save(string name)
    {
        using (var bmp = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.Size));
            bmp.Save(Path.Combine(outDir, name + ".png"));
        }
    }
}
