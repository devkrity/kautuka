# Runtime image mirrors

Kautuka publishes the selected OpenObserve and git-pkgs/proxy images in Devkrity's public GHCR namespace. Each image is copied as a complete multi-platform OCI image from a pinned upstream digest; Kautuka does not rebuild or alter the mirrored image. Runtime configuration uses the reviewed Devkrity digest, not a floating tag.

| GHCR package | Upstream project | License | Source evidence |
|---|---|---|---|
| [`kautuka-observability`](https://github.com/orgs/devkrity/packages/container/kautuka-observability) | [OpenObserve](https://github.com/openobserve/openobserve), version 0.92.2 | AGPL-3.0 | Exact source commit and mirrored image digest are in [`third-party-components.json`](../../compliance/third-party-components.json); source snapshot is published with Kautuka releases. |
| [`kautuka-package-proxy`](https://github.com/orgs/devkrity/packages/container/kautuka-package-proxy) | [git-pkgs/proxy](https://github.com/git-pkgs/proxy), version 0.8.1 | GPL-3.0-or-later | Exact source commit and mirrored image digest are in [`third-party-components.json`](../../compliance/third-party-components.json); source snapshot is published with Kautuka releases. |

## License and source files in Kautuka releases

Every Kautuka application release includes:

- `THIRD_PARTY_NOTICES.txt` and `THIRD_PARTY_COMPONENTS.json`, identifying each distributed image, license, upstream source URL, source commit, and exact image digest.
- `kautuka-licenses.zip`, including `LICENSE-openobserve.txt` and `LICENSE-proxy.txt`.
- `kautuka-third-party-sources.zip`, including complete source repository snapshots named with the component ID and exact source commit. `sources/SOURCE_ARCHIVES.md` maps the image IDs to those archives and their companion license files; `sources/SHA256SUMS` verifies the source archive files.

The source snapshots are pinned to the commits in the component inventory, not fetched from a mutable branch when a Kautuka release is built. Keep the notices, license bundle, and source bundle available for as long as the corresponding Kautuka release is distributed. These materials document the software and its license; they do not relicense either upstream project or make a legal determination.

## Updating a mirror

Use the **Mirror Runtime Image** workflow to copy a new, reviewed upstream digest under a new version tag. Update the checked-in source and release digest records and the component inventory together in a reviewed change before a Kautuka application release uses the new image. Do not overwrite an existing version or configure consumers with a floating tag. The published Kautuka release then contains the updated source and license map.
