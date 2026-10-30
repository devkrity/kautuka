Kautuka mirrors the selected upstream OpenObserve image to GHCR as
ghcr.io/devkrity/kautuka-observability. The source digest is recorded in
upstream.json; the promoted Devkrity digest is recorded in release.json.

Mirror a new upstream image only through the "Mirror Runtime Image" workflow,
then review and commit the new source and runtime digests before app releases
use it. Do not use floating tags in Kautuka runtime configuration.

OpenObserve is distributed under AGPL-3.0. Keep the corresponding source
snapshot and license with each Kautuka application release.
