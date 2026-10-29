---
sidebar_position: 1
---

# MultiFunPlayer 文档

当前仓库同时包含保留的 Windows/WPF 程序，以及正在验收的
ASP.NET Core + Web SPA + Linux/Docker/fnOS 便携路径。

从以下文档开始：

- [当前整体框架与代码导读](./migration/architecture-walkthrough.md)：运行边界图、两条播放调用链、Web/3D、NAS 路径和代码阅读顺序。
- [当前使用、测试、运行与部署](./migration/current-usage-testing-deployment.md)：从源码运行、日常使用、测试和目标部署的统一入口。
- [Web/Linux 功能矩阵](./migration/feature-matrix.md)：哪些能力已直接实现、需要外部代理或仍被阻塞。
- [Headless Web Operations](./migration/web-operations.md)：认证、备份、升级和故障排查。
- [Headless Web Architecture](./migration/web-architecture.md)：Core、Application、Infrastructure、Server 和 Web 的职责边界。
- [贡献指南](./migration/contributing-headless.md)：代码修改和验证要求。
- [按功能性能复查与实测](./migration/performance-review.md)：CPU、内存、空闲调度和按需 3D 绘制的当前结果。

便携路径仍处于 Beta/发布验收阶段。Docker、fnOS、ARM、WindowsDesktop
和物理设备的支持声明必须以对应目标环境的测试证据为准。
