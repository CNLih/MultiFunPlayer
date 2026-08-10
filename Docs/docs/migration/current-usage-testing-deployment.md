---
sidebar_position: 1
title: 当前使用、测试、运行与部署
description: MultiFunPlayer Web/Linux/fnOS 迁移分支的当前操作手册。
---

# 当前使用、测试、运行与部署

本文档说明截至 **2026-08-10**，当前迁移分支应当如何使用、测试、运行和部署。
命令默认从仓库根目录执行。

:::warning 当前仍是 Beta/验收阶段

Core、Application、Infrastructure、ASP.NET Server、Web SPA 和主要网络输出路径已经可用，
但 Windows WPF 运行验收、Docker 多架构实机、fnOS 安装升级、物理串口和真实设备仍有外部门槛。
不要把本地构建成功等同于 fnOS、ARM 或物理设备已经通过发布验收。

:::

## 1. 选择运行方式

| 方式 | 当前用途 | 是否控制真实后端/设备 | 当前状态 |
| --- | --- | --- | --- |
| ASP.NET Server + Web SPA | Linux/NAS 开发、日常使用和接口验证 | 是 | 当前便携路径的主要入口 |
| Web Demo | 查看界面、演示交互、前端开发 | 否 | 无 Server 时使用确定性模拟数据 |
| Windows WPF | 保留的桌面兼容和回退路径 | 是 | 需要 WindowsDesktop/WPF 实机验证 |
| Docker Compose | 构建并运行合并的 Server + SPA 镜像 | 是 | 配置和静态契约已验证，目标主机执行仍是验收项 |
| fnOS FPK | 在 fnOS 中安装 Docker 应用包 | 是 | FPK 可构建，干净安装、升级、回滚和硬件仍待实机验证 |

建议先使用 **本地 ASP.NET Server + Web SPA** 完成功能验证，再进入 Docker 或 fnOS。

## 2. 环境要求

### 必需

- Git。
- .NET SDK `10.0.302`；版本由仓库根目录的 `global.json` 固定。
- Node.js 22 和 npm；Web 最低声明 Node.js 18，文档和当前构建链建议统一使用 Node.js 22。
- Python 3；验证矩阵、soak、升级演练和目标验收工具需要使用。

### 按需

- Docker Engine 与 Docker Compose v2：容器构建和运行。
- Playwright Chromium 及宿主依赖：浏览器端到端测试。
- 官方 `fnpack`：构建 fnOS FPK。
- Windows 10/11 与 WindowsDesktop Runtime：运行 WPF 主程序和桌面测试。

根目录 `build.sh` 是当前统一入口。先检查它解析到的工具版本和可选能力：

```sh
./build.sh doctor
./build.sh help
```

如果 .NET SDK 安装在默认用户目录 `~/.dotnet`，当前 shell 先执行：

```sh
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"
dotnet --version
```

只执行 `~/.dotnet/dotnet run ...` 仍可能让 MSBuild/GitVersion 子进程找不到
`dotnet`，因此必须同时设置 `PATH`。确认命令可用后，可把前两行加入 `~/.bashrc`
并重新打开终端。Node 安装在用户目录时同样需要把它的实际 `bin` 目录加入
`PATH`。不要把开发机的绝对路径写进项目脚本。

## 3. 本地快速运行 Server + SPA

### 3.1 恢复依赖并构建

```sh
./build.sh bootstrap
./build.sh build Release
```

`build.sh` 会自动优先选择 `~/.dotnet/dotnet` 和
`~/.local/mfp-node/bin/npm`，避免 WSL 误用 Windows Node。Server 构建时会把
`Source/MultiFunPlayer.Web/dist` 复制到自己的 `wwwroot`。
如果没有构建 `dist`，Server 只能提供仓库内的小型静态预览页。

### 3.2 准备本地数据目录

`./build.sh run server` 默认创建 `.local/mfp/{config,keys,scripts,logs}`。如需使用
其他根目录，启动前设置 `MFP_LOCAL_ROOT`，或分别设置四个 `MFP_*_ROOT` 变量。

四个目录的作用：

