# fnOS package project

For the complete current source-run, test, Docker, fnOS, and acceptance workflow,
see [`Docs/docs/migration/current-usage-testing-deployment.md`](../../Docs/docs/migration/current-usage-testing-deployment.md).

`fnpack/` is an official-template-shaped Docker application project for the
MultiFunPlayer Web server. It contains the manifest, Docker Compose project,
Web entry, icons, lifecycle scripts, resource/privilege declarations, and a
non-interactive wizard placeholder required by current `fnpack` validation.

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
递归索引、搜索并参与智能匹配。Web 控制台的「Scripts」栏目可增删根目录下的子文件夹。

要让容器访问 **NAS 上其他共享文件夹**作为脚本目录，需要把该文件夹挂载进容器。
受 fnpack 1.2.3 资源 schema 限制，FPK 只能声明 `data-share`（应用专用目录），
**不能通过标准字段声明“任意共享文件夹授权”**。可用的做法：

1. 在 fnOS 的 Docker/容器管理中，为 `multifunplayer` 容器额外添加一个
   `主机共享文件夹 -> 容器路径` 的挂载（例如把 NAS 的 `/vol1/1000/scripts` 挂到容器
   `/scripts/extra`）。
2. 在 `app/docker/docker-compose.yaml`（或已安装的 compose）里给
   `MFP_SCRIPTS_PATHS` 加上该容器路径，例如：
   ```yaml
   MFP_SCRIPTS_PATHS: /scripts;/scripts/extra
   ```
3. 重建容器后，该文件夹及其子目录即出现在脚本库中。

不同 fnOS 版本的共享文件夹授权字段可能不同，若你的 fnOS 支持共享文件夹授权，
可参照对应版本文档在 `config/resource` 中补充声明后重新打包验证。
