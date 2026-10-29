---
title: 按功能性能复查与实测
description: 播放、路由、输出、Web 与 3D 的 CPU、内存和空闲调度审查。
---

# 按功能性能复查与实测

日期：2026-10-26。基线：`afbbde9d6e022478e534008fcb741c884e95cbb8`，分支
`feature/web-linux-fnos-migration`；优化版本为该提交之上的当前工作树。

本轮已经修复后端空闲周期计算、重复快照分配、前端无条件定时刷新和 3D 持续绘制。
播放仍以原来的 20 Hz 运行；暂停后由操作和状态变化唤醒。
实测主要收益在空闲 CPU，常驻内存下降较小，尚不能证明活动播放 CPU 降低或长期没有泄漏。

## 逐功能审查

下列路径以仓库根目录为起点，省略公共的 `Source/` 前缀。

| 功能 | 问题或检查结果 | 当前处理 |
| --- | --- | --- |
| 播放引擎 | 停止、暂停时仍每秒执行约 20 次 Tick、生成快照并输出；每帧复制配置和输出列表 | `MultiFunPlayer.Application/HeadlessPlayerEngine.cs` 在空闲 Tick 复用快照；配置和 sink 列表仅变更时重建。播放、Seek、载入和配置命令仍发布状态。 |
| 引擎调度与订阅 | 定时器常驻，Tick 后还重复进入广播回调 | `MultiFunPlayer.Server/EngineTicker.cs` 在非播放状态等待容量为 1 的唤醒通道，播放时才启用 PeriodicTimer；广播由引擎事件驱动。 |
| 路由 | 空路由、断开输出、暂停时也计算；每轮复制路由、清理缓存；内部全局脚本重复插值 | `RuntimeRoutingRegistry.cs` 按媒体、输出、路由和引擎事件唤醒；有可播放路由才保留周期计时器，跳过断开输出，内部全局路由复用引擎结果。 |
| 脚本解析与缓存 | 曾使用过的路由脚本一直保留；错误文件可能被每帧重新读取、解析 | `RuntimeScriptEvaluator.cs` 在路由替换时移除不再引用的脚本，成功和失败缓存各限 128 项，失败重读至少间隔 5 秒。上传/修改导致的 Invalidate 可立即清理失败记录并唤醒。 |
| 媒体源 | HTTP 适配器需要轮询发现外部播放变化；相同暂停状态不应持续唤醒路由 | `MediaSourceRegistry.cs` 只对变化的状态发唤醒事件。保留已连接 HTTP 适配器默认 500 ms 轮询；WebSocket 接收异步等待数据。 |
| 网络、文件与设备输出 | 队列已有容量限制，消费者异步等待；无数据时无需循环发送相同值 | 保留现有有界队列、限速和异步传输。`OutputTargetRegistry.cs` 连接时补发当前姿态；播放期间保留周期输出。真实设备所需的保活、超时行为仍需实机验收。 |
| Web 状态与 DOM | 服务端模式也运行固定 100 ms 前端 tick，所有页面都有 500 ms 媒体轮询 | `MultiFunPlayer.Web/src/main.ts`、`store.ts` 用 WebSocket 驱动状态更新，DOM 状态刷新最多约 10 Hz。媒体轮询仅在可见 Runtime 页面执行，播放时 500 ms、空闲 2 s，串行请求避免重叠。 |
| 页面隐藏与重连 | 隐藏页仍接收推送、运行模拟；连接失败可能残留 store/socket | 隐藏时停模拟、UI 定时任务和状态 socket；恢复后重新订阅当前状态。重连采用 1–30 s 退避；关闭和替换 store 时清理订阅。已经在途的 HTTP 请求可能完成一次。 |
| 3D 加载与绘制 | 进入设置自动加载大型库；包装层与库内部都请求 RAF，库每帧还重复绘制 | 通过“Enable 3D preview”启用；静止无循环，姿态、相机或尺寸变化才请求一帧；页面隐藏或模型离开视口停止绘制。轴模拟仅用户启动时运行。 |
| 3D 资源释放 | 只销毁 renderer 不会自动释放场景几何、材质、阴影资源及 controls 事件 | `osr-axis-preview.ts` 离开页面时取消 RAF、观察器和事件，释放 controls、geometry、material、shadow targets，再销毁 renderer/context。 |
| 脚本目录与搜索 | 递归列目录仍是 O(N)，但只在请求时触发，没有常驻扫描 | 本轮保留。大规模脚本库的分页、索引和搜索成本需用实际目录规模专项测量。另修复新增目录表单因空 `data-*` 属性未提交的问题。 |
| 配置与日志 | 日志队列和文件已有界限；配置关闭时可能重复访问已释放的锁 | `DeviceAxisRegistry.cs` 共用一次关闭任务。日志默认 500 条，最多 10,000 条；当前文件达到 4 MiB 后轮替并保留一份旧文件。 |
| Windows 播放线程 | 空闲时 `SleepPrecise(10)` 的尾段忙等仍消耗 CPU | `MultiFunPlayer/UI/Controls/ViewModels/ScriptViewModel.cs` 空闲使用可取消等待；播放或状态脏时保留原来的精准更新。 |
| Windows 连接与定时器 | 等待状态被取消时事件未退订；异步更新计时器取消后可能未释放 | `AbstractMediaSource.cs`、`AbstractOutputTarget.cs` 用 finally 退订、using 释放计时器，并更新 Period 而非重复创建计时器。 |

