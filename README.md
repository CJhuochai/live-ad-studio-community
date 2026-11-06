# Live Ad Studio

Windows 11 x64 本地直播投流分析工作台。复用2.0的WinForms/WebView2、官方登录授权及XLSX读取；未新增第三方依赖。分析与记录不操作广告计划。

## 构建

安装.NET 8 SDK，保留本仓库三个源码目录的相对位置。

```powershell
dotnet restore LiveAdStudio/LiveAdStudio.csproj -r win-x64 --source https://api.nuget.org/v3/index.json
dotnet build LiveAdStudio/LiveAdStudio.csproj -c Release --no-restore
dotnet publish LiveAdStudio/LiveAdStudio.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --no-restore -o publish
```

## 自检

GitHub Actions 在推送到 `main` 或提交 PR 时构建 Windows x64 单文件程序并运行离线自检。

```powershell
$p = Start-Process -FilePath '.\publish\LiveAdStudio.exe' -ArgumentList @('--check', "$PWD\checks.txt") -WindowStyle Hidden -Wait -PassThru
Get-Content checks.txt,checks.txt.auth,checks.txt.upgrade
```

自检使用虚构的订单和素材，不读取账号凭据或发起模型请求。可以在本机追加订单、增量报表两份 XLSX 的路径，验证表格读取。

## 在线更新

客户端启动时及运行期间每 15 分钟检查一次 GitHub Releases。发现版本更高且有 SHA-256 的正式 Windows 包时，右上角显示「更新并重启」。暂停盯盘后点击，程序校验下载包，备份本机工作区，退出后替换 exe 并重启。

发布版本用 `vX.Y.Z` 标签，Windows 附件命名为 `LiveAdStudio-vX.Y.Z-Win11-x64.zip`，内含一份 `LiveAdStudio.exe`。客户端读取本仓库匿名可访问的 Releases。首次安装支持更新的版本需手动替换 exe。

## 参与开发

提交 PR 前请运行 Windows 构建和离线自检。问题报告和测试样例只使用虚构数据。提交前检查 Git 作者邮箱，建议启用 GitHub 提供的 noreply 地址。

## 核心位置

- `Decision.cs`：经营换算、证据诊断、范围筛选、区间复查。
- `DecisionCommands.cs`：先校验再写入；已复查行动锁定。
- `DecisionContext.cs`：经营约束、最新事实及未完成行动优先的AI输入；不含结构化订单号/UID。
- `MaterialImport.cs`：视频/图文效果报表，未知保持未知。
- `Workspace.cs` / `WorkspaceArchive.cs`：v2→v3与备份；OAuth凭据不进入工作区。
- `Ui`：桌面程序内嵌界面。

规则是可解释的检查提示，不掌握平台内部推荐算法，不保证ROI和收益。当前版本AI无联网搜索；官方文档更新需重新核验。实际WebView2、跨电脑登录、屏幕采集和真实直播效果仍需目标电脑验证。
