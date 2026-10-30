# Kautuka Web assets build stage for Kautuka.WebHost.
# Build with src/frontend/web as context when you need a standalone dist bundle:
#   podman build -f src/images/kautuka-web-assets/Dockerfile --target export -t localhost/kautuka-web-assets:latest src/frontend/web
FROM quay.io/fedora/fedora:44 AS builder
WORKDIR /app
COPY package.json pnpm-lock.yaml ./
RUN dnf install -y curl tar xz libatomic \
	&& curl -fSL "https://nodejs.org/dist/v26.3.0/node-v26.3.0-linux-x64.tar.xz" -o /tmp/node.tar.xz \
	&& tar -C /usr/local --strip-components=1 -xJf /tmp/node.tar.xz \
	&& rm -f /tmp/node.tar.xz \
	&& npm install -g pnpm@11.5.2 --no-fund --quiet \
	&& pnpm install --frozen-lockfile \
	&& dnf clean all
COPY . .
RUN pnpm build

FROM scratch AS export
WORKDIR /out
COPY --from=builder /app/dist ./dist