## 后端 CPU 和内存

环境：Linux/WSL、16 个逻辑核、.NET SDK 10.0.302、Release 构建。
基线来自同一提交的独立临时副本，使用相同配置和测试脚本；先测基线，再测优化版。
测试使用隔离配置目录和本地 File 输出，没有浏览器连接或真实设备。
每个场景先等待 2 秒，再测约 15 秒；区间内仅读取 `/proc`，不轮询服务 API。
CPU 的 100% 代表一个逻辑核，不是整台机器的总算力。

| 场景 | 优化前 CPU | 优化后 CPU | 前 RSS / MiB | 后 RSS / MiB | 采样区间快照增量：前 → 后 | 输出帧：前 → 后 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 启动后空闲 | 3.33% | 2.07% | 93.50 | 93.25 | 301 → 0 | 0 → 0 |
| 已加载脚本、已连接输出、未播放 | 5.93% | 1.00% | 104.75 | 104.12 | 301 → 0 | 301 → 0 |
| 20 Hz 播放 | 2.40% | 3.00% | 106.13 | 105.41 | 301 → 300 | 300 → 300 |
| 暂停且保持输出连接 | 2.47% | 0.13% | 107.50 | 105.66 | 300 → 0 | 300 → 0 |

RSS 为区间末值。四个场景顺序运行，因此 JIT、GC、运行时预热和宿主调度都会影响短时 CPU。
暂停场景 CPU 下降约 95%；空闲快照和重复输出归零是比单次 CPU 数字更稳定的行为证据。
活动播放 CPU 本次为 2.40% → 3.00%，没有改善，不能宣称所有场景都更快。
本轮降低了配置复制、输出数组和快照的分配频率，但没有采集托管堆分配跟踪，不能据 RSS 推算 GC 次数。

原始记录位于本地 `artifacts/performance/runtime-before.json` 和 `runtime-after.json`。
记录中的 repository revision 相同，二进制 SHA-256 不同，分别代表基线和未提交的优化工作树。

## 浏览器、3D 与内存

环境：Node 22.14.0、Playwright Chromium、1440 × 1000、headless SwiftShader 软件渲染。
浏览器 CPU 汇总多个进程，一个逻辑核为 100%；软件渲染会使用多个核心，因此可超过 100%。
这些 CPU 值不代表真实 GPU 的占用或帧率，不能与后端单进程数据混用。

性能脚本记录 RAF 回调、WebGL draw 调用、定时器、JS heap、DOM 节点和 CPU。
每段预热 1.5 秒后至少采样 5 秒，并使用实际采样时间计算 CPU，以免繁忙渲染推迟 CDP 响应导致比例失真。
隐藏场景通过注入 document visibility 状态及事件验证应用的主动停止逻辑，不依赖浏览器自动限频。

首次加载可能触发延迟布局重绘，脚本因此连续采集两个静止窗口，第二段记录稳定空闲。
以下为最终一次完整运行的数据，每段实际时长约 5.0–5.9 秒，精确时长见 JSON。

| 场景 | 前浏览器 CPU | 后浏览器 CPU | RAF 回调：前 → 后 | WebGL draw：前 → 后 |
| --- | ---: | ---: | ---: | ---: |
| Runtime Demo | 5.79% | 3.59% | 0 → 0 | 0 → 0 |
| 3D 稳定静止 | 1139.85% | 0.00% | 19 → 0 | 5776 → 0 |
| 用户启动轴运动 | 1026.54% | 1191.46% | 15 → 49 | 4512 → 7358 |
| 3D 页面隐藏 | 861.91% | 0.00% | 12 → 0 | 3600 → 0 |
| 离开 3D 页面 | 0.40% | 0.40% | 0 → 0 | 0 → 0 |

0.00% 表示该采样窗口的进程 CPU 计数没有可见增量，不表示浏览器永远不消耗 CPU。
用户启动运动时，优化版完成更多帧，软件渲染的总 CPU 反而上升；每次 RAF 对应的平均 draw
从约 301 次降到 150 次。此处验证了取消重复绘制，并没有证明活动动画的总 CPU 下降。
稳定静止场景的主线程忙碌比例从 99.99% 降到 0.014%。

