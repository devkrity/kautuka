# Build context: src/. Supply the web-assets image from the same release.
# Release builds pin WEB_ASSETS_IMAGE by digest; no frontend toolchain is shipped.
ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.403-azurelinux3.0-aot@sha256:be109b4b7a2fa42149ee4be17d1807176b34471530b2339a5ca6331dc640b52a
ARG AZURELINUX_DISTROLESS_IMAGE=mcr.microsoft.com/dotnet/runtime-deps:10.0-azurelinux3.0-distroless-extra@sha256:6601c6381eac77fbcb2a35e24949b48f62cb390f0bc7847c3038fe496f846595
ARG WEB_ASSETS_IMAGE

FROM ${WEB_ASSETS_IMAGE} AS assets

FROM ${DOTNET_SDK_IMAGE} AS build
WORKDIR /src
COPY . .
RUN dotnet publish backend/Kautuka.WebHost/Kautuka.WebHost.csproj \
    -c Release --self-contained true -r linux-x64 -p:PublishAot=true \
    -p:InvariantGlobalization=false -p:KautukaReleaseArtifactBuild=true -p:PublishSingleFile=true -o /app

FROM ${AZURELINUX_DISTROLESS_IMAGE}
COPY --from=build /app /app
COPY --from=assets /out/dist /app/wwwroot
WORKDIR /app
ENTRYPOINT ["/app/Kautuka.WebHost"]
