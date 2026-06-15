using System;
using System.Collections.Generic;
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

namespace MdView
{
    public partial class MainWindow : Window
    {
        private class PanelState
        {
            public string? CurrentFile;
            public FileSystemWatcher? Watcher;
            public DispatcherTimer? Debounce;
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
        private static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(350);

        public MainWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            KeyDown += OnWindowKeyDown;
            AutoRefreshCheck.Checked += OnAutoRefreshChanged;
            AutoRefreshCheck.Unchecked += OnAutoRefreshChanged;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await WebViewL.EnsureCoreWebView2Async();
                await WebViewR.EnsureCoreWebView2Async();
            }
            catch (Exception ex)
            {
                StatusText.Text = "WebView2 初始化失败: " + ex.Message;
                return;
            }

            _stateL = new PanelState(TitleTextL, PathTextL, WebViewL, TitleBarL);
            _stateR = new PanelState(TitleTextR, PathTextR, WebViewR, TitleBarR);

            var candidates = Directory.GetFiles(Environment.CurrentDirectory, "*.md")
                .Concat(Directory.GetFiles(Environment.CurrentDirectory, "*.markdown"))
                .OrderBy(f => f)
                .ToList();

            var args = Environment.GetCommandLineArgs();

            if (args.Length >= 2 && File.Exists(args[1]))
                OpenFileInternal(_stateL, args[1]);
            else if (candidates.Count > 0)
                OpenFileInternal(_stateL, candidates[0]);

            if (args.Length >= 3 && File.Exists(args[2]))
                OpenFileInternal(_stateR, args[2]);
            else if (candidates.Count > 1)
                OpenFileInternal(_stateR, candidates[1]);

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
                StatusText.Text = "单击最近文件在选中栏打开";
        }

