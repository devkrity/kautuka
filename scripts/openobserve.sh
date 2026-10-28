# Start OpenObserve (ephemeral local stack)
# UI and OTLP HTTP endpoint: http://127.0.0.1:20215
# Login: kautuka-telemetry@kautuka.local / (see KautukaTelemetryDefaults.DefaultOpenObserveRootUserPassword)

mkdir -p "$PWD/artifacts/openobserve-data"

podman rm -f kautuka-openobserve 2>/dev/null || true
podman run -d \
	--name kautuka-openobserve \
	-v "$PWD/artifacts/openobserve-data:/data" \
	-p 127.0.0.1:20215:5080 \
	-e ZO_ROOT_USER_EMAIL="kautuka-telemetry@kautuka.local" \
	-e ZO_ROOT_USER_PASSWORD="KautukaOo!telemetry-7f3a" \
	-e ZO_DATA_DIR="/data" \
	public.ecr.aws/zinclabs/openobserve:latest