已确认的行为：未启用 3D 时不请求模型模块；启用并完成布局后静止为零 RAF、零 WebGL draw；
改变姿态或用户启动模拟时绘制；隐藏和离开页面后恢复为零绘制。
浏览器回归还检查了离开场景后创建的 WebGL buffer 全部对应 deleteBuffer。

原始记录位于本地 `artifacts/performance/browser-before.json` 和 `browser-after.json`。
3D 库仍约 8 MB，改变的是加载时机，包体积尚未缩小。加载过的模块代码由浏览器缓存，
退出页面后 JS heap 不必回到首次打开前；这与 geometry/material/GPU 资源是否释放是不同指标。
退出场景并主动 GC 后，本次 JS heap 为前 6.14 MiB、后 6.18 MiB，未显示下降。
短时 heap 值也受 GC 时机影响，不将单次差值当作泄漏修复的定量证明；当前没有实机显存测量。

`osr-emu` 0.7.0 仅提供压缩分发文件，缺少关闭内部循环的公共接口。
`MultiFunPlayer.Web/scripts/osr-demand-plugin.ts` 在 Vite 构建和开发期间适配该固定版本，
增加按需渲染并移除重复绘制。适配有分发文件 SHA-256 和替换唯一性校验；升级依赖时必须重新检查，
源码不匹配会让构建失败，避免悄悄恢复持续渲染。

## 验证结果

- .NET：Core 8、Application 12、Infrastructure 46、Server 55，共 121 项测试通过。
- 浏览器：桌面和移动视口合计 18 项测试通过，包含空闲/隐藏渲染、WebSocket 暂停恢复、场景释放、目录提交和原有交互。
- 全方案 Release 构建成功，包含 Windows WPF 编译；现有 Whirligig `CA2024` 警告仍存在。
- Web typecheck、静态检查及生产构建通过；大型 3D chunk 的 Vite 提示仍存在。
- 约 60 秒持续播放通过：输出 1,212 帧，失败和合并帧均为 0；更新间隔均值 50.03 ms，P95 51.57 ms；服务优雅退出。

持续播放原始数据：`artifacts/performance/playback-soak.json`。该检查有 500 ms API 采样和状态订阅，
RSS 从 95.88 MiB 增至 112.67 MiB，峰值 113.63 MiB。它包含预热和诊断开销，不能与上面的无轮询 CPU 样本直接比较，
也不足以判定长期内存是否稳定。

## 尚需实机或更大负载验证

1. Windows 桌面实际 CPU、真实 GPU 的动画帧率/显存、NAS/ARM 的成本，以及串口和真实设备的时序、保活要求。
2. 8 小时持续播放和多次连接、切换场景后的堆/句柄趋势；当前只有短测和明确资源释放断言。
3. 大脚本与大量并行路由：当前最多 128 个源路由；128 项缓存上限不是字节预算，单脚本输入上限 8 MiB，解析后的对象可能更大。
   仍需根据实际脚本规模测量峰值内存，制定字节预算和缓存策略。
4. 3D 动画期间仍有较多绘制调用。真实低性能 GPU 若不达标，再根据画质验收降低阴影、模型复杂度或分辨率；当前未据软件渲染数字改变画质。
5. 日志在写入线程内同步落盘，错误洪峰仍可能产生 I/O 等待；条目数量/文件轮替并非每条日志内容的硬内存上限。

## 复现

命令从仓库根目录执行，先准备 .NET、Node、Python 和 Playwright Chromium 依赖。
若系统代理会拦截 localhost，应为该进程清除代理或正确配置 localhost 直连。

```sh
dotnet build Source/MultiFunPlayer.Server -c Release
npm --prefix Source/MultiFunPlayer.Web run typecheck
npm --prefix Source/MultiFunPlayer.Web run check:static
npm --prefix Source/MultiFunPlayer.Web run build
python3 tools/profile-runtime.py --dotnet "$DOTNET_ROOT/dotnet" --seconds 15 --output artifacts/performance/runtime-after.json
node tools/profile-browser.mjs artifacts/performance/browser-after.json
python3 tools/run-portable-soak.py --dotnet "$DOTNET_ROOT/dotnet" --duration-seconds 60 --poll-seconds 0.5 --max-revision-interval-ms 100 --max-coalesced-frames 30 --output artifacts/performance/playback-soak.json
```

基线后端可通过 `profile-runtime.py --server /path/to/baseline/MultiFunPlayer.Server.dll` 指定；
浏览器通过 `MFP_PROFILE_WEB=/path/to/baseline/Source/MultiFunPlayer.Web` 指向已构建的基线 Web。
两组测量应顺序执行，期间避免并行构建、浏览器回归或其他 CPU 密集任务。
`artifacts/` 是本地生成证据目录，不纳入 Git；本报告保留本轮关键数字和方法。
