ARG FEDORA_IMAGE=quay.io/fedora/fedora-minimal@sha256:f71389f4775ee6c0b26d917f4487851857f1096f20b5e6144ee03c223e31748a
FROM ${FEDORA_IMAGE}
LABEL org.opencontainers.image.source="https://github.com/devkrity/kautuka"
RUN microdnf install -y nftables iproute \
    && microdnf clean all \
    && mkdir -p /usr/share/doc/kautuka \
    && rpm -qa --qf '%{SOURCERPM}\n' | sort -u | grep -v '^[(]none[)]$' > /usr/share/doc/kautuka/FEDORA_SOURCE_RPMS.txt
