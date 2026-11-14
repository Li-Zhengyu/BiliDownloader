# 🎬 BiliDownloader — B站视频下载器

![License](https://img.shields.io/badge/license-GPLv3-blue.svg)

一个基于 **C# / .NET 8 / WPF**（桌面）与 **.NET 9 / Avalonia**（Android）的 B站（bilibili）视频下载器，界面美观、操作简单、单文件免安装、运行高效。

> ⚠️ **仅供个人学习、研究、交流使用，请勿用于商业用途或传播侵权内容。** 下载内容版权归原作者所有，请尊重 UP 主的劳动成果。

---

## ✨ 功能特性

- ✅ **单视频 / 多P / UGC合集批量下载**
  - 解析视频后自动显示所有分 P，勾选即可批量下载
  - 自动识别视频所属的 **UGC 合集（多集）**，可勾选整季批量下载
- ✅ **画质选择**：360P / 480P / 720P / 1080P / 4K（登录后可获取更高画质，自动选最高可用）
- ✅ **仅音频模式**：只下载音频流（.m4a），体积小、速度快
- ✅ **扫码登录 / 粘贴 Cookie 登录**：登录后可下载高清/4K 内容
- ✅ **下载队列管理**：单个删除、清空、逐任务暂停、暂停全部、恢复
- ✅ **失败自动重试**：可配置重试次数，失败自动切换备用 CDN 节点
- ✅ **并行任务数可调**：避免同时下载过多拖垮网络与磁盘
- ✅ **断点安全**：CDN 下载强制携带 Cookie，杜绝"18 字节空文件"问题
- ✅ **缓存目录可配置**：临时文件可放到空间充足的盘，下载完成自动清理，C 盘不再爆满
- ✅ **智能合流**：自动检测 ffmpeg，缺失时弹窗提供"自动下载 / 兼容模式拼接 / 保留 m4s"三种选择（会话内只询问一次）
- ✅ **亚克力半透明界面 + 内置模糊动态壁纸背景**（与手机端同一套视觉），支持窗口缩放（最小 960×640），列表可滚动
- ✅ **手机端（Android）**：独立触屏界面，亚克力半透明风格 + 内置动态壁纸背景（可换成自己的图片）、深色/浅色/跟随系统主题，支持链接解析、多P/合集勾选、画质与并行设置、下载队列、B站官方网页登录、分享链接导入、系统目录选择、前台服务保活；只输出可直接播放的 MP4（最高 1080P）

---

## 📦 环境要求

| 依赖 | 说明 |
|------|------|
| [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) | 桌面端开发/编译需要 |
| [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) | Android 手机端编译需要（目标 `net9.0-android`） |
| Windows 10 / 11 | 桌面版运行需要 |
| Android 7.0（API 24）及以上 | 手机版运行需要 |
| ffmpeg（可选） | 用于无损合并音视频；桌面版缺失时可自动下载或使用兼容模式 |

---

## 🚀 快速开始

### 下载现成版本

前往 [Releases](https://github.com/Li-Zhengyu/BiliDownloader/releases/latest) 下载最新版本：

| 文件 | 说明 |
|------|------|
| `BiliDownloader_v*_win-x64_self-contained.exe` | Windows 单文件免安装，双击即用（无需安装 .NET） |
| `BiliDownloader_v*_win-x64_framework-dependent.exe` | 体积小，需先安装 .NET 8 Desktop Runtime |
| `BiliDownloader_v*_Android_arm64.apk` | Android 7.0+（arm64-v8a），Debug 签名，可直接安装 |

### 方式一：直接运行源码

```powershell
dotnet run
```

### 方式二：发布单文件 exe（免安装）

```powershell
# Release 单文件自包含发布（无需目标机器安装 .NET）
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

生成的 `publish/BiliDownloader.exe` 单个文件即可分发，双击运行。

### 方式三：构建 Android 手机版

> 手机端目标框架为 `net9.0-android`，需要 **.NET 9 SDK**（桌面端仍可用 .NET 8 SDK 构建）。
> 由于 Avalonia 与 Android SDK 的多 ABI 内层构建存在路径冲突，手机端请 **指定单个 ABI 构建**（现代手机用 `android-arm64` 即可）。

```powershell
# 首次编译前安装 Android workload
dotnet workload install android

# 构建调试版 APK（使用自动生成的 debug 签名，可直接安装到手机）
dotnet build CrossPlatform/BiliDownloader.Android/BiliDownloader.Android.csproj -c Debug -p:RuntimeIdentifier=android-arm64
```

APK 输出在 `CrossPlatform/BiliDownloader.Android/bin/Debug/net9.0-android/android-arm64/com.bilidownloader.app-Signed.apk`。
项目里已内置 `_DisableAvaloniaXamlCompilationInInnerBuild` 目标，用于规避 Avalonia 11.2.x 在 Android 内层 RID 构建中重复拼接 `Avalonia/` 目录导致的 `XAPRAS7023` 错误。
若项目放在网络/虚拟磁盘（如 Z:）上打包时报 `XABLD7000 Failure to create temporary file`，把源码复制到本地磁盘（如 `C:`）后再执行上面的构建命令即可。

构建完成后得到的 `com.bilidownloader.app-Signed.apk`（arm64-v8a，Debug 签名）可直接安装到手机。
手机端默认下载到应用专属目录 `Android/data/com.bilidownloader.app/files/Download`，无需存储权限；也可在界面点「选择」用系统目录选择器指定其他目录，下载完成后自动复制过去。

> Debug 构建默认启用快速部署（Fast Deployment），程序集不打进 APK，用 `adb install` 直接安装会启动即 `SIGABRT`（`No assemblies found ... Exiting`）。项目已在 csproj 中设置 `EmbedAssembliesIntoApk=true`，产物自包含，可直接安装。

> 手机端只输出**可直接播放的 MP4**：优先请求 B站 durl 单文件（最高 1080P）；若该视频只提供 DASH 音视频流，会用 Android 系统 `MediaMuxer` 自动合流成标准 MP4，不再保留 `.m4s`。

---

## 🖱️ 使用指南

1. **粘贴链接**：输入视频 / 合集链接（支持 `BV`、`av` 格式），回车或点击「🔍 解析」
2. **选择内容**：勾选需要的分 P 或合集分集（支持全选 / 取消全选，列表可滚动）
3. **设置选项**：
   - 画质（360P ~ 4K）
   - 重试次数（0/1/2/3/5）
   - 并行任务数（1/2/3/4/6）
   - 仅音频（勾选后只下载音频）
   - 保存目录（📁 按钮）
   - 缓存目录（🗂 按钮，建议选空间充足的盘）
4. **加入队列** → **▶ 开始全部**
5. 下载过程中可**暂停 / 暂停全部**，之后点「开始全部」恢复（暂停后重新下载该任务）
6. 完成后到保存目录查看文件

### 登录（可选，建议）

- 点击右上角「🔑 登录」→ 弹出二维码 → **用 B站 App 扫码确认**
- 或选择「粘贴 Cookie」：从浏览器复制 `SESSDATA` / 完整 Cookie 粘贴

> 登录后可获取更高画质（1080P+ / 4K）。不登录也能下载 720P 及以下。

### 手机端使用

1. 粘贴视频链接或 `BV` 号，点击「解析视频」；也可在 B站 App 里「分享」视频到本应用，自动填入并解析
2. 勾选分 P / 合集分集，设置画质（最高 1080P，手机端自动使用单文件 MP4）、并行数
3. 「保存目录」点右侧「选择」调起系统目录选择器，下载完成后自动复制到所选目录
4. 点击「加入下载队列」，再点底部「开始」；队列内可单独暂停 / 移除
5. 右上角「登录」会打开 B站 官方网页登录页，登录成功后自动保存登录状态（也可粘贴 Cookie / SESSDATA）
6. 下载期间应用会启动前台服务并显示常驻通知，切后台不会被系统回收
7. 未选择目录时文件保存在 `Android/data/com.bilidownloader.app/files/Download`
8. 界面为亚克力半透明卡片 + 内置动态壁纸背景；「背景图」可点「选择」换成自己的图片，点「默认」还原动态壁纸
9. 「主题」可在 跟随系统 / 浅色 / 深色 之间切换，选择会记住

> 手机端竖屏锁定；平板（最小宽度 ≥ 600dp）跟随重力自动横竖屏。

> 若启动即闪退，可查看 `Android/data/com.bilidownloader.app/files/BiliDownloader/crash.log`（应用内启动各阶段与异常都会记录在这里），把该文件发回即可定位问题。

---

## ⚙️ 配置说明

配置文件位于 `%LocalAppData%\BiliDownloader\config.json`：

| 配置项 | 默认值 | 说明 |
|--------|--------|------|
| `OutputDir` | 视频库/BiliDownloader | 下载保存目录 |
| `CacheDir` | `%TEMP%\BiliDownloader` | 临时文件目录（建议改到空间大的盘） |
| `RetryCount` | 3 | 下载失败重试次数 |
| `MaxParallel` | 3 | 最大并行下载任务数 |
| `FfmpegPath` | 空 | 手动指定的 ffmpeg 路径 |
| `Login` | 空 | 登录凭证（SESSDATA 等） |

---

## 🛠️ 常见问题（FAQ）

**Q：为什么下载的 4K/1080P 需要登录？**
A：B站高画质接口要求登录凭证，请先扫码登录。

**Q：下载的文件只有几字节 / 报错？**
A：本工具已强制携带 Cookie 并过滤 PCDN 节点，正常情况下不会出现；若仍异常请重试或换画质。

**Q：合流提示需要 ffmpeg？**
A：弹窗选择「是」自动下载 ffmpeg（约 80MB，一次下载永久使用）；「否」走兼容模式（简单拼接，部分播放器可播）；「取消」保留原始 m4s 文件。

**Q：暂停后为什么从头下载？**
A：B站 CDN 下载链接有时效性，暂停后重新开始会重新下载该任务，以保证文件完整。

**Q：C 盘空间被占满？**
A：把缓存目录（🗂）改到空间充足的盘，程序会在每个任务结束后自动清理临时文件。

---

## 🧱 技术实现

- **WPF**（原生控件 + 深色主题，无第三方 UI 框架，体积小、启动快）
- **B站 API**：
  - 视频信息 / 分P：`x/web-interface/view`
  - 播放地址：`x/player/playurl`（durl 单文件）与 `x/player/wbi/playurl`（DASH 高清，含 wbi 签名）
  - 扫码登录：`passport.bilibili.com` 系列接口
  - UGC 合集：`seasons_archives_list`
- **wbi 签名**：标准实现（mixin key 重排 + md5）
- **下载策略**：durl 单文件（免登录最稳）→ DASH（登录高画质），CDN 强制带 Cookie、跳过带端口 PCDN 节点、失败自动切备用 URL
- **合流**：ffmpeg 无损合并（`-c copy`），缺失时兼容拼接兜底
- **二维码**：QRCoder

### 目录结构

```
├── App.xaml / App.xaml.cs        # 应用入口、全局异常捕获
├── MainWindow.xaml(.cs)          # 主界面 + 交互逻辑
├── BiliClient.cs                 # B站 API 层（wbi 签名、登录、解析、合集）
├── Downloader.cs                 # 下载器（Cookie、重试、备用URL、进度）
├── Merger.cs                     # ffmpeg 查找/合并/下载
├── SubtitleConverter.cs          # 字幕 JSON→SRT
├── Models.cs                     # 数据模型
├── Config.cs                     # 配置读写
├── BiliDownloader.csproj         # 项目文件（含单文件发布配置）
└── CrossPlatform/                # 跨平台版本（Avalonia）
    ├── BiliDownloader.Core/      # 桌面 / 手机共用：API、下载、合流、配置
    ├── BiliDownloader.Desktop/   # 跨平台桌面端界面
    ├── BiliDownloader.Android/   # Android 手机端（MobileApp / MobileMainView / Manifest）
    └── BiliDownloader.Cross.sln  # 跨平台解决方案
```

---

## 📄 许可证

本项目采用 **GNU General Public License v3.0**（GPL-3.0），详见 [LICENSE](LICENSE)。

- ✅ 允许：商用、修改、分发、私用
- 📌 要求：**基于本项目的衍生作品必须同样以 GPL 协议开源**、保留版权声明
- 🚫 禁止：将本项目或其衍生作品用于闭源商用（请同时遵守上文免责声明，勿用于侵权用途）

---

## 📄 免责声明

本项目仅用于**个人学习与编程技术研究**，不提供任何形式的商业服务。请勿将本项目用于任何侵犯他人合法权益的用途。使用本项目造成的一切后果由使用者自行承担。

若涉及侵权问题，请联系删除。

---

## ⭐ 支持

如果这个项目对你有帮助，欢迎 **Star ⭐** 支持！