| 目录 | 内容 |
| --- | --- |
| `config` | 媒体源、输出目标、设备轴、认证哈希和迁移状态 |
| `keys` | ASP.NET Data Protection 密钥环 |
| `scripts` | 用户上传或直接放入的 `.funscript` 文件 |
| `logs` | 有界 JSONL 日志和 File 输出生成的内容 |

### 3.3 配置认证

仅在本机隔离开发时可以不设置认证变量。只要端口能被局域网访问，就应设置认证。

固定访问令牌方式：

```sh
export MFP_AUTH_TOKEN='replace-with-a-random-token-at-least-16-characters'
```

一次性配对方式：

```sh
unset MFP_AUTH_TOKEN
export MFP_PAIRING_TOKEN='replace-with-a-one-time-bootstrap-token'
```

使用一次性配对时，浏览器首次打开会要求输入配对令牌和一个新的、至少 16 字符的访问令牌。
Server 只在 `config/auth-token.json` 中保存新访问令牌的 SHA-256 哈希。

不要把令牌写入 Git、URL 查询参数、截图、日志或可共享的 Compose 文件。

### 3.4 启动

```sh
./build.sh run server
```

自定义监听地址或跳过已经完成的步骤：

```sh
./build.sh run server --url http://127.0.0.1:18080
./build.sh run server --no-restore --no-web-build
```

浏览器打开：

```text
http://127.0.0.1:8080/
```

基本健康检查：

```sh
curl --fail http://127.0.0.1:8080/health/live
curl --fail http://127.0.0.1:8080/health/ready
```

使用固定访问令牌检查受保护接口：

```sh
curl --fail \
  -H "Authorization: Bearer $MFP_AUTH_TOKEN" \
  http://127.0.0.1:8080/api/v1/settings
```

## 4. 当前 Web 使用流程

Web 左侧包含 `Runtime`、`Media sources`、`Scripts`、`Output targets`、
`Device settings` 和 `Logs and health` 六个页面。

建议按以下顺序验证：

1. **Runtime**：确认页面显示 `Server API connected`，健康和 WebSocket 状态正常。
2. **Media sources**：首次测试可使用 `Internal`；网络播放器按 `/api/v1/adapters` 返回的状态选择。
3. **Scripts**：导入 `.funscript`，选择资源并确认 `L0` 等轴值出现。
4. **Output targets**：首次测试建议创建 `File` 输出，Endpoint 填写 `smoke.tcode`，文件会写入 `logs` 根目录。
5. **Output rate**：默认最大值为 20 Hz。低速设备可以配置 5 或 10 Hz；不要为了“更快”把外部设备强制设为不支持的频率。
6. **Connect**：连接 File 输出后，在 Runtime 启动 Engine，并执行播放、Seek、暂停和停止。
7. **Diagnostics**：确认 `sentFrames` 增长，`failedFrames` 为 0，并检查 `lastError` 和 `reconnects`。
8. **Device settings**：在固定轴表中调整范围、反转、插值和限制，然后点击一次
   `Save axis configuration` 保存整组配置。

当前直接实现的输出包括 File、TCP、UDP、WebSocket、Serial、The Handy 和 Buttplug。
媒体源包括 Internal、OFS、Emby、Jellyfin、Plex、VLC 和 MPC-HC/BE。
实际部署时始终以 Web 的 Diagnostics 页面或 `GET /api/v1/adapters` 返回的
`ready`、`planned`、`external-proxy`、`blocked` 状态为准。

### 4.1 Device settings 的轴配置模型

当前便携服务固定使用 11 个标准 TCode 0.3 轴，不允许从 Web 自由增加或删除轴：

| 轴 | 含义 |
| --- | --- |
| `L0` | Up / Down |
| `L1` | Forward / Backward |
| `L2` | Left / Right |
| `R0` | Twist |
| `R1` | Roll |
| `R2` | Pitch |
| `V0` | Vibrate |
| `V1` | Pump |
| `A0` | Valve |
| `A1` | Suction |
| `A2` | Lube |

每行可以调整：

- `Output range`：使用双端滑杆设置 `Minimum` / `Maximum`，把最终输出缩放到指定的
  `0..1` 范围。
- `Interpolation`：选择 `Linear`、`Pchip`、`Makima` 或 `Step`。
- `Inversion`：在限制和输出缩放前反转归一化值。
- `Safety limit`：启用后使用第二个双端滑杆设置 `Limit min` / `Limit max`，先限制
  归一化输入范围。
