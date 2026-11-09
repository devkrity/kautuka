#!/usr/bin/env python3
"""Build the appliance's local image archives before signing either platform.

Core repository-style references identify the packaged OCI manifest. They do
not claim a registry publication: bootstrap uses the signed archive/config map.
No image push or application runtime is started by this producer.
"""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tarfile
import tempfile

SPEC = importlib.util.spec_from_file_location("archive_binding", Path(__file__).with_name("bind-runtime-image-archives.py"))
binding = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(binding)
PLATFORM = "linux/amd64"
CORES = {"credentialshost": "CredentialsHost", "channelhost": "ChannelHost", "gateway": "Gateway", "webhost": "WebHost"}
PIN = re.compile(r"[^\s@]+@sha256:[a-f0-9]{64}\Z")
CONFIG = re.compile(r"(?:sha256:)?[a-f0-9]{64}\Z")


def run(engine, *arguments):
    try:
        result = subprocess.run([engine, *map(str, arguments)], check=True, stdout=subprocess.PIPE, text=True)
    except subprocess.CalledProcessError as error:
        if error.stdout: print(error.stdout[-8000:], flush=True)
        raise
    return result.stdout


def user_arguments(engine):
    uid, gid = os.getuid(), os.getgid()
    result = ["--user", str(uid) + ":" + str(gid), "--env", "HOME=/tmp", "--env", "DOTNET_CLI_HOME=/tmp"]
    if Path(engine).name == "podman" and uid != 0: result += ["--userns=keep-id"]
    return result


def inspect(engine, image):
    document = json.loads(run(engine, "image", "inspect", image))
    if not isinstance(document, list) or len(document) != 1:
        raise ValueError("Expected one inspected build image")
    value = document[0]
    identity = value.get("Id", "")
    if not CONFIG.fullmatch(identity) or value.get("Os") != "linux" or value.get("Architecture") != "amd64":
        raise ValueError("Build image has an invalid identity/platform")
    return identity if identity.startswith("sha256:") else "sha256:" + identity


def pull(engine, source):
    if not isinstance(source, str) or not PIN.fullmatch(source):
        raise ValueError("Every external build/runtime image must be digest pinned")
    run(engine, "pull", "--platform", PLATFORM, source)
    return inspect(engine, source)


def build(engine, context, dockerfile, *arguments):
    iid = context / "image-id.txt"
    iid.unlink(missing_ok=True)
    run(engine, "build", "--platform", PLATFORM, "--iidfile", iid, "-f", dockerfile, *arguments, context)
    image = iid.read_text().strip()
    if not CONFIG.fullmatch(image):
        raise ValueError("Build did not return an immutable image identity")
    return inspect(engine, image)


def selected_manifest(archive):
    # archive_identity performs bounded metadata, digest and platform validation.
    metadata = binding.archive_identity(archive, PLATFORM)
    with tarfile.open(archive) as stream:
        manifest, _ = binding.read_json(stream, "index.json")
        digest = None
        for _ in range(4):
            if "manifests" not in manifest:
                break
            if len(manifest["manifests"]) != 1:
                raise ValueError("Expected one packaged platform manifest")
            manifest, digest = binding.blob(stream, manifest["manifests"][0])
        if digest is None or "config" not in manifest:
            raise ValueError("Missing packaged OCI manifest")
    return digest, metadata


def export(engine, helper, image, archive, scratch):
    docker_archive = scratch / "input.tar"
    run(engine, "save", "-o", docker_archive, image)
    # Stock Skopeo converts the local engine's archive without registry access.
    # Temporary layers live on the bounded build workspace, not helper tmpfs.
    run(engine, "run", "--rm", "--pull=never", "--network", "none",
        *user_arguments(engine),
        "--volume", str(scratch) + ":/input:rw", "--volume", str(archive.parent) + ":/output:rw",
        "--entrypoint", "/usr/bin/skopeo", helper, "--tmpdir", "/input", "copy", "--preserve-digests",
        "docker-archive:/input/input.tar", "oci-archive:/output/" + archive.name + ":release")
    docker_archive.unlink()
    digest, metadata = selected_manifest(archive)
    if metadata["localImageId"] != image:
        raise ValueError("Archive conversion changed the built image config identity: " + image + " -> " + metadata["localImageId"])
    return digest, metadata


