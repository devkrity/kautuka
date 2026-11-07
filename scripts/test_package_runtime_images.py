#!/usr/bin/env python3
"""Exercise release composition with a synthetic engine and real OCI binding."""
from contextlib import redirect_stdout
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import tarfile
import tempfile
from types import SimpleNamespace
import unittest

ROOT = Path(__file__).resolve().parent.parent
SPEC = importlib.util.spec_from_file_location("runtime_producer", ROOT / "scripts/package-runtime-images.py")
producer = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(producer)


def blob(data):
    return "sha256:" + hashlib.sha256(data).hexdigest()


def write_tar(path, members):
    with tarfile.open(path, "w") as archive:
        for name, data in members.items():
            entry = tarfile.TarInfo(name)
            entry.size = len(data)
            archive.addfile(entry, io.BytesIO(data))


def write_oci(path, config):
    config_id = blob(config)
    manifest = json.dumps({"schemaVersion": 2, "config": {"digest": config_id}, "layers": []}).encode()
    manifest_id = blob(manifest)
    index = json.dumps({"schemaVersion": 2, "manifests": [{"digest": manifest_id}]}).encode()
    write_tar(path, {"oci-layout": b'{"imageLayoutVersion":"1.0.0"}', "index.json": index,
                     "blobs/sha256/" + config_id[7:]: config, "blobs/sha256/" + manifest_id[7:]: manifest})


class Engine:
    def __init__(self):
        self.images = {}
        self.calls = []
        self.change_export_config = False
        self.drop_release_labels = False
        self.context_files = {}

    def image(self, name, labels=None):
        config = json.dumps({"os": "linux", "architecture": "amd64", "config": {"Labels": labels or {"fixture": name}},
                             "rootfs": {"type": "layers", "diff_ids": []}}).encode()
        identity = blob(config)
        self.images[name] = self.images[identity] = config
        return identity

    def __call__(self, engine, *raw):
        args = [str(value) for value in raw]
        self.calls.append(args)
        if args[0] == "pull":
            self.image(args[-1]); return ""
        if args[:2] == ["image", "inspect"]:
            config = self.images[args[-1]]
            value = json.loads(config)
            return json.dumps([{"Id": blob(config), "Os": value["os"], "Architecture": value["architecture"], "Config": value["config"]}])
        if args[0] == "build":
            context = Path(args[-1])
            role = context.name
            self.context_files[role] = [p.relative_to(context).as_posix() for p in context.rglob("*") if p.is_file()]
            labels = None
            if role in producer.CORES and not self.drop_release_labels:
                labels = {"org.opencontainers.image.version": "1.2.3", "org.opencontainers.image.revision": "a" * 40}
            identity = self.image(role, labels)
            Path(args[args.index("--iidfile") + 1]).write_text(identity)
            return ""
        if args[0] == "save":
            config = self.images[args[-1]]
            name = blob(config)[7:] + ".json"
            write_tar(Path(args[2]), {name: config, "manifest.json": json.dumps([{"Config": name, "Layers": []}]).encode()})
            return ""
        if args[0] == "run":
            mounts = [args[i + 1] for i, value in enumerate(args) if value == "--volume"]
            if "dotnet" in args:
                root = Path(next(value.removesuffix(":/src:rw") for value in mounts if value.endswith(":/src:rw")))
                assert not (root / "backend/obj/host-native.o").exists()
                output = Path(next(value.removesuffix(":/out:rw") for value in mounts if value.endswith(":/out:rw")))
                role = args[-1].split("/")[-1]
                (output / role).mkdir()
                binary = output / role / ("Kautuka." + producer.CORES[role])
                binary.write_bytes(b"\x7fELFsynthetic-aot")
                binary.chmod(0o755)
            else:
                assert "none" == args[args.index("--network") + 1]
                assert "--preserve-digests" in args
                scratch = Path(next(value.removesuffix(":/input:rw") for value in mounts if value.endswith(":/input:rw")))
                output = Path(next(value.removesuffix(":/output:rw") for value in mounts if value.endswith(":/output:rw")))
                with tarfile.open(scratch / "input.tar") as archive:
                    manifest = json.load(archive.extractfile("manifest.json"))
                    config = archive.extractfile(manifest[0]["Config"]).read()
                if self.change_export_config: config += b" "
                name = args[-1].removeprefix("oci-archive:/output/").removesuffix(":release")
                write_oci(output / name, config)
            return ""
        raise AssertionError("Unexpected engine command: " + str(args))


