#!/usr/bin/env python3
"""Exercise the installer socket helper with real tmpfiles and a different UID."""
import fcntl
import grp
import os
from pathlib import Path
import pwd
import subprocess
import tempfile
import unittest

SOURCE = Path(__file__).with_name('kautuka-install-linux.sh.in').read_text()
FUNCTION = 'install_socket_runtime_dirs() {' + SOURCE.split('install_socket_runtime_dirs() {', 1)[1].split('\ninstall_apply_release_helper() {', 1)[0]
ROOTLESS = 'ensure_rootless_podman_ready() {' + SOURCE.split('ensure_rootless_podman_ready() {', 1)[1].split('\nensure_service_account() {', 1)[0]

@unittest.skipUnless(os.name == 'posix' and os.geteuid() == 0, 'Linux root and systemd-tmpfiles required')
class ContainerSocketsTests(unittest.TestCase):
    def setUp(self):
        self.scratch = tempfile.TemporaryDirectory(prefix='kautuka-container-sockets-')
        self.root = Path(self.scratch.name); self.root.chmod(0o755)
        self.account = pwd.getpwnam('nobody')
        self.group = grp.getgrgid(self.account.pw_gid).gr_name
        # Only absolute fixture locations change; execute the real shell helper
        # and systemd-tmpfiles, with real owner/group permission enforcement.
        function = FUNCTION.replace('/usr/lib/tmpfiles.d', str(self.root / 'tmpfiles.d'))
        function = function.replace('/run/kautuka', str(self.root / 'sockets'))
        self.script = self.root / 'run.sh'
        self.script.write_text('set -euo pipefail\nfail() { echo "$*" >&2; exit 1; }\n' + function
                               + '\ninstall_socket_runtime_dirs nobody "${1:-containers}" ' + self.group + '\n')

    def tearDown(self):
        self.scratch.cleanup()

    def run_helper(self, layout='containers'):
        return subprocess.run(['bash', str(self.script), layout], capture_output=True, text=True)

    def test_runtime_owner_can_bind_and_writer_lock_inode_survives_recreation(self):
        result = self.run_helper()
        self.assertEqual(0, result.returncode, result.stderr)
        parent = self.root / 'sockets'
        self.assertEqual(0, parent.stat().st_uid)
        for service in ('manager', 'updateservice', 'gateway', 'credentialshost', 'channelhost'):
            directory = parent / service
            self.assertEqual(self.account.pw_uid, directory.stat().st_uid)
            self.assertEqual(self.account.pw_gid, directory.stat().st_gid)
            self.assertEqual(0o2750, directory.stat().st_mode & 0o7777)
            bind = subprocess.run(['runuser', '-u', 'nobody', '--', 'python3', '-c',
                                   'import socket,sys,os; p=sys.argv[1]; s=socket.socket(socket.AF_UNIX); s.bind(p); os.chmod(p,0o660); s.close(); os.unlink(p)',
                                   str(directory / 'test.sock')], capture_output=True, text=True)
            self.assertEqual(0, bind.returncode, bind.stderr)
        lock = self.root / 'sockets-db-writer.lock'
        self.assertEqual(0, lock.stat().st_uid)
        self.assertEqual(0o644, lock.stat().st_mode & 0o777)
        inode = lock.stat().st_ino
        with lock.open('rb') as stream:
            fcntl.flock(stream, fcntl.LOCK_SH | fcntl.LOCK_NB)
            result = self.run_helper()
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual(inode, lock.stat().st_ino)
        config = (self.root / 'tmpfiles.d/kautuka-sockets.conf').read_text()
        self.assertNotIn('kautuka-gateway-client', config)
        self.assertNotIn('kautuka-credentials:', config)

    def test_unknown_layout_is_rejected_before_writing_configuration(self):
        result = self.run_helper('unknown')
        self.assertNotEqual(0, result.returncode)
        self.assertFalse((self.root / 'tmpfiles.d').exists())

    def test_failed_rootless_info_keeps_container_home_root_owned(self):
        home = self.root / 'home'; home.mkdir(mode=0o750)
        os.chown(home, 0, self.account.pw_gid)
        runtime = self.root / 'runtime'; runtime.mkdir(mode=0o700)
        script = self.root / 'rootless.sh'
        script.write_text('''set -euo pipefail
fail() { echo "$*" >&2; exit 1; }
ensure_subid_entry() { :; }
ensure_rootless_idmap_helpers() { :; }
validate_linger_enabled() { :; }
install_user_manager_restart_dropin() { :; }
loginctl() { :; }
systemctl() { :; }
run_as_service_user() { return 72; }
''' + ROOTLESS + '\nensure_rootless_podman_ready nobody ' + self.group + ' "' + str(home) + '" "' + str(runtime) + '" containers\n')
        result = subprocess.run(['bash', str(script)], capture_output=True, text=True)
        self.assertEqual(72, result.returncode, result.stderr)
        self.assertEqual(0, home.stat().st_uid)
        self.assertEqual(0o750, home.stat().st_mode & 0o777)
        self.assertEqual(self.account.pw_uid, (home / '.config/containers/storage.conf').stat().st_uid)

if __name__ == '__main__': unittest.main()
