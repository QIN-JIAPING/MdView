# MdView

轻量级 Markdown 文件查看器，基于 WPF + WebView2 + Markdig。

## 特性

- **实时预览** — 打开 .md 文件即时渲染，修改文件自动刷新
- **双栏对比** — 分屏模式同时查看两个 Markdown 文件（Ctrl+T）
- **亮色/暗色** — 蓝紫主题配色，一键切换（Ctrl+Shift+D），CSS 平滑过渡
- **拖放打开** — 支持拖入 .md 文件直接查看
- **最近文件** — 侧边栏历史记录，搜索筛选
- **代码高亮** — 集成 Prism.js，支持 C#/Python/JS/TS/JSON/YAML 等
- **前端元数据** — 自动解析 YAML Front Matter 并展示为卡片
- **缩放** — Ctrl+0 / Ctrl++ / Ctrl+- 调整字号

## 快捷键

| 按键 | 功能 |
|------|------|
| `Ctrl+O` | 打开文件 |
| `Ctrl+T` | 分栏切换 |
| `Ctrl+F` | 查找 |
| `F5` | 刷新 |
| `Ctrl+Shift+D` | 切换主题 |
| `Ctrl+0` | 重置缩放 |
| `Ctrl+=/-` | 放大/缩小 |
| `Esc` | 取消面板选中 |

## 构建

```bash
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:EnableCompressionInSingleFile=true \
  -o publish
```

## 依赖

- [.NET 6](https://dotnet.microsoft.com/)
- [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)
- [Markdig](https://github.com/xoofx/markdig)
- [Prism.js](https://prismjs.com/) (CDN)

## 图标

运行 `create_icon.py` 可重新生成 app 图标：

```bash
python create_icon.py
```

## 许可

MIT