@unittest.skipUnless(producer.os.name == "posix", "The release image build host is Linux")
class ProducerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.source = self.root / "source"
        for path in ("src/backend/obj", "src/shared", "src/images/kautuka-egress-setup"):
            (self.source / path).mkdir(parents=True)
        (self.source / "src/backend/obj/host-native.o").write_bytes(b"incompatible-host-object")
        (self.source / "src/Directory.Build.props").write_text("<Project />")
        for path in ("src/images/runtime-release-inputs.json", "src/images/kautuka-egress-setup/Containerfile"):
            (self.source / path).write_bytes((ROOT / path).read_bytes())
        self.payload = self.root / "payload"
        (self.payload / "bin").mkdir(parents=True)
        (self.payload / "oci-images").mkdir()
        self.inventory = self.payload / "bin/runtime-images.json"
        images = []
        for role in ("kautuka-base", "openobserve", "git-pkgs-proxy"):
            archive = role + ".oci"
            config = json.dumps({"os": "linux", "architecture": "amd64"}).encode()
            write_oci(self.payload / "oci-images" / archive, config)
            images.append({"id": role, "image": "ghcr.io/example/" + role + "@sha256:" + "b" * 64, "archive": archive})
        inventory = producer.binding.bind_inventory({"schemaVersion": 1, "images": images}, self.payload / "oci-images", producer.PLATFORM)
        self.inventory.write_text(json.dumps(inventory, indent=2) + "\n")
        bundles = self.payload / "services/gateway/built-in-mcpb"
        bundles.mkdir(parents=True)
        for name in ("kautuka-web-tools", "kautuka-m365-mail", "kautuka-google-mail"):
            (bundles / (name + ".mcpb")).write_bytes(b"bundle")
        web = self.payload / "services/webhost/wwwroot"
        web.mkdir(parents=True)
        (web / "index.html").write_text("release web assets")
        self.options = SimpleNamespace(source_root=self.source, payload=self.payload, work_root=self.root,
                                       runtime="fixture-runtime", version="1.2.3", numeric_version="1.2.3.0", source_sha="a" * 40)
        self.engine = Engine()
        self.previous = producer.run
        producer.run = self.engine

    def tearDown(self):
        producer.run = self.previous
        self.temp.cleanup()

    def test_complete_offline_inventory_preserves_release_assets_and_exact_build_ids(self):
        with redirect_stdout(io.StringIO()): producer.package(self.options)
        inventory = json.loads(self.inventory.read_text())
        self.assertEqual(11, len(inventory["images"]))
        self.assertEqual(inventory, producer.binding.bind_inventory(inventory, self.payload / "oci-images", producer.PLATFORM))
        self.assertEqual({entry["archive"] for entry in inventory["images"]}, {p.name for p in (self.payload / "oci-images").iterdir()})
        self.assertTrue(all(entry["archiveSha256"] in entry["archive"] for entry in inventory["images"]))
        self.assertIn("wwwroot/index.html", self.engine.context_files["webhost"])
        self.assertIn("built-in-mcpb/kautuka-google-mail.mcpb", self.engine.context_files["gateway"])
        self.assertEqual(4, sum("dotnet" in command for command in self.engine.calls))
        self.assertFalse(any(command[0] == "pull" and "kautuka-core-" in command[-1] for command in self.engine.calls))

    def test_missing_bundle_fails_before_image_acquisition(self):
        (self.payload / "services/gateway/built-in-mcpb/kautuka-google-mail.mcpb").unlink()
        with self.assertRaisesRegex(ValueError, "Missing prepared built-in"):
            producer.package(self.options)
        self.assertEqual([], self.engine.calls)

    def test_conversion_identity_change_cannot_publish_inventory(self):
        before = self.inventory.read_bytes()
        self.engine.change_export_config = True
        with redirect_stdout(io.StringIO()), self.assertRaisesRegex(ValueError, "changed.*config identity"):
            producer.package(self.options)
        self.assertEqual(before, self.inventory.read_bytes())

    def test_missing_release_labels_cannot_publish_inventory(self):
        before = self.inventory.read_bytes()
        self.engine.drop_release_labels = True
        with redirect_stdout(io.StringIO()), self.assertRaisesRegex(ValueError, "release metadata"):
            producer.package(self.options)
        self.assertEqual(before, self.inventory.read_bytes())


if __name__ == "__main__": unittest.main()
