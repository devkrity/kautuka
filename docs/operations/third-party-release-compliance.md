# Third-party release compliance

## Inventory and change review

`compliance/third-party-components.json` records externally sourced software bundled in Kautuka release payloads that may not be discoverable as ordinary application packages. It covers sqlite-vec, pinned runtime images, bundled web fonts, Photino.Native with its embedded WebView2 loader, and the reviewed elkjs source/license record. For each component, retain the exact upstream version, immutable source commit where available, license expression, source URL, and any relevant binary hash, npm integrity, or image digest.

The release generator is `scripts/generate-third-party-compliance.mjs`. It verifies tracked sqlite-vec and elkjs license hashes, confirms the elkjs version/integrity against `src/frontend/pnpm-lock.yaml`, and checks that runtime image digests still match the reviewed catalog. It also captures the installed frontend production dependency graph from the frozen pnpm install into `FRONTEND_DEPENDENCIES.json`, recording every package version, declared license expression, source locator, and hash of package-specific license/notice files present in the package. If metadata or a version mismatches, update the reviewed evidence before release.

## What each release publishes

The Linux platform archive and WSL appliance carry notices, the component inventory, and frontend package inventory under `/usr/share/doc/kautuka/`. The release directory publishes these beside the downloads, with full license materials in `kautuka-licenses.zip`. Generated `CREDITS.md` combines release SPDX data with the frontend production graph, which is a conservative superset and can include packages used only during frontend build. Standard license texts come from a pinned SPDX License List revision; package-specific copyright, license, and notice text files are separately staged and hash-checked.

The publish workflow scans the built Linux and WSL payloads, the bundled sidecar image archives, and the exact `kautuka-base` digest with the pinned Syft image. It publishes separate SPDX documents for those targets and refreshes the release asset manifest and SHA-256 checksums after SBOM generation.

## Source availability

Each release publishes actual source archives beside its binaries under `sources/`: complete repository snapshots for OpenObserve, git-pkgs/proxy, elkjs 0.12.0, and elkjs 0.9.3 at the exact commits recorded in `THIRD_PARTY_COMPONENTS.json`, plus a compressed set of source RPMs for every Fedora package installed in the WSL rootfs and the exact `kautuka-base` image. For the two GHCR mirrors, the component inventory records both the Devkrity image digest and upstream source, commit, and license. `kautuka-licenses.zip` contains `LICENSE-openobserve.txt` and `LICENSE-proxy.txt`; `kautuka-third-party-sources.zip` contains the corresponding full source snapshots. Its `SOURCE_ARCHIVES.md` maps image IDs to source archives and license texts. Separate versioned elkjs snapshots accompany the EPL-2.0 license records for the direct frontend and transitive Mermaid package. The per-release asset manifest records each source file's size and SHA-256. Keep these archives available for at least as long as the corresponding release binaries and images remain available.

The WebView2 Runtime is a Windows prerequisite and is not bundled by Kautuka; the installer embeds `WebView2Loader.dll` through Photino.Native. Review every `NOT DETECTED` license in `CREDITS.md` and the source archives for the exact release before publication. The automation provides traceable inventory and distribution materials; it does not make a legal determination.
