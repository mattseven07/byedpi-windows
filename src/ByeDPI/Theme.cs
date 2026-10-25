using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using Microsoft.Win32;

namespace ByeDPI
{
    // Material-like palette matching the Android app (colorPrimary #5976DF)
    static class Theme
    {
        public static bool Dark { get; private set; }
        public static Color Bg, Text, TextSecondary, TextDisabled, Accent, Divider,
            Outline, Hover, Pressed, SwitchOnThumb, SwitchOnTrack, SwitchOffThumb, SwitchOffTrack,
            DialogBg, InputBg, ToastBg, ToastText, Good, Warn, Bad;

        public static event Action Changed;
        public static float Scale = 1f;

        static readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();
        static string iconFamily;

        public static void Apply(string name)
        {
            Dark = name == "dark" || (name != "light" && SystemIsDark());
            if (Dark)
            {
                Bg = C(0x131313); Text = C(0xE6E6E6); TextSecondary = C(0xA8A8A8); TextDisabled = C(0x5E5E5E);
                Accent = C(0x7E91EC); Divider = C(0x2B2B2B); Outline = C(0x8E8E8E);
                Hover = Color.FromArgb(18, 255, 255, 255); Pressed = Color.FromArgb(34, 255, 255, 255);
                SwitchOnThumb = C(0x5D79E2); SwitchOnTrack = C(0x3A4C8E);
                SwitchOffThumb = C(0xC9C9C9); SwitchOffTrack = C(0x4A4A4A);
                DialogBg = C(0x232323); InputBg = C(0x1A1A1A); ToastBg = C(0xE6E6E6); ToastText = C(0x1C1C1C);
                Good = C(0x4CC38A); Warn = C(0xE0A43A); Bad = C(0xF0706A);
            }
            else
            {
                Bg = C(0xFFFFFF); Text = C(0x1C1C1C); TextSecondary = C(0x5F5F5F); TextDisabled = C(0xB5B5B5);
                Accent = C(0x3C52A3); Divider = C(0xE3E3E3); Outline = C(0x8A8A8A);
                Hover = Color.FromArgb(14, 0, 0, 0); Pressed = Color.FromArgb(28, 0, 0, 0);
                SwitchOnThumb = C(0x5976DF); SwitchOnTrack = C(0xB9C4F2);
                SwitchOffThumb = C(0xF4F4F4); SwitchOffTrack = C(0xB0B0B0);
                DialogBg = C(0xFFFFFF); InputBg = C(0xF6F6F6); ToastBg = C(0x323232); ToastText = C(0xF2F2F2);
                Good = C(0x1E7F4F); Warn = C(0x9A6200); Bad = C(0xC42B24);
            }
            var h = Changed;
            if (h != null) h();
        }

        static bool SystemIsDark()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    return v is int && (int)v == 0;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        static Color C(int rgb)
        {
            return Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        }

        public static int S(float px)
        {
            return (int)Math.Round(px * Scale);
        }

        public static Font Font(float size, FontStyle style = FontStyle.Regular)
        {
            return Get("Segoe UI", size, style);
        }

        // Segoe Fluent Icons on Windows 11, Segoe MDL2 Assets on Windows 10
        public static Font Icons(float size)
        {
            if (iconFamily == null)
            {
                iconFamily = "Segoe MDL2 Assets";
                using (var fc = new InstalledFontCollection())
                    foreach (var f in fc.Families)
                        if (f.Name == "Segoe Fluent Icons") iconFamily = f.Name;
            }
            return Get(iconFamily, size, FontStyle.Regular);
        }

        static Font Get(string family, float size, FontStyle style)
        {
            string key = family + size + style;
            Font f;
            if (!fonts.TryGetValue(key, out f))
            {
                f = new Font(family, size, style, GraphicsUnit.Point);
                fonts[key] = f;
            }
            return f;
        }

        public static class Glyph
        {
            public const string Back = "";
            public const string Settings = "";
            public const string More = "";
            public const string Code = "";
            public const string Docs = "";
            public const string Check = "";
            public const string Search = "";
            public const string Play = "";
            public const string Stop = "";
            public const string Warning = "";
            public const string Cancel = "";
            public const string Sync = "";
            public const string Clock = "";
        }
    }
}
