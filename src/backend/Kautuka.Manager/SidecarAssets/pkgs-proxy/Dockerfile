# Local wrapper over git-pkgs/proxy so Podman can run an in-container HEALTHCHECK.
# Manager cannot HTTP-probe unpublished sandbox aliases from the host netns (rootless).
# HEALTHCHECK uses wget already in this Alpine image; do not add a second probe container.
FROM ghcr.io/git-pkgs/proxy@sha256:3165aeeb7fbdd2160b4caf6f7022b1498ed564deb95b21bbd19ecc7c6e6f89a5

ENV PROXY_LISTEN=:20101

HEALTHCHECK --interval=5s --timeout=3s --start-period=15s --retries=6 \
  CMD wget -q -O /dev/null http://127.0.0.1:20101/health || exit 1
