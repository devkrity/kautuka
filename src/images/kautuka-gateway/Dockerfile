# Stage 1 — Native AOT compile using the .NET SDK.
# Build context: src/ (includes backend/ and shared/ project references).
ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.300
ARG AZURELINUX_DISTROLESS_IMAGE=mcr.microsoft.com/azurelinux/distroless/base:3.0

FROM ${DOTNET_SDK_IMAGE} AS build
RUN apt-get update && apt-get install -y --no-install-recommends clang binutils zlib1g-dev && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY . .
RUN dotnet publish backend/Kautuka.Gateway/Kautuka.Gateway.csproj \
    -c Release --self-contained true -r linux-x64 -p:PublishAot=true \
    -p:InvariantGlobalization=true -o /app

# Stage 2 — Azure Linux distroless base runtime for the self-contained AOT binary.
FROM ${AZURELINUX_DISTROLESS_IMAGE}
COPY --from=build /app /app
WORKDIR /app
ENTRYPOINT ["/app/Kautuka.Gateway"]
