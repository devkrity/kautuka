ARG FEDORA_IMAGE=quay.io/fedora/fedora-minimal:44
FROM ${FEDORA_IMAGE}
RUN microdnf install -y nftables iproute && microdnf clean all