        private void OnWindowKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F5)
            {
                ReloadFile(_stateL);
                ReloadFile(_stateR);
                StatusText.Text = "已刷新";
                e.Handled = true;
            }
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O)
            {
                if (_activePanel != null)
                    ShowOpenFor(_activePanel);
                else
                    ShowOpenFor(_stateL);
                e.Handled = true;
            }
        }

        private void OnAutoRefreshChanged(object? sender, RoutedEventArgs e)
        {
            if (_stateL != null) SetupWatcher(_stateL);
            if (_stateR != null) SetupWatcher(_stateR);
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
            foreach (var state in new[] { _stateL, _stateR })
            {
                if (state == null) continue;
                if (state == _activePanel && _isSplitMode)
                    state.TitleBar.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEC, 0xFD, 0xF5));
                else
                    state.TitleBar.Background = System.Windows.Media.Brushes.White;
            }
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
                state.TitleText.Text = Path.GetFileName(state.CurrentFile);
                state.PathText.Text = Path.GetDirectoryName(state.CurrentFile) ?? "";
                ReloadFile(state);
                SetupWatcher(state);
                UpdateWindowTitle();
                _history.Add(state.CurrentFile);
                StatusText.Text = "已打开: " + Path.GetFileName(state.CurrentFile);
            }
            catch (Exception ex)
            {
                StatusText.Text = "打开失败: " + ex.Message;
            }
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
                var html = Render(md);
                state.WebView.NavigateToString(html);
            }
            catch (Exception ex)
            {
                StatusText.Text = "读取失败: " + ex.Message;
            }
        }

        private void SetupWatcher(PanelState state)
        {
            state.Watcher?.Dispose();
            state.Watcher = null;
            state.Debounce?.Stop();
            state.Debounce = null;

            if (string.IsNullOrEmpty(state.CurrentFile) || !AutoRefreshCheck.IsChecked == true) return;
            try
            {
                var dir = Path.GetDirectoryName(state.CurrentFile);
                var name = Path.GetFileName(state.CurrentFile);
                if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name)) return;

                state.Watcher = new FileSystemWatcher(dir, name)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
                };
                state.Debounce = new DispatcherTimer { Interval = DebounceInterval };

                state.Debounce.Tick += (_, _) =>
                {
                    state.Debounce!.Stop();
                    ReloadFile(state);
                };

                state.Watcher.Changed += (_, _) =>
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (state.Debounce!.IsEnabled) state.Debounce.Stop();
                        state.Debounce.Start();
                    });
                };

                state.Watcher.EnableRaisingEvents = true;
            }
            catch { }
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
                ContentGrid.ColumnDefinitions[1].Width = new GridLength(5);
                ContentGrid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
                SplitterControl.Visibility = Visibility.Visible;
                RightPanel.Visibility = Visibility.Visible;
            }
            else
            {
                ContentGrid.ColumnDefinitions[1].Width = new GridLength(0);
                ContentGrid.ColumnDefinitions[2].Width = new GridLength(0);
                SplitterControl.Visibility = Visibility.Collapsed;
                RightPanel.Visibility = Visibility.Collapsed;
            }
            UpdateActivePanelVisual();
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

        // ========== 最近文件 ==========

        private void RefreshRecentFilesList()
        {
            RecentFilesList.ItemsSource = null;
            RecentFilesList.ItemsSource = _history.Entries.Select(p =>
            {
                try { return Path.GetFileName(p); }
                catch { return p; }
            }).ToList();
            RecentFilesList.Tag = _history.Entries.ToList();
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
                StatusText.Text = "文件不存在: " + path;
                return;
            }

            var target = _isSplitMode ? (_activePanel ?? _stateL) : _stateL;
            OpenFileInternal(target, path);
        }

        private void OnClearHistory(object sender, RoutedEventArgs e)
        {
            _history.Clear();
            StatusText.Text = "最近文件已清除";
        }

        // ========== Markdown → HTML ==========

        private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .UsePipeTables()
            .UseGridTables()
            .UseTaskLists()
            .UseAutoLinks()
            .DisableHtml()
            .Build();

        private static string Render(string md)
        {
            var html = Markdig.Markdown.ToHtml(md, Pipeline);
            var anchors = new StringBuilder();
            var headings = Regex.Matches(html, @"<h([1-6])>(.+?)</h[1-6]>", RegexOptions.IgnoreCase);
            foreach (Match h in headings)
            {
                var txt = Regex.Replace(h.Groups[2].Value, @"<[^>]+>", "");
                var a = ToAnchor(txt);
                if (string.IsNullOrEmpty(a)) continue;
                anchors.Append(
                    $"document.querySelectorAll('h{h.Groups[1].Value}').forEach(" +
                    $"function(el){{ if(!el.id) el.id='{a}'; }});");
            }

            return $@"<!DOCTYPE html>
<html><head><meta charset='utf-8'/>
<meta name='viewport' content='width=device-width,initial-scale=1'/>
<style>
  html,body {{ margin:0; padding:0; background:#fff; color:#2B2F36; }}
  body {{ font-family:'Microsoft YaHei','PingFang SC',Segoe UI,Helvetica,Arial,sans-serif; font-size:14px; line-height:1.65; padding:16px 20px 40px; }}
  h1,h2,h3,h4,h5,h6 {{ color:#111827; font-weight:600; margin:1em 0 .4em; line-height:1.3; }}
  h1 {{ font-size:1.5em; border-bottom:1px solid #E4E6EB; padding-bottom:.3em; }}
  h2 {{ font-size:1.25em; border-bottom:1px solid #F0F2F5; padding-bottom:.2em; }}
  h3 {{ font-size:1.1em; }}
  p {{ margin:.5em 0; }}
  a {{ color:#2D6CFF; text-decoration:none; }}
  a:hover {{ text-decoration:underline; }}
  ul,ol {{ padding-left:1.4em; margin:.4em 0; }}
  li {{ margin:.15em 0; }}
  blockquote {{ border-left:3px solid #2D6CFF; background:#F5F6F8; color:#4B5563; padding:.4em .8em; margin:.5em 0; border-radius:0 4px 4px 0; }}
  code {{ font-family:'Consolas','JetBrains Mono',Menlo,monospace; background:#F0F2F5; padding:1px 4px; border-radius:3px; font-size:.9em; color:#E83E8C; }}
  pre {{ background:#0F172A; color:#E2E8F0; padding:10px 14px; border-radius:6px; overflow-x:auto; line-height:1.45; font-size:12px; margin:.5em 0; }}
  pre code {{ background:transparent; color:inherit; padding:0; font-size:inherit; }}
  table {{ border-collapse:collapse; width:100%; margin:.5em 0; font-size:12px; }}
  th,td {{ border:1px solid #E4E6EB; padding:4px 8px; }}
  th {{ background:#F5F6F8; font-weight:600; }}
  tr:hover td {{ background:#FAFBFC; }}
  hr {{ border:none; border-top:1px solid #E4E6EB; margin:1em 0; }}
  img {{ max-width:100%; border-radius:4px; }}
  input[type=checkbox] {{ margin-right:.3em; vertical-align:-2px; }}
  .hljs {{ background:#0F172A !important; }}
</style>
</head>
<body>
{html}
<script>{anchors}</script>
</body></html>";
        }

        private static string ToAnchor(string s)
        {
            s = Regex.Replace(s, @"[`*_~\[\]():#@]", "");
            s = Regex.Replace(s, @"\s+", "-");
            s = Regex.Replace(s, @"[^\w\-\u4e00-\u9fff]", "", RegexOptions.Compiled);
            return s.ToLowerInvariant();
        }
    }
}
