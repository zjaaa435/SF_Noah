# SF_Noah（Noah 缺陷 + SF 可视化 + Cursor 一键分析）

`SF_Noah` 是一个 Windows 桌面工具，用于：

- 从 Noah 拉取缺陷详情/评论/附件（使用 TT 登录态）
- 一键下载日志压缩包、选择性导入（提取 `sf_logs.txt` / `android_log_*.txt` / 视频等）
- 打开 SF 3D 可视化窗口（WebView2）
- **每条缺陷一键生成 Cursor 分析包**：自动生成 `.code-workspace`、`.cursorrules`（WMS/窗口规则）、提示词、并尽量把日志证据一并带上
- 支持 **应用内检查更新/自动更新**（GitHub Releases）

---

## 目录

- **用户使用手册（中文）**：`docs/使用说明.md`
- **用户手册（ASCII 文件名兜底）**：`docs/USER_MANUAL.md`（解决部分解压工具中文乱码）
- **发布/更新说明**：见下文（维护者）

---

## 给用户（只看这段即可）

- 解压 `SF_Noah-win-x64.zip`
- 双击 `SF_Noah.exe` 运行
- 详细操作：见 `docs/使用说明.md`（或 `docs/USER_MANUAL.md`）

---

## 给维护者（开发/发布）

### 环境要求

- Windows 10/11
- .NET SDK（建议 `8.x`）

### 本地运行（开发调试）

在 `buglens_lite_cs/BugLensLite` 目录下：

```powershell
dotnet build
dotnet run
```

---

## 发布（生成可双击的 EXE + Release 资产）

### 一键发布脚本

仓库已提供脚本：`tools/publish_win_x64.ps1`

在 **PowerShell** 中运行：

```powershell
cd /d F:\hhhhhh
powershell -NoProfile -ExecutionPolicy Bypass -File tools\publish_win_x64.ps1
```

输出物：

- 发布目录：`dist/win-x64/`
- Release 资产 zip：`dist/SF_Noah-win-x64*.zip`（如 zip 被占用会自动加时间戳）

> 说明：脚本使用 `dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true -p:SelfContained=true`，产物为可双击运行的 `SF_Noah.exe`，无需命令行启动。

### （更简单）只打 Tag 自动发 Release（推荐）

仓库内已提供 GitHub Actions：`.github/workflows/release.yml`

以后你发布只需要：

1. 修改 `BugLensLite.csproj` 的版本号（`Version/FileVersion/AssemblyVersion`）
2. 提交代码后打 tag 并 push：

```powershell
git tag v1.2.3
git push origin v1.2.3
```

Actions 会自动：

- 运行 `tools/publish_win_x64.ps1`
- 生成 `dist/SF_Noah-win-x64*.zip`
- 创建 GitHub Release 并把 zip 作为资产上传

---

## GitHub Releases（不带源码、仅发资产）

建议流程：

- **打 Tag**：使用语义化版本，并以 `v` 开头，例如 `v1.2.3`
  - 应用内更新会解析 `tag_name`，因此必须能解析成版本号
- **创建 Release**：上传 `dist/SF_Noah-win-x64.zip` 作为 Release Asset
- **不提供源码**：只上传 Release Assets（zip/exe），不要上传仓库源码压缩包到 Release 附件

---

## 应用内更新（用户无需重新下载 zip）

工具内有“检查更新”按钮，更新逻辑为：

1. 请求 GitHub API：`/repos/<owner>/<repo>/releases/latest`
2. 选取 `assets` 里最合适的下载项（优先 `.zip`）
3. 下载到临时目录并解压覆盖当前目录
4. 重启 `SF_Noah.exe`

### 用户侧配置（一次即可）

更新源通过环境变量配置：

- 环境变量名：`SF_NOAH_GITHUB_REPO`
- 值：`owner/repo`（例如 `yourOrg/SF_Noah`）

用户执行（cmd 或 PowerShell 均可）：

```powershell
setx SF_NOAH_GITHUB_REPO "yourOrg/SF_Noah"
```

> 说明：如果未配置该变量，工具会提示“未配置更新源”。

> 进一步简化：如果你希望用户连 `setx` 都不用做，可以把默认更新源仓库写入代码（`AppUpdateService.DefaultRepo`）。



