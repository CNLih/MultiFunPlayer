# Local image-size fork

This package is vendored from the npm `image-size` 2.0.2 distribution and is
published locally as `2.0.3-mfp.1` because the upstream project is archived and
has no patched release for CVE-2025-71329 (GHSA-5p2g-fcmc-qvqq) or
CVE-2025-71330 (GHSA-w3rx-r6r6-pgpr).

The generated CommonJS/ESM bundles reject zero or negative HEIF/JXL box sizes
and ICNS entry lengths before advancing parser offsets. This prevents the
infinite-loop denial-of-service inputs described by those advisories. The
package is used only by the Docusaurus build to read local documentation image
dimensions; no remote image input is accepted by the application.

The original MIT license is retained in `LICENSE`. Recheck this fork when an
upstream release with equivalent fixes becomes available.