- `Full travel`：可选的物理行程标定。`L` 轴使用毫米，`R` 轴使用角度，`V/A`
  轴使用百分比。

页面上方的 `Axis motion map` 会实时显示配置结果：

- 外层虚线长方体表示完整归一化运动空间。
- 内层高亮长方体同时考虑输出范围和安全限制；`L2` 映射宽度、`L0` 映射高度、
  `L1` 映射深度。
- 直线与旋转箭头表示实际运动方向；启用反转时箭头方向和轴卡片的起止方向会同步
  反转。`R0`、`R1`、`R2` 分别表示 Twist、Roll 和 Pitch。
- 右侧轴卡片显示有效起止百分比；填写 `Full travel` 后，还会显示完整物理行程和
  当前有效物理行程。

`Full travel` 是标定与显示元数据，不会直接改变 TCode 的归一化输出，也不会绕过
`Output range` 或 `Safety limit`。例如 `L0` 完整行程标定为 `120 mm`，有效范围为
`20%..60%` 时，页面会显示有效移动距离为 `48 mm`。

`Save axis configuration` 会把 11 行作为一个请求提交到
`PUT /api/v1/device-axes`。服务端先验证完整集合，再一次性应用到引擎，并写入单个
`device-axis-settings.json` 文件；容器默认路径为
`/config/device-axis-settings.json`。缺轴、重复轴、未知轴或无效范围都会拒绝整个请求，
不会只保存其中几行。

`Reset all defaults` 只重置浏览器中当前表格，仍需点击保存才会写入服务端。旧的
`PUT /api/v1/device-axes/{axis}` 仍用于兼容单轴客户端；
`DELETE /api/v1/device-axes/{axis}` 现在表示把该固定轴恢复默认值，而不是删除轴。

## 5. 仅运行 Web Demo

Web Demo 不连接 Server，也不会向真实设备发送数据，适合 UI 开发和演示。

```sh
./build.sh run web
```

打开 Vite 输出的地址并添加 `?demo=1`：

```text
http://127.0.0.1:5173/?demo=1
```

不要用 Demo 模式判断后端、认证、文件持久化或设备协议是否可用。

## 6. 测试方法

### 6.1 快速回归

修改便携路径后运行统一回归入口：

```sh
./build.sh test
```

该命令运行四个便携测试项目、Web typecheck/静态检查/构建，以及文档安全检查和构建。
完整解决方案构建使用：

```sh
./build.sh build Release
```

Linux 可以交叉编译 WPF，但不能替代 WindowsDesktop 运行测试。

### 6.2 Web 与文档

浏览器环境完整时运行：

```sh
npm exec --prefix Source/MultiFunPlayer.Web -- \
  playwright install --with-deps chromium
./build.sh test --browser
```

Playwright 浏览器存在但宿主共享库缺失时，测试仍无法启动；CI 会安装完整浏览器依赖。

### 6.3 一键本地验证矩阵

```sh
./build.sh validate
```

当前机器不具备 Chromium 宿主依赖时：

```sh
./build.sh validate --skip-browser
```

`pending` 不是 `pass`。Docker、fnOS、ARM 和物理设备项目必须在对应环境补充证据。

### 6.4 短时 soak

先完成 Release 构建，再运行 30 秒回归：

```sh
./build.sh soak 30
```

默认测试让引擎和输出目标都运行在 20 Hz，因此当前回归门槛要求 0 个错误合并帧。
如果专门测试低于引擎频率的输出目标，合并帧是正常限流行为，应使用对应预算。

发布前的 8 小时命令：

```sh
./build.sh soak 8h
```

检查 `sentFrames`、`coalescedFrames`、`failedFrames`、`reconnects`、p95 更新时间、
RSS、CPU、真实运行时长和 `gracefulStop`。进程级 soak 不能替代容器、fnOS 或真实硬件 soak。

### 6.5 已部署实例黑盒验收

```sh
export MFP_ACCEPTANCE_TOKEN='the-deployed-server-token'

python3 tools/run-target-acceptance.py \
  --base-url http://host-or-nas:8080 \
  --output artifacts/target/target-acceptance.json
```

