# Stage 1 — Native AOT compile using the .NET SDK.
# Build context: src/ (includes backend/ and shared/ project references).
ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.403-azurelinux3.0-aot@sha256:be109b4b7a2fa42149ee4be17d1807176b34471530b2339a5ca6331dc640b52a
ARG AZURELINUX_DISTROLESS_IMAGE=mcr.microsoft.com/azurelinux/distroless/base:3.0@sha256:370fb2c55e7f0474fed019623cce6a8db65a4d70dedfca9219fc69d6c7627f89

FROM ${DOTNET_SDK_IMAGE} AS build
WORKDIR /src
COPY . .
RUN dotnet publish backend/Kautuka.Gateway/Kautuka.Gateway.csproj \
    -c Release --self-contained true -r linux-x64 -p:PublishAot=true \
    -p:InvariantGlobalization=true -p:KautukaReleaseArtifactBuild=true -p:PublishSingleFile=true -o /app

# Stage 2 — Azure Linux distroless base runtime for the self-contained AOT binary.
FROM ${AZURELINUX_DISTROLESS_IMAGE}
COPY --from=build /app /app
WORKDIR /app
ENTRYPOINT ["/app/Kautuka.Gateway"]
