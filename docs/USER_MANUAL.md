# SF_Noah 使用说明（ASCII 文件名兜底）

说明：部分解压工具会把中文文件名显示成乱码（例如 `浣跨敤璇存槑.md`）。如果你遇到这种情况，请打开本文件。

---

## 1. 安装/启动

- 从 GitHub Release 下载 `SF_Noah-win-x64.zip`
- 解压到任意目录（建议 `D:\Tools\SF_Noah\`）
- 双击运行 `SF_Noah.exe`

> 依赖：工具使用 WebView2（Edge 内核）。大部分 Win10/11 自带；如启动白屏/崩溃，请先安装 WebView2 Runtime。

---

## 2. TT 登录（必做）

点击顶部 **TT登录** 完成登录。

登录成功后：

- 左上角会显示 `auth: ok`（或类似状态）
- `operator: <工号>` 会自动识别（无需手动输入）

> 附件接口后端强制要求 `operator`，本工具会自动从 TT 登录态识别并携带。

---

## 3. 拉取缺陷 / 查看信息

### 3.1 拉取缺陷

在左侧输入框输入 `bugId`（例如 `10638213`），点击 **拉取/刷新**。

缺陷会出现在上方表格中，点击行即可在下方 Tab 查看：

- **基础信息**：关键字段卡片化展示（描述已去除 HTML 标签）
- **附件**：附件卡片化展示，点击图标下载
- **评论**：卡片化展示（作者优先使用 `createdOnBehalfOf`）
- **更多字段**：Noah 原始字段/JSON 便于排查

### 3.2 右键删除缺陷

在表格行上 **右键 → 删除缺陷**：只会从当前列表移除，不会影响 Noah 数据。

---

## 4. 日志：下载 / 导入 / SF 可视化

### 4.1 下载日志

表格列 **日志 → 下载日志**：下载缺陷对应日志压缩包。

默认下载目录：

- `C:\Users\<你>\Downloads\BugLensLiteLogs\<bugId>\`

### 4.2 导入日志（选择性导入，速度更快）

表格列 **导入 → 导入日志**：扫描压缩包内容并选择性解压（避免全量解压导致过慢）。

默认导入缓存目录：

- `%LOCALAPPDATA%\BugLensLite\imports\<bugId>\`

### 4.3 打开 SF 可视化窗口

导入完成后工具会打开独立的 **SF Viewer** 窗口（3D 视图）。

说明：

- 工具已移除 SF 页面里的“上传文件”入口（避免误操作）
- 视频会在 SF 页面内以“悬浮窗”方式显示（尺寸自适配）
- 对下一帧消失的图层：会在前一帧以虚线样式提示

---

## 5. Cursor 一键分析（每条缺陷一键生成分析包）

### 5.1 你会得到什么

在表格列 **分析 → Cursor分析** 点击后，工具会生成一个 “Cursor 分析包” 并尝试自动打开 Cursor。

分析包位置：

- `%LOCALAPPDATA%\BugLensLite\cursor\<bugId>\<时间戳>\`

目录内关键文件：

- `.cursorrules`：**数字人强制规则（WMS/窗口 only，排除 WiFi/WLAN）**
- `cursor_prompt.md`：完整提示词（含证据路径/要求/输出格式/上传步骤）
- `cursor_chat_prompt.txt`：短提示词（已自动复制到剪贴板，直接粘贴到 Cursor Chat）
- `SF_Noah_bug_<bugId>.code-workspace`：Workspace（把日志/导入目录/窗口文档一起挂载）
- `window_docs/`：窗口领域文档（随工具内置复制出来，用户无需拉数字人仓库）
- `tools/core/`：Noah 上传脚本（用户确认后执行）

> 日志传递规则：优先携带“下载的原始压缩包”；如果压缩包不超过 512MB，会复制到分析包里，避免 Cursor 看不到“整包”。

### 5.2 Cursor 必须遵守的规则

- **只做 WMS/窗口领域分析**，明确排除 WiFi/WLAN
- 结论完成后：**必须先询问用户是否上传到 Noah**（未确认禁止上传）

### 5.3 上传到 Noah（需用户确认）

分析包内已包含上传工具：

- `tools/core/defect_comment_formatter.py`
- `tools/core/update_bug_comment_api.py`

其中 `update_bug_comment_api.py` 会在生成分析包时自动把默认 `commentator` 改为你当前 TT 登录识别到的工号（`operatorId`），确保评论作者正确。

---

## 6. 检查更新（应用内更新）

点击顶部 **检查更新**。

更新源仓库有两种方式（满足其一即可）：

- 方式 A（GitHub，默认无需配置）：使用维护者内置的 GitHub Releases 更新源
- 方式 A-override（GitHub）：用环境变量覆盖默认更新源（只需要一次）
- 方式 B（内网）：配置内网更新源（只需要一次，且**需要先 TT 登录用于鉴权**）
- 方式 C（更省事）：由维护者在发布版内置默认更新源（无需任何配置）

方式 A-override：环境变量配置：

```powershell
setx SF_NOAH_GITHUB_REPO "owner/repo"
```

方式 B：内网更新源（推荐你们当前场景）

```powershell
setx SF_NOAH_UPDATE_FEED_URL "https://intranet.example.com/sf_noah/latest.json"
```

版本号要求：

- GitHub Release Tag 必须类似 `v1.2.3`
- Release Asset 必须包含 `.zip`（推荐 `SF_Noah-win-x64.zip`）

---

## 7. 常见问题（FAQ）

## 8. 发表评论（直接写入 Noah 讨论区）

前提：

- 必须先 **TT登录**（用于识别你的工号 `operatorId` 作为评论人）
- 必须先选中一个缺陷（bugId）

步骤：

- 选中缺陷后，点击顶部 **发表评论**
- 在弹窗中输入评论内容，点击 **发布**
  - 默认会把换行转换为 `<br>`（更适配 Noah 的富文本显示）

发布成功后工具会自动刷新“评论”页签。

### 7.1 点击 Cursor分析 后 Cursor 打开“啥也没有”

通常是 Cursor 没有正确打开 workspace。

处理：

- 打开分析包目录：`%LOCALAPPDATA%\BugLensLite\cursor\<bugId>\<时间戳>\`
- 用 Cursor 手动打开 `SF_Noah_bug_<bugId>.code-workspace`
- 打开 `cursor_prompt.md`，把 `cursor_chat_prompt.txt` 的内容粘贴到 Chat

### 7.2 无法获取附件 / 提示 operator 为空

先确认：

- 已点击 **TT登录** 并成功
- 顶部显示 `operator: <工号>`

若仍失败，截图 Response JSON 给维护者排查接口字段变化。

### 7.3 下载/导入很慢

- 导入已做“选择性解压”，如果仍慢通常是压缩包巨大/磁盘慢
- 建议把工具放在 SSD 目录，且避免在网盘目录运行