新安装并使用一次性配对时，同时设置 `MFP_PAIRING_TOKEN`。
该工具验证认证、健康、SPA、能力列表、脚本上传/加载、播放控制和日志脱敏，
但不会声明物理串口、蓝牙、fnOS 固件或设备运动已经通过。

### 6.6 Server 发布包

生成包含 Web SPA 的 framework-dependent 通用包：

```sh
./build.sh package server portable
```

生成 Linux 自包含架构包：

```sh
./build.sh package server linux-x64
./build.sh package server linux-arm64
```

产物位于 `artifacts/server/<target>/`，包括发布目录、`.tar.gz` 和 SHA-256 文件。

## 7. Docker Compose 部署

### 7.1 部署前检查

```sh
./build.sh package validate
docker compose -f deploy/docker/compose.yaml config
```

静态检查只验证仓库中的 Dockerfile、Compose 安全边界、卷和 fnOS 结构；它不会构建镜像。

### 7.2 构建和启动

```sh
export MFP_AUTH_TOKEN='replace-with-a-random-token-at-least-16-characters'
export MFP_HTTP_PORT=8080

./build.sh run docker
```

检查状态：

```sh
./build.sh status docker
./build.sh logs docker
curl --fail http://127.0.0.1:8080/health/live
```

Compose 默认：

- 使用非 root 用户。
- 只读根文件系统和受限 `/tmp`。
- 启用 `no-new-privileges`。
- 挂载 `config`、`keys`、`scripts`、`logs` 四个命名卷。
- 使用 bridge 网络，端口默认为 8080。
- 限制为 2 CPU 和 512 MiB，可通过部署覆盖文件调整。

停止但保留数据：

```sh
./build.sh stop docker
```

删除卷必须同时显式执行
`MFP_CONFIRM_DELETE_VOLUMES=1 ./build.sh stop docker --volumes`。不要在没有备份的
情况下使用它。

### 7.3 TCP/UDP 模拟器

```sh
MFP_AUTH_TOKEN='replace-with-a-long-random-token' ./build.sh run simulators --foreground
```

容器内可使用 `fake-tcp:18000` 和 `fake-udp:18001`。模拟器只用于协议 smoke，不代表真实设备。

### 7.4 串口

串口必须通过 Compose `devices` 显式映射。Linux 优先使用稳定的
`/dev/serial/by-id/...` 路径，并确保容器用户具有读写权限。
不要为了省事默认启用 `privileged: true`。

### 7.5 升级和备份

升级前同时备份四个持久根，并保留当前镜像标签。使用新镜像副本验证健康、认证、
脚本和设备设置后再替换当前实例。详细策略见 [Headless Web Operations](./web-operations.md)。

当前 Compose 默认从源码构建 `multifunplayer:local`；在正式多架构镜像完成发布和扫描前，
不要把本地构建标记为官方 amd64/arm64 发布物。

## 8. fnOS 部署

### 8.1 构建前检查

```sh
./build.sh package validate
```

安装官方 `fnpack` 后构建架构包：

```sh
FNPACK=/path/to/fnpack ./build.sh package fnos x86
FNPACK=/path/to/fnpack ./build.sh package fnos arm
# 或一次构建并验证两个架构
FNPACK=/path/to/fnpack ./build.sh package fnos all
```

验证产物：

```sh
(cd artifacts/fnos/x86 && sha256sum --check multifunplayer.fpk.sha256)
(cd artifacts/fnos/arm && sha256sum --check multifunplayer.fpk.sha256)
```

### 8.2 安装前条件

- FPK 引用的容器镜像必须已经发布，或已经加载到目标 fnOS 主机。
- 目标环境必须提供正确的 `MFP_IMAGE`、认证变量和 fnOS 分配的 `TRIM_*` 变量。
- USB 串口权限需要单独配置，默认不启用。
- x86 包和 ARM 包必须分别验证，不能用交叉编译结果替代真实启动。

### 8.3 安装后验收

