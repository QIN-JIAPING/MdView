using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using Markdig;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Web.WebView2.Core;

namespace MdView
{
    public partial class MainWindow : Window
    {
        private class PanelState
        {
            public string? CurrentFile;
            public string? FileDir;
            public FileSystemWatcher? Watcher;
            public DispatcherTimer? Debounce;
            public double LastScrollY;
            public double FontScale = 1.0;
            public TextBlock TitleText { get; }
            public TextBlock PathText { get; }
            public WebView2 WebView { get; }
            public Border TitleBar { get; }

            public PanelState(TextBlock titleText, TextBlock pathText, WebView2 webView, Border titleBar)
            {
                TitleText = titleText;
                PathText = pathText;
                WebView = webView;
                TitleBar = titleBar;
            }
        }

        private PanelState _stateL = null!;
        private PanelState _stateR = null!;
        private PanelState? _activePanel;
        private readonly FileHistory _history = FileHistory.Load();
        private bool _isSplitMode;
        private string _searchFilter = "";
        private static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(350);

        public MainWindow()
        {
            InitializeComponent();
            SetWindowIcon();
            Loaded += OnLoaded;
            KeyDown += OnWindowKeyDown;
            AutoRefreshCheck.Checked += OnAutoRefreshChanged;
            AutoRefreshCheck.Unchecked += OnAutoRefreshChanged;
            ThemeManager.Changed += OnThemeChanged;
            UpdateThemeButton();
            AllowDrop = true;
            Drop += OnDrop;
            DragOver += OnDragOver;
        }

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                CleanupPanel(_stateL);
                CleanupPanel(_stateR);
            }
            catch { }
            base.OnClosed(e);
        }

        private static void CleanupPanel(PanelState? state)
        {
            if (state == null) return;
            try
            {
                if (state.Watcher != null)
                {
                    state.Watcher.EnableRaisingEvents = false;
                    state.Watcher.Dispose();
                    state.Watcher = null;
                }
                if (state.Debounce != null)
                {
                    state.Debounce.Stop();
                    state.Debounce = null;
                }
            }
            catch { }
        }

        private void SetWindowIcon()
        {
            try
            {
                Icon = new System.Windows.Media.Imaging.BitmapImage(
                    new Uri("pack://application:,,,/app_icon.png"));
            }
            catch { }
        }

        private void OnThemeChanged()
        {
            UpdateThemeButton();
            try
            {
                // 透明背景：CSS 变量完全控制颜色，遮罩揭起时已无闪变
                var transparent = System.Drawing.Color.FromArgb(0, 0, 0, 0);
                WebViewL.DefaultBackgroundColor = transparent;
                WebViewR.DefaultBackgroundColor = transparent;
                UpdateActivePanelVisual();
                // WebView 内容由 AnimateThemeTransition 在遮罩后同步刷新
            }
            catch (Exception ex)
            {
                LogErr("Theme change failed: " + ex.Message);
            }
        }

        private static System.Drawing.Color ParseColor(string hex)
        {
            try
            {
                hex = hex.TrimStart('#');
                if (hex.Length == 6
                    && byte.TryParse(hex.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var r)
                    && byte.TryParse(hex.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)
                    && byte.TryParse(hex.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
                {
                    return System.Drawing.Color.FromArgb(r, g, b);
                }
            }
            catch { }
            return System.Drawing.Color.White;
        }

        private void UpdateThemeButton()
        {
            try
            {
                if (ThemeToggleBtn != null)
                {
                    ThemeToggleBtn.Content = ThemeManager.Current == ThemeManager.Dark ? "☀" : "🌙";
                    ThemeToggleBtn.ToolTip = ThemeManager.Current == ThemeManager.Dark
                        ? "切换到亮色主题 (Ctrl+Shift+D)"
                        : "切换到暗色主题 (Ctrl+Shift+D)";
                }
            }
            catch { }
        }

        private void OnThemeToggle(object sender, RoutedEventArgs e)
        {
            AnimateThemeTransition(() =>
            {
                ThemeManager.Toggle(this);
                StatusText.Text = ThemeManager.Current == ThemeManager.Dark
                    ? "已切换到暗色主题"
                    : "已切换到亮色主题";
            });
        }

        /// <summary>播放过渡动画后执行主题切换。遮罩等 WebView 也更新完再消失。</summary>
        private async void AnimateThemeTransition(Action switchAction)
        {
            try
            {
                // 变暗：0 → 0.6，150ms
                var dimIn = new System.Windows.Media.Animation.DoubleAnimation(0, 0.6, TimeSpan.FromMilliseconds(150))
                {
                    EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn }
                };
                TransitionOverlay.BeginAnimation(UIElement.OpacityProperty, dimIn);
                await System.Threading.Tasks.Task.Delay(140);

                // 切换主题
                switchAction();

                // WebView JS 注入
                var isDark = ThemeManager.Current == ThemeManager.Dark;
                var js = $"setTheme({(isDark ? "true" : "false")})";
                try { WebViewL?.CoreWebView2?.ExecuteScriptAsync(js); } catch { }
                try { WebViewR?.CoreWebView2?.ExecuteScriptAsync(js); } catch { }

                await System.Threading.Tasks.Task.Delay(60);

                // 恢复：0.6 → 0，400ms
                var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(0.6, 0, TimeSpan.FromMilliseconds(400))
                {
                    EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                };
                TransitionOverlay.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            }
            catch { switchAction(); }
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            // 恢复保存的主题（必须最先执行，确保后续 BrushHex 能读取到正确颜色）
            var saved = ThemeManager.Current == ThemeManager.Dark ? ThemeManager.Dark : ThemeManager.Light;
            ThemeManager.ApplyToWindow(this, saved);
            UpdateThemeButton();

            WebViewL.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0, 0, 0, 0);
            WebViewR.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0, 0, 0, 0);

            try
            {
                await WebViewL.EnsureCoreWebView2Async();
                await WebViewR.EnsureCoreWebView2Async();
            }
            catch (Exception ex)
            {
                StatusText.Text = "WebView2 初始化失败: " + ex.Message;
                LogErr("WebView2 init failed: " + ex);
                return;
            }

            WebViewL.CoreWebView2.NavigationStarting += (_, ea) => OnNavigationStarting(WebViewL, ea);
            WebViewR.CoreWebView2.NavigationStarting += (_, ea) => OnNavigationStarting(WebViewR, ea);
            WebViewL.CoreWebView2.NewWindowRequested += (_, ea) => OnNewWindowRequested(ea);
            WebViewR.CoreWebView2.NewWindowRequested += (_, ea) => OnNewWindowRequested(ea);
            WebViewL.CoreWebView2.WebMessageReceived += (_, ea) => OnWebMessage(_stateL, ea);
            WebViewR.CoreWebView2.WebMessageReceived += (_, ea) => OnWebMessage(_stateR, ea);

            _stateL = new PanelState(TitleTextL, PathTextL, WebViewL, TitleBarL);
            _stateR = new PanelState(TitleTextR, PathTextR, WebViewR, TitleBarR);

            var args = Environment.GetCommandLineArgs();
            string? leftFromArgs = (args.Length >= 2 && File.Exists(args[1])) ? args[1] : null;

            if (leftFromArgs != null)
            {
                OpenFileInternal(_stateL, leftFromArgs);
            }
            else
            {
                try
                {
                    var candidates = Directory.GetFiles(Environment.CurrentDirectory, "*.md")
                        .Concat(Directory.GetFiles(Environment.CurrentDirectory, "*.markdown"))
                        .Concat(Directory.GetFiles(Environment.CurrentDirectory, "*.mkd"))
                        .OrderBy(f => f)
                        .ToList();
                    if (candidates.Count > 0)
                        OpenFileInternal(_stateL, candidates[0]);
                }
                catch (Exception ex) { LogErr("Scan dir failed: " + ex.Message); }
            }

            if (args.Length >= 3 && File.Exists(args[2]))
                OpenFileInternal(_stateR, args[2]);

            RefreshRecentFilesList();
            _history.Changed += RefreshRecentFilesList;

            var hasLeft = !string.IsNullOrEmpty(_stateL.CurrentFile);
            var hasRight = !string.IsNullOrEmpty(_stateR.CurrentFile);
            SetSplitMode(hasLeft && hasRight);

            _activePanel = _stateL;
            UpdateActivePanelVisual();

            if (hasLeft && hasRight)
                StatusText.Text = "双栏模式 — 点击标题栏选中面板";
            else if (hasLeft)
                StatusText.Text = "提示：拖动最近文件到右栏可开启对比";
            else
                StatusText.Text = "Ctrl+O 打开 Markdown 文件 ｜ 也可直接把 md 文件拖进窗口";
            UpdateWindowTitle();
        }

        private void OnWindowKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F5)
            {
                var target = _activePanel ?? _stateL;
                if (target != null && !string.IsNullOrEmpty(target.CurrentFile))
                {
                    ReloadFile(target);
                    StatusText.Text = "已刷新: " + Path.GetFileName(target.CurrentFile);
                }
                else
                    StatusText.Text = "当前栏未打开文件";
                e.Handled = true;
                return;
            }

            var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

            if (ctrl && e.Key == Key.O)
            {
                if (_activePanel != null) ShowOpenFor(_activePanel);
                else if (_stateL != null) ShowOpenFor(_stateL);
                e.Handled = true;
            }
            else if (ctrl && e.Key == Key.T)
            {
                SetSplitMode(!_isSplitMode);
                e.Handled = true;
            }
            else if (ctrl && e.Key == Key.N)
            {
                if (Application.Current is App app) app.CreateWindow();
                e.Handled = true;
            }
            else if (ctrl && (e.Key == Key.W || e.Key == Key.F4))
            {
                Close();
                e.Handled = true;
            }
            else if (ctrl && shift && e.Key == Key.D)
            {
                OnThemeToggle(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (ctrl && e.Key == Key.F)
            {
                var tgt = _activePanel ?? _stateL;
                if (tgt != null) ShowFind(tgt);
                e.Handled = true;
            }
            else if (ctrl && e.Key == Key.D0)
            {
                SetZoom(_activePanel ?? _stateL, 1.0);
                e.Handled = true;
            }
            else if (ctrl && (e.Key == Key.Add || e.Key == Key.OemPlus || e.Key == Key.Subtract || e.Key == Key.OemMinus))
            {
                var tgt = _activePanel ?? _stateL;
                if (tgt != null)
                {
                    double step = 0.1;
                    if (e.Key == Key.Add || e.Key == Key.OemPlus) SetZoom(tgt, Math.Min(2.5, tgt.FontScale + step));
                    else SetZoom(tgt, Math.Max(0.5, tgt.FontScale - step));
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                _activePanel = null;
                UpdateActivePanelVisual();
                e.Handled = true;
            }
        }

        private void OnAutoRefreshChanged(object? sender, RoutedEventArgs e)
        {
            if (_stateL != null) SetupWatcher(_stateL);
            if (_stateR != null) SetupWatcher(_stateR);

            if (AutoRefreshCheck.IsChecked == true)
                StatusText.Text = "实时刷新：开 — 改动文件 350ms 后自动重载";
            else
                StatusText.Text = "实时刷新：关 — 需按 F5 手动刷新";
        }

        private void OnPanelFocusL(object sender, MouseButtonEventArgs e)
        {
            _activePanel = _stateL;
            UpdateActivePanelVisual();
            StatusText.Text = "已选中左栏";
        }

        private void OnPanelFocusR(object sender, MouseButtonEventArgs e)
        {
            _activePanel = _stateR;
            UpdateActivePanelVisual();
            StatusText.Text = "已选中右栏";
        }

        private void UpdateActivePanelVisual()
        {
            if (_stateL == null) return;
            var activeBg = TryBrushHex("TitleBarActiveBackgroundBrush") ?? "#EEF0FF";
            var idleBg   = TryBrushHex("TitleBarBackgroundBrush")     ?? "#FFFFFF";

            foreach (var state in new[] { _stateL, _stateR })
            {
                if (state == null) continue;
                if (state == _activePanel && _isSplitMode)
                    state.TitleBar.Background = new System.Windows.Media.SolidColorBrush(ParseMediaColor(activeBg));
                else
                    state.TitleBar.Background = new System.Windows.Media.SolidColorBrush(ParseMediaColor(idleBg));
            }
        }

        private static System.Windows.Media.Color ParseMediaColor(string hex)
        {
            try
            {
                hex = hex.TrimStart('#');
                if (hex.Length == 6
                    && byte.TryParse(hex.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var r)
                    && byte.TryParse(hex.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)
                    && byte.TryParse(hex.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
                {
                    return System.Windows.Media.Color.FromRgb(r, g, b);
                }
            }
            catch { }
            return System.Windows.Media.Colors.White;
        }

        private void OnNavigationStarting(WebView2 view, CoreWebView2NavigationStartingEventArgs e)
        {
            if (e.Uri != null && !e.Uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                && !e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
                && !e.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                OpenInSystemBrowser(e.Uri);
            }
        }

        private void OnNewWindowRequested(CoreWebView2NewWindowRequestedEventArgs e)
        {
            e.Handled = true;
            if (!string.IsNullOrEmpty(e.Uri))
                OpenInSystemBrowser(e.Uri);
        }

        private static void OpenInSystemBrowser(string url)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                LogErr("Open link failed: " + ex.Message);
            }
        }

        private void OnWebMessage(PanelState state, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var raw = e.TryGetWebMessageAsString();
                if (string.IsNullOrEmpty(raw)) return;
                using var doc = System.Text.Json.JsonDocument.Parse(raw);
                var root = doc.RootElement;
                if (!root.TryGetProperty("kind", out var kindEl)) return;
                var kind = kindEl.GetString();
                if (kind == "scroll" && root.TryGetProperty("y", out var yEl))
                {
                    state.LastScrollY = yEl.GetDouble();
                }
                else if (kind == "link" && root.TryGetProperty("href", out var hrefEl))
                {
                    var href = hrefEl.GetString();
                    if (!string.IsNullOrEmpty(href)
                        && !(href.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                             || href.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                             || href.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
                             || href.StartsWith("#", StringComparison.Ordinal)))
                    {
                        OpenInSystemBrowser(href);
                    }
                }
            }
            catch (Exception ex) { LogErr("Web msg parse: " + ex.Message); }
        }

        private void OnOpenL(object sender, RoutedEventArgs e)
        {
            _activePanel = _stateL;
            UpdateActivePanelVisual();
            ShowOpenFor(_stateL);
        }

        private void OnOpenR(object sender, RoutedEventArgs e)
        {
            if (!_isSplitMode) SetSplitMode(true);
            _activePanel = _stateR;
            UpdateActivePanelVisual();
            ShowOpenFor(_stateR);
        }

        public void OpenFile(string path)
        {
            if (_stateL != null && File.Exists(path))
                OpenFileInternal(_stateL, path);
        }

        private void ShowOpenFor(PanelState state)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Markdown 文件 (*.md;*.markdown;*.mkd)|*.md;*.markdown;*.mkd|所有文件 (*.*)|*.*",
                Title = "选择 Markdown 文件"
            };
            if (dlg.ShowDialog(this) == true) OpenFileInternal(state, dlg.FileName);
        }

        private void OpenFileInternal(PanelState state, string path)
        {
            try
            {
                state.CurrentFile = Path.GetFullPath(path);
                state.FileDir = Path.GetDirectoryName(state.CurrentFile);
                state.TitleText.Text = Path.GetFileName(state.CurrentFile);
                state.PathText.Text = Path.GetDirectoryName(state.CurrentFile) ?? "";
                UpdateInfoPanel(state);
                ReloadFile(state);
                SetupWatcher(state);
                UpdateWindowTitle();
                _history.Add(state.CurrentFile);
                StatusText.Text = "已打开: " + Path.GetFileName(state.CurrentFile)
                    + (AutoRefreshCheck.IsChecked == true ? "（实时刷新中）" : "");
            }
            catch (Exception ex)
            {
                StatusText.Text = "打开失败: " + ex.Message;
                LogErr("Open file: " + ex);
            }
        }

        private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .UsePipeTables()
            .UseGridTables()
            .UseTaskLists()
            .UseAutoLinks()
            .UseAutoIdentifiers()
            .UseEmojiAndSmiley()
            .DisableHtml()
            .Build();

        private static readonly Regex YamlFrontMatterRegex = new(
            @"^---\s*\r?\n.*?\r?\n---\s*\r?\n",
            RegexOptions.Compiled | RegexOptions.Singleline);

        private static readonly Regex ImgSrcRegex = new(
            @"<img\b([^>]*?)\bsrc=(['""])([^""'>]+)\2",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex TableHtmlRegex = new(
            @"<table[^>]*>([\s\S]*?)</table>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex PreWithCodeRegex = new(
            @"<pre[^>]*>(\s*)<code([^>]*)>([\s\S]*?)</code>\s*</pre>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static string ProcessRelativePaths(string html, string? baseDir)
        {
            if (string.IsNullOrEmpty(baseDir)) return html;
            return ImgSrcRegex.Replace(html, m =>
            {
                var preAttrs = m.Groups[1].Value;
                var quote = m.Groups[2].Value;
                var src = m.Groups[3].Value;

                if (src.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || src.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    || src.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                    || src.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                    || src.StartsWith("/", StringComparison.Ordinal))
                {
                    return m.Value;
                }

                try
                {
                    var full = Path.GetFullPath(Path.Combine(baseDir, src));
                    var uri = new UriBuilder(full) { Scheme = "file" };
                    var newSrc = uri.ToString();
                    return $"<img{preAttrs}src={quote}{newSrc}{quote}";
                }
                catch
                {
                    return m.Value;
                }
            });
        }

        private static string WrapTables(string html)
            => TableHtmlRegex.Replace(html, "<div style=\"overflow-x:auto;margin:.5em 0;\">$0</div>");

        private static string UpgradePreBlocks(string html)
            => PreWithCodeRegex.Replace(html, "<pre class=\"code-block\" spellcheck=\"false\">$1<code$2 class=\"language-plaintext\">$3</code></pre>");

        private static string? StripYamlFrontMatter(string md, out string? frontMatterBlock)
        {
            frontMatterBlock = null;
            var match = YamlFrontMatterRegex.Match(md);
            if (match.Success)
            {
                frontMatterBlock = match.Value;
                return md.Substring(match.Length);
            }
            return md;
        }

        private static string BuildFrontMatterCard(string? block)
        {
            if (string.IsNullOrWhiteSpace(block)) return "";
            var body = block.TrimStart('-').Trim();
            var lines = body.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            var sb = new StringBuilder();
            foreach (var l in lines)
            {
                var idx = l.IndexOf(':');
                if (idx > 0)
                {
                    var k = l.Substring(0, idx).Trim();
                    var v = l.Substring(idx + 1).Trim().Trim('"').Trim('\'');
                    sb.Append($"<div class=\"fm-line\"><span class=\"fm-key\">{System.Security.SecurityElement.Escape(k)}</span><span class=\"fm-val\">{System.Security.SecurityElement.Escape(v)}</span></div>");
                }
            }
            return $"<div class=\"fm-card\">{sb}</div>";
        }

        private void ReloadFile(PanelState state)
        {
            if (string.IsNullOrEmpty(state.CurrentFile) || !File.Exists(state.CurrentFile)) return;
            try
            {
                var fi = new FileInfo(state.CurrentFile);
                if (fi.Length > 50 * 1024 * 1024)
                {
                    state.WebView.NavigateToString("<!DOCTYPE html><html><body style='padding:40px;color:#666;font-family:sans-serif'><h2>文件过大</h2><p>超过 50MB 限制，无法预览</p></body></html>");
                    return;
                }
                var md = File.ReadAllText(state.CurrentFile, Encoding.UTF8);
                md = StripBom(md);
                md = StripYamlFrontMatter(md, out var fmBlock);
                var fmCard = BuildFrontMatterCard(fmBlock);

                var htmlRaw = Markdig.Markdown.ToHtml(md, Pipeline);
                htmlRaw = ProcessRelativePaths(htmlRaw, state.FileDir);
                htmlRaw = WrapTables(htmlRaw);
                htmlRaw = UpgradePreBlocks(htmlRaw);

                var html = Render(htmlRaw, state, fmCard);
                state.WebView.NavigateToString(html);
            }
            catch (Exception ex)
            {
                StatusText.Text = "读取失败: " + ex.Message;
                LogErr("Reload: " + ex);
            }
        }

        private static string StripBom(string s)
        {
            if (s.Length >= 1 && s[0] == '\uFEFF') return s.Substring(1);
            return s;
        }

        private string? TryBrushHex(string key)
        {
            try
            {
                if (TryFindResource(key) is System.Windows.Media.SolidColorBrush b)
                    return $"#{b.Color.R:X2}{b.Color.G:X2}{b.Color.B:X2}";
            }
            catch { }
            return null;
        }

        private string BrushHex(string key, string fallback) =>
            TryBrushHex(key) ?? fallback;

        private string Render(string bodyHtml, PanelState state, string frontMatterCard)
        {
            // 用 CSS 变量实现双主题，切换时只改 class 无需整页重载
            var cBg        = BrushHex("CardBackgroundBrush", "#FFFFFF");
            var cText      = BrushHex("TextBodyBrush", "#1F2937");
            var cHeading   = BrushHex("TextPrimaryBrush", "#111827");
            var cH1Border  = BrushHex("DarkBtnBrush", "#1F2937");
            var cH2Border  = BrushHex("LightGrayBrush", "#F3F4F6");
            var cLink      = TryBrushHex("EmeraldBrush") ?? "#6366F1";
            var cQuote     = TryBrushHex("EmeraldBrush") ?? "#6366F1";
            var cQuoteBg   = TryBrushHex("EmeraldLightBrush") ?? "#EEF0FF";
            var cQuoteText = TryBrushHex("TextSecondaryBrush") ?? "#64748B";
            var cCodeBg    = BrushHex("LightGrayBrush", "#F1F5F9");
            var cPreBg     = IsDarkTheme() ? "#1E293B" : "#1E1B4B";
            var cPreText   = IsDarkTheme() ? "#E2E8F0" : "#E2E8F0";
            var cTableBdr  = IsDarkTheme() ? "#334155" : "#E2E8F0";
            var cTableHead = BrushHex("LightGrayBrush", "#F8FAFC");
            var cTableHov  = IsDarkTheme() ? "#1E293B" : "#F8FAFC";
            var cHr        = BrushHex("LightGrayBrush", "#E2E8F0");
            var cFmBg      = TryBrushHex("EmeraldLightBrush") ?? "#EEF0FF";

            // 暗色 CSS 变量值（从 ThemeManager.DarkColors 对应取值）
            var dkBg        = "#1C232E";
            var dkText      = "#CBD5E1";
            var dkHeading   = "#F1F5F9";
            var dkH2Border  = "#333B48";
            var dkCodeBg    = "#333B48";
            var dkPreBg     = "#161B24";
            var dkTableBdr  = "#333B48";
            var dkTableHov  = "#242B38";
            var dkFmBg      = "#1E1B4B";
            var dkQuoteBg   = "#1E1B4B";
            var dkH1Border  = "#CBD5E1";

            var css = $@"
:root {{
  --bg:{cBg}; --text:{cText}; --heading:{cHeading};
  --h1-border:{cH1Border}; --h2-border:{cH2Border};
  --link:{cLink}; --quote:{cQuote}; --quote-bg:{cQuoteBg}; --quote-text:{cQuoteText};
  --code-bg:{cCodeBg}; --pre-bg:{cPreBg}; --pre-text:{cPreText};
  --table-bdr:{cTableBdr}; --table-head:{cTableHead}; --table-hov:{cTableHov};
  --hr:{cHr}; --fm-bg:{cFmBg};
  --bg-rgb: 255,255,255;
}}
html.dark {{
  --bg:{dkBg}; --text:{dkText}; --heading:{dkHeading};
  --h1-border:{dkH1Border}; --h2-border:{dkH2Border};
  --quote-bg:{dkQuoteBg};
  --code-bg:{dkCodeBg}; --pre-bg:{dkPreBg};
  --table-bdr:{dkTableBdr}; --table-hov:{dkTableHov};
  --fm-bg:{dkFmBg};
  --bg-rgb: 28,35,46;
}}
html,body {{ margin:0; padding:0; background:var(--bg); color:var(--text); }}
body {{ font-family:'Microsoft YaHei','PingFang SC',Segoe UI,Helvetica,Arial,sans-serif; font-size:14px; line-height:1.65; padding:16px 20px 40px; }}
h1,h2,h3,h4,h5,h6,p,a,li,code,pre,blockquote,table,th,td,tr,hr,.fm-card,.fm-line,.fm-key,.fm-val {{ transition:background-color .35s ease,color .35s ease,border-color .35s ease; }}
h1,h2,h3,h4,h5,h6 {{ color:var(--heading); font-weight:600; margin:1em 0 .4em; line-height:1.3; }}
h1 {{ font-size:1.5em; border-bottom:1px solid var(--h1-border); padding-bottom:.3em; }}
h2 {{ font-size:1.25em; border-bottom:1px solid var(--h2-border); padding-bottom:.2em; }}
h3 {{ font-size:1.1em; }}
p {{ margin:.5em 0; }}
a {{ color:{cLink}; text-decoration:none; }}
a:hover {{ text-decoration:underline; }}
ul,ol {{ padding-left:1.4em; margin:.4em 0; }}
li {{ margin:.15em 0; }}
blockquote {{ border-left:3px solid var(--quote); background:var(--quote-bg); color:var(--quote-text); padding:.4em .8em; margin:.5em 0; border-radius:0 4px 4px 0; }}
code {{ font-family:'Consolas','JetBrains Mono',Menlo,monospace; background:var(--code-bg); padding:1px 4px; border-radius:3px; font-size:.88em; }}
pre {{ background:var(--pre-bg); color:var(--pre-text); padding:10px 14px; border-radius:8px; overflow-x:auto; line-height:1.45; font-size:12px; margin:.5em 0; }}
pre code {{ background:transparent; color:inherit; padding:0; font-size:inherit; }}
pre .token {{ background:transparent !important; }}
table {{ border-collapse:collapse; width:100%; margin:.5em 0; font-size:12px; }}
th,td {{ border:1px solid var(--table-bdr); padding:4px 8px; }}
th {{ background:var(--table-head); font-weight:600; }}
tr:hover td {{ background:var(--table-hov); }}
hr {{ border:none; border-top:1px solid var(--hr); margin:1em 0; }}
img {{ max-width:100%; border-radius:4px; display:block; margin:.5em 0; }}
input[type=checkbox] {{ margin-right:.3em; vertical-align:-2px; }}
.fm-card {{ background:var(--fm-bg); border-radius:8px; padding:10px 14px; margin:.5em 0 1em; font-size:12px; line-height:1.8; }}
.fm-line {{ display:flex; gap:8px; }}
.fm-key {{ min-width:80px; color:var(--quote-text); font-weight:600; }}
.fm-val {{ color:var(--text); word-break:break-all; }}
.task-list {{ list-style:none; padding-left:0; }}
";

            // theme CSS 注入函数 + 现有 JS
            var scrollScript = $@"
(function(){{
  var __restoreY = {state.LastScrollY.ToString(System.Globalization.CultureInfo.InvariantCulture)};
  function report(){{ try {{ window.chrome.webview.postMessage({{kind:'scroll',y:window.scrollY}}); }} catch(e){{}} }}
  function applyZoom(z){{ document.documentElement.style.fontSize=(z*14)+'px'; }}
  function tryRestore(){{
    if(__restoreY > 0) {{ window.scrollTo(0, __restoreY); }}
    applyZoom({state.FontScale.ToString(System.Globalization.CultureInfo.InvariantCulture)});
    try {{ if(window.Prism) Prism.highlightAll(); }} catch(e){{}}
    report();
    window.removeEventListener('load', tryRestore);
  }}
  if(document.readyState === 'complete') tryRestore();
  else window.addEventListener('load', tryRestore);
  window.addEventListener('scroll', function(){{
    requestAnimationFrame(function(){{ report(); }});
  }});
  document.addEventListener('click', function(e){{
    var a = e.target && e.target.closest ? e.target.closest('a[href]') : null;
    if(!a) return;
    try {{ window.chrome.webview.postMessage({{kind:'link',href:a.href}}); }} catch(err){{}}
  }});
  window.__mdviewApplyZoom = applyZoom;
}})();
function setTheme(dark){{
  if(dark) document.documentElement.classList.add('dark');
  else document.documentElement.classList.remove('dark');
}}";

            var prismTheme = IsDarkTheme()
                ? @"https://cdnjs.cloudflare.com/ajax/libs/prism/1.29.0/themes/prism-tomorrow.min.css"
                : @"https://cdnjs.cloudflare.com/ajax/libs/prism/1.29.0/themes/prism.min.css";

            var htmlClass = IsDarkTheme() ? " class='dark'" : "";

            return $@"<!DOCTYPE html>
<html{htmlClass}><head><meta charset='utf-8'/>
<meta name='viewport' content='width=device-width,initial-scale=1'/>
<link rel='stylesheet' href='{prismTheme}'/>
<style>{css}</style>
</head>
<body>
{frontMatterCard}
{bodyHtml}
<script src='https://cdnjs.cloudflare.com/ajax/libs/prism/1.29.0/components/prism-core.min.js'></script>
<script src='https://cdnjs.cloudflare.com/ajax/libs/prism/1.29.0/components/prism-bash.min.js'></script>
<script src='https://cdnjs.cloudflare.com/ajax/libs/prism/1.29.0/components/prism-c.min.js'></script>
<script src='https://cdnjs.cloudflare.com/ajax/libs/prism/1.29.0/components/prism-csharp.min.js'></script>
<script src='https://cdnjs.cloudflare.com/ajax/libs/prism/1.29.0/components/prism-css.min.js'></script>
<script src='https://cdnjs.cloudflare.com/ajax/libs/prism/1.29.0/components/prism-javascript.min.js'></script>
<script src='https://cdnjs.cloudflare.com/ajax/libs/prism/1.29.0/components/prism-json.min.js'></script>
<script src='https://cdnjs.cloudflare.com/ajax/libs/prism/1.29.0/components/prism-markup.min.js'></script>
<script src='https://cdnjs.cloudflare.com/ajax/libs/prism/1.29.0/components/prism-python.min.js'></script>
<script src='https://cdnjs.cloudflare.com/ajax/libs/prism/1.29.0/components/prism-typescript.min.js'></script>
<script src='https://cdnjs.cloudflare.com/ajax/libs/prism/1.29.0/components/prism-yaml.min.js'></script>
<script>{scrollScript}</script>
</body></html>";
        }

        private static bool IsDarkTheme()
            => ThemeManager.Current == ThemeManager.Dark;

        private void ShowFind(PanelState state)
        {
            try
            {
                var js = "window.find(prompt('查找:', ''), false, false, true, false, false, false)";
                state.WebView.CoreWebView2.ExecuteScriptAsync(js);
            }
            catch { }
        }

        private void SetZoom(PanelState state, double scale)
        {
            state.FontScale = Math.Clamp(scale, 0.5, 2.5);
            try
            {
                var js = $"window.__mdviewApplyZoom ? window.__mdviewApplyZoom({state.FontScale}) : " +
                         $"(document.documentElement.style.fontSize=({state.FontScale*14})+'px');";
                state.WebView.ExecuteScriptAsync(js);
            }
            catch { }
            if (state.CurrentFile != null)
                StatusText.Text = $"缩放: {(int)(state.FontScale * 100)}%";
        }

        private void OnZoomL(object sender, RoutedEventArgs e) => SetZoomDialog(_stateL);
        private void OnZoomR(object sender, RoutedEventArgs e)
        {
            if (!_isSplitMode) SetSplitMode(true);
            SetZoomDialog(_stateR);
        }

        private void OnFindL(object sender, RoutedEventArgs e) => ShowFind(_stateL);
        private void OnFindR(object sender, RoutedEventArgs e)
        {
            if (!_isSplitMode) SetSplitMode(true);
            ShowFind(_stateR);
        }

        private void SetZoomDialog(PanelState state)
        {
            var w = new Window
            {
                Title = "设置字号",
                Width = 280, Height = 120,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = System.Windows.ResizeMode.NoResize,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.White)
            };
            var stack = new StackPanel { Margin = new Thickness(16) };
            var header = new TextBlock { Text = "字号比例 (50% - 250%)", FontSize = 12, Margin = new Thickness(0, 0, 0, 6) };
            var tb = new TextBox
            {
                Text = ((int)(state.FontScale * 100)).ToString(),
                FontSize = 14,
                Padding = new Thickness(6, 4, 6, 4)
            };
            var btn = new Button
            {
                Content = "应用",
                Margin = new Thickness(0, 10, 0, 0),
                Padding = new Thickness(20, 6, 20, 6),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            btn.Click += (_, _) =>
            {
                if (int.TryParse(tb.Text, out var pct))
                {
                    SetZoom(state, Math.Clamp(pct / 100.0, 0.5, 2.5));
                    w.DialogResult = true;
                    w.Close();
                }
                else
                    tb.Focus();
            };
            stack.Children.Add(header);
            stack.Children.Add(tb);
            stack.Children.Add(btn);
            w.Content = stack;
            w.ShowDialog();
        }

        private void SetupWatcher(PanelState state)
        {
            state.Watcher?.Dispose();
            state.Watcher = null;
            state.Debounce?.Stop();
            state.Debounce = null;

            if (string.IsNullOrEmpty(state.CurrentFile) || AutoRefreshCheck.IsChecked != true)
                return;

            try
            {
                var dir = Path.GetDirectoryName(state.CurrentFile);
                var name = Path.GetFileName(state.CurrentFile);
                if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name)) return;

                state.Watcher = new FileSystemWatcher(dir, name)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = false
                };
                state.Debounce = new DispatcherTimer { Interval = DebounceInterval };

                state.Debounce.Tick += (_, _) =>
                {
                    state.Debounce!.Stop();
                    ReloadFile(state);
                };

                state.Watcher.Changed += (_, _) =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (state.Debounce!.IsEnabled) state.Debounce.Stop();
                        state.Debounce.Start();
                    }));
                };

                state.Watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) { LogErr("Watcher setup: " + ex.Message); }
        }

        private void OnToggleSplit(object sender, RoutedEventArgs e)
        {
            SetSplitMode(!_isSplitMode);
        }

        private void SetSplitMode(bool split)
        {
            _isSplitMode = split;

            if (split)
            {
                SplitGrid.ColumnDefinitions[1].Width = new GridLength(5);
                SplitGrid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
                SplitterControl.Visibility = Visibility.Visible;
                RightPanel.Visibility = Visibility.Visible;
            }
            else
            {
                SplitGrid.ColumnDefinitions[1].Width = new GridLength(0);
                SplitGrid.ColumnDefinitions[2].Width = new GridLength(0);
                SplitterControl.Visibility = Visibility.Collapsed;
                RightPanel.Visibility = Visibility.Collapsed;
            }
            UpdateActivePanelVisual();
            UpdateWindowTitle();
        }

        private void UpdateWindowTitle()
        {
            var l = !string.IsNullOrEmpty(_stateL?.CurrentFile)
                ? Path.GetFileName(_stateL.CurrentFile) : "未选择";
            if (_isSplitMode)
            {
                var r = !string.IsNullOrEmpty(_stateR?.CurrentFile)
                    ? Path.GetFileName(_stateR.CurrentFile) : "未选择";
                Title = $"MdView — {l} | {r}";
            }
            else
            {
                Title = $"MdView — {l}";
            }
        }

        private void RefreshRecentFilesList()
        {
            try
            {
                var source = _history.Entries;
                IEnumerable<string> filtered;
                if (!string.IsNullOrWhiteSpace(_searchFilter))
                {
                    var f = _searchFilter.Trim().ToLowerInvariant();
                    filtered = source.Where(p =>
                    {
                        var name = Path.GetFileName(p).ToLowerInvariant();
                        var dir = (Path.GetDirectoryName(p) ?? "").ToLowerInvariant();
                        return name.Contains(f) || dir.Contains(f);
                    });
                }
                else
                {
                    filtered = source;
                }
                var list = filtered.ToList();
                RecentFilesList.ItemsSource = null;
                RecentFilesList.ItemsSource = list.Select(p =>
                {
                    try { return Path.GetFileName(p); }
                    catch { return p; }
                }).ToList();
                RecentFilesList.Tag = new List<string>(list);
            }
            catch (Exception ex) { LogErr("Refresh list: " + ex.Message); }
        }

        private void OnSearchFilter(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            try
            {
                _searchFilter = (SearchBox.Text ?? "").Trim();
                RefreshRecentFilesList();
            }
            catch { }
        }

        private void OnRecentFileClick(object sender, MouseButtonEventArgs e)
        {
            if (RecentFilesList.SelectedItem == null) return;
            var tagList = RecentFilesList.Tag as List<string>;
            if (tagList == null) return;
            var idx = RecentFilesList.SelectedIndex;
            if (idx < 0 || idx >= tagList.Count) return;
            var path = tagList[idx];
            if (!File.Exists(path))
            {
                _history.Remove(path);
                StatusText.Text = "文件不存在，已从历史移除: " + Path.GetFileName(path);
                RefreshRecentFilesList();
                return;
            }

            PanelState target;
            if (!_isSplitMode)
            {
                target = _stateL;
            }
            else if (string.IsNullOrEmpty(_stateL.CurrentFile))
            {
                target = _stateL;
            }
            else if (string.IsNullOrEmpty(_stateR.CurrentFile))
            {
                target = _stateR;
            }
            else
            {
                target = _activePanel ?? _stateL;
            }

            _activePanel = target;
            UpdateActivePanelVisual();
            OpenFileInternal(target, path);
        }

        private void OnRecentFileRightClick(object sender, MouseButtonEventArgs e)
        {
            var dep = e.OriginalSource as DependencyObject;
            var item = FindAncestor<ListBoxItem>(dep);
            if (item != null) item.IsSelected = true;
        }

        private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T t) return t;
                current = System.Windows.Media.VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private void OnClearHistory(object sender, RoutedEventArgs e)
        {
            _history.Clear();
            StatusText.Text = "最近文件已清除";
        }

        private string? GetSelectedRecentPath()
        {
            var tagList = RecentFilesList.Tag as List<string>;
            if (tagList == null) return null;
            var idx = RecentFilesList.SelectedIndex;
            if (idx < 0 || idx >= tagList.Count) return null;
            return tagList[idx];
        }

        private void OpenRecentInto(PanelState target)
        {
            var path = GetSelectedRecentPath();
            if (string.IsNullOrEmpty(path)) return;
            if (!File.Exists(path))
            {
                _history.Remove(path);
                StatusText.Text = "文件不存在，已从历史移除: " + Path.GetFileName(path);
                RefreshRecentFilesList();
                return;
            }
            _activePanel = target;
            UpdateActivePanelVisual();
            OpenFileInternal(target, path);
        }

        private void OnCtxOpenInLeft(object sender, RoutedEventArgs e) => OpenRecentInto(_stateL);
        private void OnCtxOpenInRight(object sender, RoutedEventArgs e)
        {
            if (!_isSplitMode) SetSplitMode(true);
            OpenRecentInto(_stateR);
        }

        private void OnCtxRevealInExplorer(object sender, RoutedEventArgs e)
        {
            var path = GetSelectedRecentPath();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                StatusText.Text = "文件不存在";
                return;
            }
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                StatusText.Text = "无法打开资源管理器: " + ex.Message;
            }
        }

        private void OnCtxRemove(object sender, RoutedEventArgs e)
        {
            var path = GetSelectedRecentPath();
            if (string.IsNullOrEmpty(path)) return;
            _history.Remove(path);
            StatusText.Text = "已从历史移除: " + Path.GetFileName(path);
            RefreshRecentFilesList();
        }

        private void OnCtxOpen(object sender, RoutedEventArgs e)
        {
            OnRecentFileClick(RecentFilesList, null!);
        }

        private void OnCtxCopyPath(object sender, RoutedEventArgs e)
        {
            var path = GetSelectedRecentPath();
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                System.Windows.Clipboard.SetText(path);
                StatusText.Text = "已复制路径: " + path;
            }
            catch (Exception ex)
            {
                StatusText.Text = "复制失败: " + ex.Message;
            }
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var dropped = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (dropped == null || dropped.Length == 0) return;

            var mdFiles = dropped.Where(p =>
            {
                try
                {
                    if (!File.Exists(p)) return false;
                    var ext = Path.GetExtension(p).ToLowerInvariant();
                    return ext == ".md" || ext == ".markdown" || ext == ".mkd" || ext == ".mdown";
                }
                catch { return false; }
            }).ToList();

            if (mdFiles.Count == 0)
            {
                StatusText.Text = "拖入的不是 Markdown 文件";
                return;
            }

            if (mdFiles.Count >= 2 && _stateL != null && _stateR != null)
            {
                SetSplitMode(true);
                OpenFileInternal(_stateL, mdFiles[0]);
                OpenFileInternal(_stateR, mdFiles[1]);
                _activePanel = _stateL;
                UpdateActivePanelVisual();
            }
            else
            {
                var target = _activePanel ?? _stateL;
                if (!_isSplitMode || !string.IsNullOrEmpty(target.CurrentFile) && string.IsNullOrEmpty(_stateR?.CurrentFile))
                    target = _stateR != null && string.IsNullOrEmpty(_stateR.CurrentFile) && _isSplitMode ? _stateR : target;
                if (target == _stateR && !_isSplitMode) SetSplitMode(true);
                OpenFileInternal(target, mdFiles[0]);
                _activePanel = target;
                UpdateActivePanelVisual();
            }
        }

        private void UpdateInfoPanel(PanelState state)
        {
            try
            {
                if (state.CurrentFile != null && File.Exists(state.CurrentFile))
                {
                    InfoFileName.Text = Path.GetFileName(state.CurrentFile);
                    InfoFilePath.Text = Path.GetDirectoryName(state.CurrentFile) ?? "";
                }
                else
                {
                    InfoFileName.Text = "—";
                    InfoFilePath.Text = "—";
                }
            }
            catch { }
        }

        private void OnToggleInfoPanel(object sender, RoutedEventArgs e)
        {
            if (InfoPanel.Visibility == Visibility.Visible)
            {
                InfoPanel.Visibility = Visibility.Collapsed;
                InfoPanelCol.Width = new GridLength(0);
                InfoPanelToggleBtn.Content = "▶";
                InfoPanelToggleBtn.ToolTip = "显示信息面板";
                StatusText.Text = "信息面板已隐藏 — 点侧边栏 ▶ 可恢复";
            }
            else
            {
                InfoPanel.Visibility = Visibility.Visible;
                InfoPanelCol.Width = new GridLength(220);
                InfoPanelToggleBtn.Content = "◀";
                InfoPanelToggleBtn.ToolTip = "隐藏信息面板";
                StatusText.Text = "信息面板已显示";
            }
        }

        private static void LogErr(string msg)
        {
            try { System.Diagnostics.Debug.WriteLine("[MdView] " + msg); } catch { }
        }
    }
}
