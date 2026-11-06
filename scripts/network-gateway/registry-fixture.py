#!/usr/bin/env python3
"""Tiny offline TLS OCI registry fixture, never an appliance registry service."""

import argparse
import hashlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
from pathlib import Path
import sqlite3
import ssl
import threading
import time


def digest(content):
    return "sha256:" + hashlib.sha256(content).hexdigest()


def encode(value):
    return json.dumps(value, separators=(",", ":")).encode()


def build_image(directory, layer):
    directory.mkdir(mode=0o700)
    layer_bytes = layer.read_bytes()
    layer_digest = digest(layer_bytes)
    configuration = encode({"architecture": "amd64", "os": "linux",
                            "config": {"User": "65532:65532", "Cmd": ["/probe", "hold"]},
                            "rootfs": {"type": "layers", "diff_ids": [layer_digest]}})
    configuration_digest = digest(configuration)
    manifest = encode({"schemaVersion": 2, "mediaType": "application/vnd.oci.image.manifest.v1+json",
                       "config": {"mediaType": "application/vnd.oci.image.config.v1+json",
                                  "digest": configuration_digest, "size": len(configuration)},
                       "layers": [{"mediaType": "application/vnd.oci.image.layer.v1.tar",
                                   "digest": layer_digest, "size": len(layer_bytes)}]})
    for identifier, content in ((layer_digest, layer_bytes), (configuration_digest, configuration)):
        (directory / identifier.removeprefix("sha256:")).write_bytes(content)
    (directory / "manifest.json").write_bytes(manifest)
    metadata = {"manifestDigest": digest(manifest), "configurationDigest": configuration_digest,
                "layerDigest": layer_digest}
    (directory / "metadata.json").write_bytes(encode(metadata))
    return metadata


def serve(directory, ledger, observe_home=None):
    events_path = directory / "events.jsonl"
    events_path.write_bytes(b"")
    log_lock = threading.Lock()

    def record(event):
        with log_lock:
            with events_path.open("ab") as stream:
                stream.write(encode(event) + b"\n")
            print(json.dumps(event), flush=True)

    metadata = json.loads((directory / "metadata.json").read_bytes())
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.minimum_version = ssl.TLSVersion.TLSv1_2
    context.load_cert_chain(directory / "server.crt", directory / "server.key")

    class Handler(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        def log_message(self, *_):
            pass

        def respond(self, head):
            record({"event": "request", "method": self.command, "path": self.path, "peer": list(self.client_address)})
            kind = "application/json"
            identifier = None
            if self.path == "/v2/":
                content, status = b"{}", 200
            elif self.path == "/v2/ktng/probe/manifests/" + metadata["manifestDigest"]:
                content, status = (directory / "manifest.json").read_bytes(), 200
                kind = "application/vnd.oci.image.manifest.v1+json"
                identifier = metadata["manifestDigest"]
            elif self.path in ["/v2/ktng/probe/blobs/" + metadata[field] for field in ("configurationDigest", "layerDigest")]:
                identifier = self.path.rsplit("/", 1)[-1]
                content, status = (directory / identifier.removeprefix("sha256:")).read_bytes(), 200
                kind = "application/octet-stream"
                if identifier == metadata["configurationDigest"] and (directory / "corrupt").exists():
                    content = content.replace(b'"amd64"', b'"amd65"', 1)
                    if digest(content) == identifier:
                        raise RuntimeError("Corrupt fixture did not change bytes")
                    record({"event": "corrupt_blob", "expected": identifier, "served": digest(content)})
            else:
                content, status = b'{"errors":[{"code":"NAME_UNKNOWN"}]}', 404
            self.send_response(status)
            self.send_header("Content-Type", kind)
            self.send_header("Content-Length", str(len(content)))
            self.send_header("Docker-Distribution-API-Version", "registry/2.0")
            if identifier:
                self.send_header("Docker-Content-Digest", identifier)
            self.end_headers()
            if not head:
                self.wfile.write(content)

        def do_GET(self):
            self.respond(False)

        def do_HEAD(self):
            self.respond(True)

    class Registry(ThreadingHTTPServer):
        daemon_threads = True
        observed = False

        def get_request(self):
            connection, peer = self.socket.accept()
            connection.settimeout(5)
            try:
                # Observe the translated socket before serving even the TLS handshake.
                with sqlite3.connect("file:" + str(ledger) + "?mode=ro", uri=True) as database:
                    row = database.execute("SELECT id FROM gateway_admissions WHERE source_address=? AND source_port=? AND destination_address='10.231.252.2' AND destination_port=20443 AND protocol=6 AND subject='probe-artifacts'", peer).fetchone()
                if row is None:
                    record({"event": "AUDIT_MISSING", "peer": list(peer)})
                    raise OSError("No durable admission before registry handshake")
                record({"event": "admitted_connection", "peer": list(peer), "admission": row[0]})
                if observe_home is not None and not self.observed:
                    self.observed = True
                    control = observe_home / "control.json"
                    ack = observe_home / "control-ack"
                    control.write_bytes(encode({"phase": "observe-pull", "peer": list(peer)}))
                    deadline = time.monotonic() + 5
                    while not ack.exists():
                        if time.monotonic() > deadline:
                            raise OSError("Root pull observer did not acknowledge")
                        time.sleep(0.02)
                    if ack.read_text() != "observe-pull":
                        raise OSError("Unexpected root pull observation acknowledgement")
                    control.unlink()
                    ack.unlink()
                return context.wrap_socket(connection, server_side=True), peer
            except (OSError, sqlite3.Error):
                connection.close()
                raise

    with Registry(("10.231.252.2", 20443), Handler) as server:
        print("KTNG_REGISTRY_READY", flush=True)
        server.serve_forever()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--directory", type=Path, required=True)
    parser.add_argument("--ledger", type=Path, required=True)
    parser.add_argument("--observe-home", type=Path, help="Trusted fixture observer handshake directory")
    args = parser.parse_args()
    serve(args.directory, args.ledger, args.observe_home)
