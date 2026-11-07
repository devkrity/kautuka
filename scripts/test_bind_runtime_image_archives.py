import hashlib
import importlib.util
import io
import json
import pathlib
import tarfile
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("binding", pathlib.Path(__file__).with_name("bind-runtime-image-archives.py"))
binding = importlib.util.module_from_spec(spec)
spec.loader.exec_module(binding)


class ArchiveBindings(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix="kautuka-binding-")
        self.addCleanup(self.directory.cleanup)
        self.root = pathlib.Path(self.directory.name)
        self.config = json.dumps({"os": "linux", "architecture": "amd64", "rootfs": {"type": "layers", "diff_ids": []}}).encode()
        self.config_id = "sha256:" + hashlib.sha256(self.config).hexdigest()

    def archive(self, name, oci=True, tamper=False, multiple=False):
        payloads = {}
        if oci:
            config_path = "blobs/sha256/" + self.config_id.split(":")[1]
            payloads[config_path] = self.config + (b" " if tamper else b"")
            manifest = json.dumps({"schemaVersion": 2, "config": {"digest": self.config_id}, "layers": []}).encode()
            digest = "sha256:" + hashlib.sha256(manifest).hexdigest()
            payloads["blobs/sha256/" + digest.split(":")[1]] = manifest
            payloads["index.json"] = json.dumps({"manifests": [{"digest": digest}] * (2 if multiple else 1)}).encode()
        else:
            payloads[self.config_id.split(":")[1] + ".json"] = self.config
            payloads["manifest.json"] = json.dumps([{"Config": self.config_id.split(":")[1] + ".json", "Layers": []}]).encode()
        target = self.root / name
        with tarfile.open(target, "w") as archive:
            for path, data in payloads.items():
                member = tarfile.TarInfo(path)
                member.size = len(data)
                archive.addfile(member, io.BytesIO(data))
        return target

    def inventory(self, archive):
        return {"schemaVersion": 1, "images": [{"id": "core-gateway", "image": "ghcr.io/devkrity/test@sha256:" + "a" * 64, "archive": archive}]}

    def test_oci_and_docker_wrappers_bind_the_same_config(self):
        oci = self.archive("image.oci")
        docker = self.archive("image.tar", oci=False)
        first = binding.bind_inventory(self.inventory(oci.name), self.root, "linux/amd64")["images"][0]
        second = binding.bind_inventory(self.inventory(docker.name), self.root, "linux/amd64")["images"][0]
        self.assertEqual(first["localImageId"], self.config_id)
        self.assertEqual(second["localImageId"], self.config_id)
        self.assertEqual(first["archiveSha256"], hashlib.sha256(oci.read_bytes()).hexdigest())
        self.assertNotEqual(first["archiveSha256"], second["archiveSha256"])

    def test_wrong_platform_is_not_bound(self):
        target = self.archive("image.oci")
        with self.assertRaisesRegex(ValueError, "platform"):
            binding.bind_inventory(self.inventory(target.name), self.root, "linux/arm64")

    def test_changed_blob_is_not_signed_as_the_original_config(self):
        target = self.archive("image.oci", tamper=True)
        with self.assertRaisesRegex(ValueError, "digest mismatch"):
            binding.bind_inventory(self.inventory(target.name), self.root, "linux/amd64")

    def test_multi_image_archives_require_platform_selection_first(self):
        target = self.archive("image.oci", multiple=True)
        with self.assertRaisesRegex(ValueError, "one selected"):
            binding.bind_inventory(self.inventory(target.name), self.root, "linux/amd64")

    def test_inventory_cannot_escape_the_archive_directory(self):
        with self.assertRaisesRegex(ValueError, "basename"):
            binding.bind_inventory(self.inventory("../image.oci"), self.root, "linux/amd64")


if __name__ == "__main__":
    unittest.main()
