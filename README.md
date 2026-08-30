# Clashui

mihomo（Clash Meta 内核）的 Windows 托盘伴侣：管理核心进程与系统集成，面板用 WebView2 内嵌 metacubexd。WinUI 3 + NativeAOT。

## 现有功能（M0）

- WebView2 内存优化：面板隐藏到托盘时切 `MemoryUsageTargetLevel.Low`（实测工作集 ~680MB → ~70MB），显示时恢复；环境关闭跟踪防护 + `--renderer-process-limit=1`
- 托盘常驻：左键打开面板（最大化窗口），右键菜单操作；关闭窗口即隐藏到托盘，真正退出走托盘菜单；图标随状态变化（核心未运行灰化 / 系统代理绿点 / TUN 橙点，经典 Clash 猫头，取自 clash-verge-rev）
- 静默启动：托盘「静默启动」开关（写入 settings.json），或 `--silent` / `-s` 参数；静默模式下不创建主窗口，仅托盘运行，首次点托盘才创建窗口
- 配置文件切换：托盘「配置文件」子菜单列出 `profiles\` 下所有 YAML，点击切换并热重载（失败自动整核重启）
- 核心进程托管：启动 / 停止 / 崩溃自动重启（3 秒后拉起）；核心挂载 kill-on-close Job，应用强杀/崩溃不残留孤儿进程
- TUN 模式开关（需管理员，见下）
- 系统代理开关（写注册表 + 广播刷新，立即生效）；启动时自动清理崩溃残留的指向本应用端口的系统代理（防开机断网）
- 配置合成：订阅 profile 原样保留，注入端口 / secret / external-controller / external-ui / tun / dns
- mihomo 自动下载面板（`external-ui-url`，默认 metacubexd gh-pages）；面板地址带 hostname/port/secret 深链，首次打开自动连接，免手填
- 开机自启：计划任务（ONLOGON + 最高权限，登录不弹 UAC）
- 关闭窗口 = 隐藏到托盘；单实例互斥，再启动自动激活转发（双击 exe 弹出面板）

## 目录

```
src/Clashui.Core   核心管理 / 配置合成 / 系统代理 / 提权 / 计划任务（无 UI 依赖，IsAotCompatible）
src/Clashui.App    WinUI 3 外壳：托盘（H.NotifyIcon）、主窗口（WebView2）、编排器
scripts/           图标生成等工具脚本
```

## 数据目录

`%LOCALAPPDATA%\Clashui\`

| 路径 | 说明 |
|---|---|
| `mihomo.exe` | 核心二进制（可选）：支持数据目录 / `settings.json` 的 `MihomoPath` / PATH 环境变量三种来源，按此顺序解析 |
| `profiles/` | 订阅 / 手写 profile（YAML 原样保留） |
| `config.runtime.yaml` | 合成后的运行时配置，核心实际加载的文件 |
| `settings.json` | 应用设置（端口、secret、开关状态） |
| `ui/` | mihomo 自动下载的面板文件 |
| `logs/` | `app.log`（应用）与 `core.log`（核心输出） |

## 使用

1. `dotnet publish src/Clashui.App -c Release`（NativeAOT，产物在 `bin\Release\...\publish\`）
2. mihomo 三选一：数据目录放 `mihomo.exe` / `settings.json` 设 `MihomoPath` / 直接用 PATH 里的（如 scoop 安装的）；把订阅 YAML 放进 `profiles\`，托盘「配置文件」子菜单选择（或 `settings.json` 的 `ActiveProfile`，不填用 default.yaml）
3. 启动 Clashui.App.exe —— 若开启 TUN 会弹 UAC 提权重启一次；之后用托盘「开机自启」注册计划任务，登录即静默提权运行
4. 静默启动：托盘勾选「静默启动」后所有启动均不显示窗口；也可带 `--silent`（`-s`）参数启动。开机自启的计划任务已自动携带该参数

## TUN / 提权设计

应用本体 `asInvoker` 启动；需要 TUN 时自动以管理员重启自身（首次一次 UAC）。日常方案：注册计划任务后由任务计划以最高权限启动，全程无 UAC。未来演进：独立 Windows Service 持有核心（参考 clash-verge-service）。

## 已知限制 / 待办

- [x] ~~核心进程加入 Job Object~~（已完成）
- [ ] 订阅管理 UI（M1 剩余）：订阅 URL 下载/更新、profile 增删改名（切换与热重载已由托盘子菜单实现）
- [ ] 核心自动下载与更新（M2，GitHub Releases + 哈希校验）
- [x] ~~WebView2 用户数据目录迁到数据目录~~（已迁至 `%LOCALAPPDATA%\Clashui\webview2`）
- [x] ~~面板首次打开需在 metacubexd 设置页填一次 `127.0.0.1:9090` + secret~~（DashboardUrl 已带 `#/setup?hostname=&port=&secret=` 深链，setup 页自动连接）
- [x] ~~单实例目前是「第二个实例直接退出」，未做激活转发~~（第二实例发命名信号 + `AllowSetForegroundWindow` 转授前台权，第一实例弹出面板；`--silent` 再启动不转发）

## AOT 注意事项

- 全部 P/Invoke 走 `LibraryImport`（源生成）；JSON 走 `JsonSerializerContext`
- YAML 只用 YamlDotNet 文档模型（无反射反序列化）；YamlDotNet 18 中节点变更必须经 `Children`（`IDictionary`），节点自身索引器只读
- XAML 全部用编译期绑定 / 代码后置直接引用，未用反射型 `{Binding}`