1. 从 fnOS Web 入口打开应用。
2. 检查 `/health/live` 和 `/health/ready`。
3. 完成配对或访问令牌登录。
4. 上传并加载脚本。
5. 创建 File 输出完成无硬件 smoke。
6. 运行 `tools/run-target-acceptance.py`。
7. 另行验证安装、重启、升级、回滚、卸载、卷保留和 USB 设备重连。

FPK 构建和静态校验通过，不等于 fnOS 实机验收通过。

## 9. Windows WPF 运行

WPF 是当前保留的桌面兼容和回退路径，只能在 Windows 上运行。在 Git Bash 中：

```sh
./build.sh run desktop Release
```

PowerShell 环境没有 Bash 时仍可直接使用原始 `dotnet restore` / `dotnet run` 命令。

发布前还必须运行 WPF XAML、插件加载、配置迁移、应用启动和优雅关闭测试。
Linux 上设置 `EnableWindowsTargeting=true` 只能证明可交叉编译，不能证明窗口、插件和设备运行正常。

## 10. 常用配置变量

| 变量 | 默认值 | 用途 |
| --- | --- | --- |
| `ASPNETCORE_URLS` | 由宿主决定 | Server 监听地址；容器使用 `http://+:8080` |
| `MFP_CONFIG_ROOT` | `/config` | 持久配置根目录；兼容别名 `MFP_CONFIG_PATH` |
| `MFP_KEYS_ROOT` | `/keys` | Data Protection 密钥；兼容别名 `MFP_KEYS_PATH` |
| `MFP_SCRIPTS_ROOT` | `/scripts` | Funscript 根目录；兼容别名 `MFP_SCRIPTS_PATH` |
| `MFP_LOGS_ROOT` | `/logs` | 日志和 File 输出根目录；兼容别名 `MFP_LOGS_PATH` |
| `MFP_AUTH_TOKEN` | 空 | 固定访问令牌，至少 16 字符 |
| `MFP_PAIRING_TOKEN` | 空 | 一次性配对令牌 |
| `MFP_TICK_HZ` | `20` | 引擎更新频率，限制为 1–20 Hz |
| `MFP_MAX_WEBSOCKET_CLIENTS` | `16` | 最大状态 WebSocket 客户端数，限制为 1–128 |
| `MFP_LOG_CAPACITY` | `500` | 内存日志容量，限制为 50–10000 |
| `MFP_DATA_PROTECTION_KEY_LIFETIME_DAYS` | `90` | 密钥轮换周期，限制为 7–3650 天 |
| `MFP_SERIAL_BY_ID_ROOT` | `/dev/serial/by-id` | Linux 稳定串口发现目录 |
| `MFP_THE_HANDY_CONNECTION_KEY` | 空 | The Handy 连接密钥，不写入配置文件 |
| `MFP_EMBY_API_KEY` | 空 | Emby API 密钥 |
| `MFP_JELLYFIN_API_KEY` | 空 | Jellyfin API 密钥 |
| `MFP_PLEX_TOKEN` | 空 | Plex 访问令牌 |
| `MFP_VLC_PASSWORD` | 空 | VLC Web 接口密码 |

媒体源配置中的 `secretRef` 只保存环境变量名称，不保存秘密值本身。

## 11. 发布前最低检查清单

- [ ] 锁定恢复成功。
- [ ] Core、Application、Infrastructure、Server 测试全部通过。
- [ ] Release 解决方案构建成功。
- [ ] Web typecheck、静态契约和生产构建通过。
- [ ] 文档安全检查和 Docusaurus 构建通过。
- [ ] 本地验证矩阵没有失败项，pending 项已明确归属目标环境。
- [ ] 30 秒 soak 通过。
- [ ] 8 小时 patched-build soak 通过。
- [ ] Docker amd64/arm64 构建、扫描、启动和卷替换通过。
- [ ] fnOS 干净安装、Web 入口、升级、回滚和卸载通过。
- [ ] 物理串口和目标设备连接、断开、重连通过。
- [ ] Windows WPF 和插件回退路径通过。

更多细节：

- [功能兼容矩阵](./feature-matrix.md)
- [运行、认证、备份和故障排查](./web-operations.md)
- [Headless 架构](./web-architecture.md)
- [贡献和验证要求](./contributing-headless.md)
- 仓库根目录的 `WEB_LINUX_FNOS_MIGRATION_PLAN.md`