def address_archives(inventory, archives):
    """New releases must not overwrite archives required for source recovery."""
    previous = []
    for entry in inventory["images"]:
        old = entry["archive"]
        name = entry["id"] + "-" + entry["archiveSha256"] + Path(old).suffix
        if old == name: continue
        source, target = archives / old, archives / name
        if target.exists():
            with target.open("rb") as stream:
                if hashlib.file_digest(stream, "sha256").hexdigest() != entry["archiveSha256"]:
                    raise ValueError("A content-addressed archive name contains different bytes")
        else:
            os.link(source, target)
        entry["archive"] = name
        previous.append(source)
    return previous


def package(options):
    if os.name != "posix": raise ValueError("Runtime image packaging requires the Linux build host")
    if not re.fullmatch(r"[0-9A-Za-z][0-9A-Za-z.+_-]{0,127}", options.version):
        raise ValueError("Invalid release version")
    if not re.fullmatch(r"[a-f0-9]{40}", options.source_sha):
        raise ValueError("The release must name its exact source commit")
    if not re.fullmatch(r"[0-9]+(?:\.[0-9]+){2,3}", options.numeric_version):
        raise ValueError("Invalid numeric assembly/file version")
    source, payload = options.source_root.resolve(strict=True), options.payload.resolve(strict=True)
    inputs = json.loads((source / "src/images/runtime-release-inputs.json").read_text())
    if inputs.get("schemaVersion") != 1 or inputs.get("platform") != PLATFORM:
        raise ValueError("Unsupported runtime producer inputs/platform")
    for name in ("sdkImage", "coreRuntimeImage", "setupBaseImage", "envoyImage", "dnsImage", "acquisitionImage"):
        if not isinstance(inputs.get(name), str) or not PIN.fullmatch(inputs[name]):
            raise ValueError("Unpinned runtime producer input: " + name)
    inventory_path = payload / "bin/runtime-images.json"
    archives = payload / "oci-images"
    inventory = json.loads(inventory_path.read_text())
    # Validate the existing common stage before adding anything to it.
    if binding.bind_inventory(inventory, archives, PLATFORM) != inventory:
        raise ValueError("The common archive stage has stale bindings")
    existing = {entry["id"] for entry in inventory["images"]}
    if existing != {"kautuka-base", "openobserve", "git-pkgs-proxy"}:
        raise ValueError("The existing released base/package/observability inventory is incomplete")
    if any(entry.get("archive") is None for entry in inventory["images"]):
        raise ValueError("Cold-start runtime images require signed offline archives")
    expected_archives = {entry["archive"] for entry in inventory["images"]}
    if {path.name for path in archives.iterdir()} != expected_archives:
        raise ValueError("The common stage contains unreferenced files; rebuild the image stage")
    additions = {"egress-setup", "egress-envoy", "egress-dns", "image-acquisition"} | {"core-" + name for name in CORES}
    if existing & additions:
        raise ValueError("Core/bootstrap release roles were already packaged")
    for path in (payload / "services/gateway/built-in-mcpb", payload / "services/webhost/wwwroot"):
        if not path.is_dir() or not any(path.iterdir()):
            raise ValueError("Missing prepared release assets: " + str(path))
    for name in ("kautuka-web-tools.mcpb", "kautuka-m365-mail.mcpb", "kautuka-google-mail.mcpb"):
        path = payload / "services/gateway/built-in-mcpb" / name
        if not path.is_file() or path.is_symlink():
            raise ValueError("Missing prepared built-in bundle: " + name)
    with tempfile.TemporaryDirectory(prefix="kautuka-runtime-images-", dir=options.work_root) as temporary:
        work = Path(temporary)
        clean_source = work / "source"
        clean_source.mkdir()
        # Never reuse Fedora host-publish obj/native outputs for Azure Linux.
        for directory in ("backend", "shared", "images"):
            shutil.copytree(source / "src" / directory, clean_source / directory,
                            ignore=shutil.ignore_patterns("bin", "obj", "node_modules", ".git"))
        for file in (source / "src").glob("Directory.*"):
            if file.is_file(): shutil.copyfile(file, clean_source / file.name)
        publishes = work / "publish"
        publishes.mkdir()
        nuget = Path(options.nuget_cache) if getattr(options, "nuget_cache", None) else work / "nuget"
        nuget.mkdir(parents=True, exist_ok=True)
        sdk = pull(options.runtime, inputs["sdkImage"])
        pull(options.runtime, inputs["coreRuntimeImage"])
        helper = pull(options.runtime, inputs["acquisitionImage"])
        for role, executable in CORES.items():
            print("Publishing compatible core service: " + role, flush=True)
            run(options.runtime, "run", "--rm", "--pull=never", "--volume", str(clean_source) + ":/src:rw",
                *user_arguments(options.runtime), "--volume", str(publishes) + ":/out:rw",
                "--volume", str(nuget) + ":/nuget:rw", "--env", "NUGET_PACKAGES=/nuget", "--workdir", "/src", sdk,
                "dotnet", "publish", "backend/Kautuka." + executable + "/Kautuka." + executable + ".csproj",
                "-c", "Release", "-r", "linux-x64", "--self-contained", "true", "-p:PublishAot=true",
                "-p:InvariantGlobalization=false", "-p:KautukaReleaseArtifactBuild=true", "-p:PublishSingleFile=true",
                "-p:Version=" + options.version, "-p:InformationalVersion=" + options.version + "+" + options.source_sha[:7],
                "-p:AssemblyVersion=" + options.numeric_version, "-p:FileVersion=" + options.numeric_version,
                "-p:SourceRevisionId=" + options.source_sha, "-p:IncludeSourceRevisionInInformationalVersion=false", "-o", "/out/" + role)
            binary = publishes / role / ("Kautuka." + executable)
            if not binary.is_file() or not binary.stat().st_mode & 0o111:
                raise ValueError("Native publication did not produce an executable: " + role)
            with binary.open("rb") as stream:
                if stream.read(4) != b"\x7fELF": raise ValueError("Core publication must produce a Linux Native AOT executable")
        shutil.copytree(payload / "services/gateway/built-in-mcpb", publishes / "gateway/built-in-mcpb", dirs_exist_ok=True)
        shutil.copytree(payload / "services/webhost/wwwroot", publishes / "webhost/wwwroot", dirs_exist_ok=True)
        entries = []

        def add(role, image, reference=None):
            print("Exporting offline runtime image: " + role, flush=True)
            archive = role + ".oci"
            digest, metadata = export(options.runtime, helper, image, archives / archive, work)
            entries.append({"id": role, "image": reference or "ghcr.io/devkrity/kautuka-" + role + "@" + digest,
                            "archive": archive, **metadata})

        for role, executable in CORES.items():
            context = publishes / role
            (context / ".dockerignore").write_text("Containerfile\nimage-id.txt\n.dockerignore\n")
            recipe = context / "Containerfile"
            recipe.write_text("FROM " + inputs["coreRuntimeImage"] + "\nCOPY . /app\nWORKDIR /app\n"
                              + "LABEL org.opencontainers.image.version=" + json.dumps(options.version)
                              + " org.opencontainers.image.revision=" + json.dumps(options.source_sha) + "\n"
                              + "ENTRYPOINT " + json.dumps(["/app/Kautuka." + executable]) + "\n")
            image = build(options.runtime, context, recipe)
            labels = json.loads(run(options.runtime, "image", "inspect", image))[0].get("Config", {}).get("Labels", {})
            if labels.get("org.opencontainers.image.version") != options.version or labels.get("org.opencontainers.image.revision") != options.source_sha:
                raise ValueError("Built core image release metadata differs from the selected release")
            add("core-" + role, image)
        setup = work / "setup"
        setup.mkdir()
        recipe = setup / "Containerfile"
        shutil.copyfile(source / "src/images/kautuka-egress-setup/Containerfile", recipe)
        pull(options.runtime, inputs["setupBaseImage"])
        add("egress-setup", build(options.runtime, setup, recipe, "--build-arg", "SETUP_BASE_IMAGE=" + inputs["setupBaseImage"]))
        for role, field in (("egress-envoy", "envoyImage"), ("egress-dns", "dnsImage"), ("image-acquisition", "acquisitionImage")):
            add(role, helper if field == "acquisitionImage" else pull(options.runtime, inputs[field]), inputs[field])
        inventory["images"].extend(entries)
        previous = address_archives(inventory, archives)
        if binding.bind_inventory(inventory, archives, PLATFORM) != inventory:
            raise ValueError("Final image archive bindings are inconsistent")
        temporary_inventory = inventory_path.with_suffix(".next.json")
        temporary_inventory.write_text(json.dumps(inventory, indent=2) + "\n")
        os.replace(temporary_inventory, inventory_path)
        for path in previous: path.unlink()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-root", required=True, type=Path)
    parser.add_argument("--payload", required=True, type=Path)
    parser.add_argument("--work-root", required=True, type=Path)
    parser.add_argument("--runtime", required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--numeric-version", required=True)
    parser.add_argument("--source-sha", required=True)
    package(parser.parse_args())


if __name__ == "__main__": main()
