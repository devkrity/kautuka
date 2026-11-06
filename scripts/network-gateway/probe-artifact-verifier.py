#!/usr/bin/env python3
"""Offline executable-level signature rejection checks; no accounts or networking."""

import argparse
import base64
import json
from pathlib import Path
import resource
import shutil
import subprocess
import sys
import tempfile
import uuid


def execute(binary):
    checks = []
    # An unexpected native abort must not leave a core containing fixture material.
    resource.setrlimit(resource.RLIMIT_CORE, (0, 0))
    with tempfile.TemporaryDirectory(prefix="ktng-controlled-rejection-", dir="/var/tmp") as directory:
        root = Path(directory)

        def command(arguments):
            return subprocess.run(arguments, capture_output=True, check=True, timeout=10)

        for name in ("trusted", "attacker"):
            command(["openssl", "ecparam", "-name", "prime256v1", "-genkey", "-noout", "-out", str(root / (name + ".pem"))])
        command(["openssl", "pkey", "-in", str(root / "trusted.pem"), "-pubout", "-out", str(root / "public.pem")])
        public = (root / "public.pem").read_bytes()
        manifest = {"schemaVersion": 1, "releaseId": "ktng-" + uuid.uuid4().hex,
                    "registry": "10.231.252.2:20443", "repository": "ktng/probe", "platform": "linux/amd64",
                    "manifestDigest": "sha256:" + "a" * 64, "configurationDigest": "sha256:" + "b" * 64,
                    "compatibility": "ktng-probe-v1"}
        raw = json.dumps(manifest, separators=(",", ":")).encode()
        path = root / "manifest.json"
        path.write_bytes(raw)

        def sign(name="trusted"):
            signature = command(["openssl", "dgst", "-sha256", "-sign", str(root / (name + ".pem")), str(path)]).stdout
            (root / "signature.txt").write_bytes(base64.b64encode(signature))

        def verify():
            return subprocess.run([str(binary), "probe-verify-artifact", str(path), str(root / "signature.txt"),
                                   str(root / "public.pem")], capture_output=True, timeout=10)

        def reject(name):
            observed = verify()
            if observed.returncode != 1 or observed.stdout or not observed.stderr.startswith(b"Artifact fixture rejected:"):
                raise RuntimeError(f"{name}: expected controlled exit 1 without a plan, observed {observed.returncode}")
            checks.append({"case": name, "exitCode": 1, "noPlan": True})

        sign()
        valid = verify()
        if valid.returncode != 0 or json.loads(valid.stdout)["ManifestDigest"] != manifest["manifestDigest"]:
            raise RuntimeError("Valid signature did not produce the expected verified plan")
        checks.append({"case": "valid", "exitCode": 0})
        path.write_bytes(raw + b" ")
        reject("modified_bytes")
        path.write_bytes(raw)
        sign("attacker")
        reject("untrusted_signer")
        sign()
        (root / "public.pem").write_bytes(b"malformed")
        reject("malformed_key")
        (root / "public.pem").write_bytes(public)
        path.write_bytes(b"{")
        sign()
        reject("signed_malformed_json")
        path.write_bytes(b"x" * 16385)
        reject("oversized_input")
    return checks


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    options = parser.parse_args()
    if sys.platform != "linux" or not options.binary.is_file() or shutil.which("openssl") is None:
        parser.error("requires Linux, the published fixture executable and openssl; no resources created")
    with options.report.open("x", encoding="utf-8") as report:
        result = {"schemaVersion": 1, "scope": "published-aot-controlled-artifact-rejection", "gate1Passed": False,
                  "verificationPassed": False, "limitations": ["No network transport, production trust-store or updater proof."]}
        try:
            result["checks"] = execute(options.binary.resolve())
            result["verificationPassed"] = True
        except (OSError, RuntimeError, subprocess.SubprocessError, ValueError, KeyError) as error:
            result["error"] = str(error)
        json.dump(result, report, indent=2)
        report.write("\n")
        print(json.dumps(result, indent=2))
        return 0 if result["verificationPassed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
