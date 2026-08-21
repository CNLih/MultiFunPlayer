# fnOS package project

For the complete current source-run, test, Docker, fnOS, and acceptance workflow,
see [`Docs/docs/migration/current-usage-testing-deployment.md`](../../Docs/docs/migration/current-usage-testing-deployment.md).

`fnpack/` is an official-template-shaped Docker application project for the
MultiFunPlayer Web server. It contains the manifest, Docker Compose project,
Web entry, icons, lifecycle scripts, resource/privilege declarations, and a
short install guide for the managed and fnOS-authorized Scripts folders.

The older JSON `manifest` beside this file remains a repository-owned contract
for Docker/fnOS static checks. It is not an FPK manifest and is intentionally
kept separate from `fnpack/manifest`.

Install the official [`fnpack`](https://developer.fnnas.com/docs/cli/fnpack/)
binary from the fnOS developer portal, then build an architecture-specific
package:

```sh
FNPACK=/path/to/fnpack ./build.sh package fnos x86
FNPACK=/path/to/fnpack ./build.sh package fnos arm
# Or build and validate both architectures:
FNPACK=/path/to/fnpack ./build.sh package fnos all
```

The wrapper maps fnOS `x86`/`arm` package labels to the Docker image's
`linux/amd64`/`linux/arm64` builds. It refuses to overwrite an existing FPK
unless `--force` is supplied. The Compose project uses fnOS's
`TRIM_SERVICE_PORT` and `TRIM_PKGVAR` variables and preserves separate
`config`, `keys`, `scripts`, and `logs` shares across upgrade and uninstall.

The image in the Compose project defaults to `multifunplayer:local` so local
development is deterministic. A release must set `MFP_IMAGE` to the published
multi-architecture image before installing the FPK. USB serial access remains
an explicit device permission and is not enabled by default.

Run the repository-owned static checks before using `fnpack`:

```sh
./build.sh package validate
```

This check confirms both the portable container contract and the canonical
fnpack project structure. Running `fnpack build` validates the package with the
installed official tool, but does not replace installation, upgrade, rollback,
uninstall, serial, or ARM tests on a real fnOS device.

`build.sh` invokes `deploy/validate-fpk.sh` automatically after each successful
FPK build. Verify the generated checksum again before transferring it to fnOS:

```sh
(cd artifacts/fnos/x86 && sha256sum --check multifunplayer.fpk.sha256)
(cd artifacts/fnos/arm && sha256sum --check multifunplayer.fpk.sha256)
```

`build-fpk.sh` writes the matching `multifunplayer.fpk.sha256` sidecar next to
each package so the archive can be verified after transfer.

After installing the FPK and opening its Web entry, run the black-box Web/API
acceptance harness from a machine that can reach the NAS:

```sh
export MFP_ACCEPTANCE_TOKEN='the-target-bearer-token'
python3 tools/run-target-acceptance.py \
  --base-url http://fnos-host:8080 \
  --output artifacts/target/fnos-acceptance.json
```

For a new installation using one-time pairing, also export
`MFP_PAIRING_TOKEN`. The report covers authentication, health, SPA fallback,
capabilities, script playback, and log redaction. It is not evidence of fnOS
firmware schema acceptance, USB permission, ARM execution, or physical output;
attach those target-device results separately to the release evidence manifest.

## 多脚本目录与 NAS 文件夹

后端脚本库支持**多个根目录**：`MFP_SCRIPTS_PATHS`（分号分隔的容器内路径列表），
默认值为 `/scripts`（即 fnOS 分配的 scripts 数据卷）。每个根目录及其子文件夹都会被
递归索引并参与智能匹配。Web 控制台的「Scripts」栏目以目录树和只读文件预览展示内容，
支持按文件后缀筛选和分页。

fnOS `1.2.0401` / App `1.34.0` 及以上可直接在 MultiFunPlayer 的 Scripts 页面点击
`Add mapping`。FPK 声明最小的 `trim.file.sharedAccess` scope，官方 Web SDK 打开系统目录
选择器，管理员确认后 fnOS 为应用用户授予 ACL。授权变化会触发生命周期配置回调，回调把
第一个授权目录连接到稳定的应用数据入口；重启应用后 Compose 将其只读挂载到
`/scripts/external`。

从 fnOS 应用入口打开时，目录选择器显示在系统宿主中；直接访问服务端口时，授权页在独立
标签打开，原控制台会保留并在授权完成后刷新状态。授权目录中的层级和脚本显示在 Scripts
页面的可展开目录树和文件夹预览中，点击预览里的文件即可加载。

默认 `/scripts` 始终保留为可写的托管脚本库，因此不授权外部目录也可以上传和播放脚本。
当前 FPK 可记录多个授权目录，但只挂载第一个；需要更换时，点击 `Remove mapping` 进入 fnOS
应用设置撤销旧目录授权，再从 Scripts 页面添加新映射。移除映射只撤销应用访问关系，不删除、
移动或修改 NAS 上的真实文件和文件夹。授权目录暂时离线时，容器使用空目录占位，不会扩大到
其他 NAS 路径。

旧版 fnOS 不提供应用内授权 API 时，进入 fnOS 应用设置手动授权；若目标固件
不能把授权变化传给 Docker 项目，则继续使用托管 `/scripts`，或作为实机兼容措施手动添加
只读 bind mount。官方接口说明：
[应用共享授权路径](https://developer.fnnas.com/api/authorization/shared-access/)。
