# 发布指南

本文描述 ClashUI 当前的发布方法、产物构成与验证步骤。所有裁剪决策都固化在 `ClashUI.App.csproj` 里，**发布只需一条命令**，无需额外参数。

## 标准发布

```bash
dotnet publish src/ClashUI.App/ClashUI.App.csproj -c Release -r win-x64 -o <输出目录>
```

- `-c Release` 必须带：`PublishAot` 只在发布链路生效，日常 `dotnet build` / F5 是普通 JIT，AOT 特有问题（`LibraryImport` 入口点、反射序列化）只在发布时暴露
- `-r win-x64`：NativeAOT 必须指定 RID（csproj 同时声明了 `win-arm64` 供将来交叉编译）
- 发布耗时主要在 `Generating native code`（ILC + 链接），增量下约 1-2 分钟

## 产物构成（约 74MB / 152 文件）

| 内容 | 大小 | 说明 |
|---|---|---|
| `ClashUI.App.exe` | 13MB | 原生 AOT 二进制，全部托管代码已编入，无 .NET 运行时依赖 |
| `Microsoft.ui.xaml.dll` 等 WinAppSDK 运行时 | ~55MB | 自包含拷贝的 XAML/Composition 原生 DLL、`.pri` 资源、winmd 元数据、语言目录 |
| `Microsoft.WindowsAppRuntime.Bootstrap.dll` | ~0.2MB | 自包含模式不参与启动，保留无害 |
| `WebView2Loader.dll` / `Microsoft.Web.WebView2.Core.dll` | ~0.5MB | WebView2 加载器（运行时本身依赖系统安装的 Evergreen WebView2） |
| `Assets/` | 少量 | 应用图标等 |

整体拷贝目录即可在干净的 Win10/11 x64 机器运行，无需安装任何运行时（WebView2 除外，Win11 自带）。

### 已剔除的死重（历史对比：伞包时代 228MB）

1. **WinAppSDK 子包按需引用**：不用 `Microsoft.WindowsAppSDK` 伞包——它强制携带 Widgets/AI/ML/Search 四个子包（onnxruntime 21MB + DirectML 18MB + Search/Widgets 等，共约 45MB），本应用全用不到。当前引用 `WinUI 2.3.6` + `DWrite 2.1.0` + `Runtime 2.4.0`（版本取自伞包 2.4.0 的原始解析结果），其中 `InteractiveExperiences` 显式钉 `2.1.6`（见下文框架依赖一节的校验约束）
2. **原生 pdb 不进发布**：`CopyOutputSymbolsToPublishDirectory=false`，省 63MB。应用日志的堆栈方法名来自二进制内嵌元数据，不依赖 pdb；调试符号在 `bin\Release` 下始终保留。注意 `DebugType=none` 对 AOT pdb 无效，必须用这个属性
3. **WebView2 用户数据目录在数据目录**：`%LOCALAPPDATA%\ClashUI\webview2`（`AppPaths.WebView2DataDir`），缓存膨胀不再污染发布目录（曾经攒到 69MB）

语言目录（~4MB）与 winmd（~3MB）体量小、裁剪风险高，未做处理。

## 发布后验证清单

```bash
# 1. 结束旧实例（提权实例 taskkill 杀不掉，需从托盘「退出」）
taskkill /IM ClashUI.App.exe /F

# 2. 发布（见上）并启动
# 3. 进程在位
tasklist | findstr ClashUI
# 4. 核心 API 可达（端口与 secret 读 %LOCALAPPDATA%\ClashUI\settings.json 的
#    ControllerAddr / Secret，不要假设默认值 9090）
curl -H "Authorization: Bearer <secret>" http://127.0.0.1:<ControllerAddr>/version
# 5. 日志无异常
tail %LOCALAPPDATA%\ClashUI\logs\app.log
```

功能抽验：托盘图标出现且左键切换窗口显示/隐藏；右键菜单完整；`profiles\` 里改一下激活的 YAML，约 1 秒内日志出现「检测到配置文件修改，已热重载」。

## 常见坑

- **MSB3027 exe 被占用**：发布目标目录里有正在运行的实例。先杀再发；**提权运行中的实例 taskkill 无效**（拒绝访问），只能从托盘退出
- **发布成功但启动即退**：单实例互斥（`Local\ClashUI.SingleInstance`）——已有实例在跑时，第二个实例会静默退出（等待重试 20×250ms 是为提权重启场景设计的）
- **换目录 = 重新注册开机自启**：计划任务「ClashUI」绑定 exe 绝对路径；升级若挪了目录，托盘重开一次「开机自启」即可（非管理员下会自动提权重启完成注册）

## 备选：框架依赖（非自包含）

```bash
dotnet publish src/ClashUI.App/ClashUI.App.csproj -c Release -r win-x64 \
  -p:WindowsAppSDKSelfContained=false -o <输出目录>
```

实测 **8 文件 / 约 15MB**（exe 占 13MB）。代价：目标机器必须安装 Windows App Runtime 2.x（版本须与 `Runtime` 包大版本对齐），且框架依赖模式会强制做**组件一致性校验**——这就是 csproj 里钉 `InteractiveExperiences 2.1.6` 的原因（WinUI 2.3.6 传递依赖的是 2.1.3，过不了 Runtime 2.4.0 的校验）。适合能控制运行时安装的场景，默认不采用。

## 不可行：单文件发布

WinUI 3 + 自包含不支持单文件：`PublishSingleFile=true` 实测仍产出 200+ 文件。NativeAOT 的 exe 本身已内嵌全部托管代码，剩余文件是 WinAppSDK 原生运行时与 `.pri` 资源，XAML 资源系统按磁盘路径加载，微软未提供打包机制。分发用整目录压缩包即可。
