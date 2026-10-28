# Fedora 44 is a build input on the release runner only. The appliance runs the
# finished ghcr.io image recorded for that platform release, not this tag.
ARG FEDORA_IMAGE=quay.io/fedora/fedora-minimal:44
FROM ${FEDORA_IMAGE}

RUN microdnf install -y \
    bash \
    ca-certificates \
    curl \
    git \
    gzip \
    procps-ng \
    python3 \
    python3-pip \
    shadow-utils \
    tar \
    xz \
    libatomic \
    && microdnf clean all \
    && curl -fSL "https://nodejs.org/dist/v26.3.0/node-v26.3.0-linux-x64.tar.xz" -o /tmp/node.tar.xz \
    && echo "0ce210831380ab75a5fec0d3fb6b5af0958898016b2cae51cc2fa11057d2f77e  /tmp/node.tar.xz" | sha256sum -c - \
    && tar -C /usr/local --strip-components=1 -xJf /tmp/node.tar.xz \
    && rm -f /tmp/node.tar.xz \
    && curl -fSL "https://github.com/astral-sh/uv/releases/download/0.11.19/uv-x86_64-unknown-linux-gnu.tar.gz" -o /tmp/uv.tar.gz \
    && echo "7035608168e106375b36d0c818d537a889c51a8625fe7f8f7cad5e62b947c368  /tmp/uv.tar.gz" | sha256sum -c - \
    && tar -C /usr/local/bin --strip-components=1 -xzf /tmp/uv.tar.gz \
    && rm -f /tmp/uv.tar.gz

ENV PATH=/usr/local/bin:$PATH \
    UV_SYSTEM_PYTHON=1 \
    UV_CACHE_DIR=/opt/uv-cache

RUN mkdir -p /usr/share/doc/kautuka \
    && rpm -qa --qf '%{SOURCERPM}\n' | sort -u | grep -v '^[(]none[)]$' > /usr/share/doc/kautuka/FEDORA_SOURCE_RPMS.txt

RUN ln -sf /usr/bin/python3 /usr/local/bin/python \
    && groupadd --gid 1000 node \
    && useradd --uid 1000 --gid 1000 --create-home --shell /bin/sh node \
    && mkdir -p /workspace /opt/uv-cache /home/node/.npm \
    && chown -R node:node /workspace /opt/uv-cache /home/node/.npm

WORKDIR /workspace

USER node

CMD ["tail", "-f", "/dev/null"]
