FROM quay.io/fedora/fedora-minimal:44

ARG DOTNET_SDK_VERSION=10.0.300

ENV PATH=/opt/dotnet:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
ENV CC=clang
ENV CXX=clang++

RUN microdnf install -y \
    bash \
    binutils \
    ca-certificates \
    clang \
    curl \
    findutils \
    gcc \
    gcc-c++ \
    glibc-devel \
    libicu \
    libicu-devel \
    lld \
    make \
    tar \
    zlib-devel \
    && microdnf clean all \
    && curl --fail --show-error --location --retry 5 --retry-all-errors --retry-delay 2 --silent https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
    && chmod +x /tmp/dotnet-install.sh \
    && /tmp/dotnet-install.sh --version "$DOTNET_SDK_VERSION" --install-dir /opt/dotnet --no-path \
    && ln -sf /opt/dotnet/dotnet /usr/local/bin/dotnet \
    && rm -f /tmp/dotnet-install.sh