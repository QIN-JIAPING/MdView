using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace MdView
{
    /// <summary>
    /// 主题管理。构建完整 ResourceDictionary 替换到窗口，而非修改已有 Brush 对象。
    /// </summary>
    public static class ThemeManager
    {
        public const string Light = "light";
        public const string Dark  = "dark";

        private const string SettingsFileName = "settings.json";
        private const string ThemeKey = "theme";

        private static string _current = Light;
        public  static string Current => _current;

        /// <summary>主题切换时触发。</summary>
        public static event Action? Changed;

        // ──────────────── 亮色 ────────────────
        private static readonly Dictionary<string, Color> LightColors = new()
        {
            ["EmeraldBrush"]              = Color.FromRgb(0x63, 0x66, 0xF1),
            ["EmeraldLightBrush"]         = Color.FromRgb(0xEE, 0xF0, 0xFF),
            ["TextPrimaryBrush"]          = Color.FromRgb(0x1E, 0x1B, 0x4B),
            ["TextBodyBrush"]             = Color.FromRgb(0x33, 0x41, 0x55),
            ["TextSecondaryBrush"]        = Color.FromRgb(0x94, 0xA3, 0xB8),
            ["IconForegroundBrush"]       = Color.FromRgb(0x64, 0x74, 0x8B),
            ["DarkBtnBrush"]              = Color.FromRgb(0x1E, 0x1B, 0x4B),
            ["LightGrayBrush"]            = Color.FromRgb(0xE2, 0xE8, 0xF0),
            ["InputBgBrush"]              = Color.FromRgb(0xF8, 0xFA, 0xFC),
            ["WindowBackgroundBrush"]     = Color.FromRgb(0xF1, 0xF5, 0xF9),
            ["SidebarBackgroundBrush"]    = Color.FromRgb(0xFF, 0xFF, 0xFF),
            ["CardBackgroundBrush"]       = Color.FromRgb(0xFF, 0xFF, 0xFF),
            ["TitleBarBackgroundBrush"]   = Color.FromRgb(0xFF, 0xFF, 0xFF),
            ["TitleBarActiveBackgroundBrush"] = Color.FromRgb(0xEE, 0xF0, 0xFF),
            ["ItemHoverBrush"]            = Color.FromRgb(0xF4, 0xF5, 0xFF),
            ["ItemSelectedBrush"]         = Color.FromRgb(0xE0, 0xE3, 0xFF),
        };

        // ──────────────── 暗色 ────────────────
        private static readonly Dictionary<string, Color> DarkColors = new()
        {
            ["EmeraldBrush"]              = Color.FromRgb(0x81, 0x8C, 0xF8),
            ["EmeraldLightBrush"]         = Color.FromRgb(0x1E, 0x1B, 0x4B),
            ["TextPrimaryBrush"]          = Color.FromRgb(0xF1, 0xF5, 0xF9),
            ["TextBodyBrush"]             = Color.FromRgb(0xCB, 0xD5, 0xE1),
            ["TextSecondaryBrush"]        = Color.FromRgb(0x94, 0xA3, 0xB8),
            ["IconForegroundBrush"]       = Color.FromRgb(0xCB, 0xD5, 0xE1),
            ["DarkBtnBrush"]              = Color.FromRgb(0xCB, 0xD5, 0xE1),
            ["LightGrayBrush"]            = Color.FromRgb(0x33, 0x3B, 0x48),
            ["InputBgBrush"]              = Color.FromRgb(0x1E, 0x23, 0x2E),
            ["WindowBackgroundBrush"]     = Color.FromRgb(0x0F, 0x13, 0x1A),
            ["SidebarBackgroundBrush"]    = Color.FromRgb(0x16, 0x1B, 0x24),
            ["CardBackgroundBrush"]       = Color.FromRgb(0x1C, 0x23, 0x2E),
            ["TitleBarBackgroundBrush"]   = Color.FromRgb(0x1C, 0x23, 0x2E),
            ["TitleBarActiveBackgroundBrush"] = Color.FromRgb(0x1E, 0x1B, 0x4B),
            ["ItemHoverBrush"]            = Color.FromRgb(0x24, 0x2B, 0x38),
            ["ItemSelectedBrush"]         = Color.FromRgb(0x2E, 0x30, 0x50),
        };

        // ──────────────── 阴影参数 ────────────────
        private const double LightCardOpacity = 0.06, LightCardDepth = 3;
        private const double DarkCardOpacity  = 0.18, DarkCardDepth  = 2;
        private const double LightSideOpacity = 0.04, LightSideDepth = 2;
        private const double DarkSideOpacity  = 0.16, DarkSideDepth  = 2;

        // ──────────────── API ────────────────

        /// <summary>预构建的字典缓存，避免每次切换都重建。</summary>
        private static ResourceDictionary? _lightDict;
        private static ResourceDictionary? _darkDict;

        public static void Initialize()
        {
            var saved = LoadSavedTheme();
            _current = saved == Dark ? Dark : Light;
            // 预构建两个字典
            _lightDict = BuildDictionary(Light);
            _darkDict  = BuildDictionary(Dark);
        }

        /// <summary>构建完整的主题 ResourceDictionary（颜色 + 阴影）。</summary>
        public static ResourceDictionary BuildDictionary(string theme)
        {
            var dict  = new ResourceDictionary();
            var map   = theme == Dark ? DarkColors : LightColors;
            var isDark = theme == Dark;

            foreach (var kv in map)
                dict[kv.Key] = new SolidColorBrush(kv.Value);

            var accent = map["EmeraldBrush"];
            var shadowCol = isDark ? Colors.Black : accent;

            dict["CardShadow"] = new DropShadowEffect
            {
                BlurRadius  = 20,
                Opacity     = isDark ? DarkCardOpacity  : LightCardOpacity,
                ShadowDepth = isDark ? DarkCardDepth    : LightCardDepth,
                Color       = shadowCol,
            };
            dict["SidebarShadow"] = new DropShadowEffect
            {
                BlurRadius  = 14,
                Opacity     = isDark ? DarkSideOpacity  : LightSideOpacity,
                ShadowDepth = isDark ? DarkSideDepth    : LightSideDepth,
                Color       = shadowCol,
            };
            return dict;
        }

        /// <summary>应用主题到窗口（使用缓存的字典，无需重建）。</summary>
        public static void ApplyToWindow(Window window, string theme)
        {
            var dict = theme == Dark ? _darkDict : _lightDict;
            if (dict == null) return;

            window.Resources.Clear();
            window.Resources.MergedDictionaries.Clear();
            window.Resources.MergedDictionaries.Add(dict);
            // InvalidateVisual 可省略 — MergedDictionaries 变更已触发 WPF 重绘

            _current = theme;
            SaveTheme(theme);

            Changed?.Invoke();
        }

        public static void Toggle(Window window)
        {
            var next = _current == Light ? Dark : Light;
            ApplyToWindow(window, next);
        }

        // ──────────────── 持久化 ────────────────

        private static string StoragePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MdView", SettingsFileName);

        private static string? LoadSavedTheme()
        {
            try
            {
                if (!File.Exists(StoragePath)) return null;
                var json = File.ReadAllText(StoragePath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty(ThemeKey, out var el)
                    && el.ValueKind == JsonValueKind.String)
                {
                    return el.GetString();
                }
            }
            catch { }
            return null;
        }

        private static void SaveTheme(string theme)
        {
            try
            {
                var dir = Path.GetDirectoryName(StoragePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(StoragePath,
                    JsonSerializer.Serialize(new { theme }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }
}
