#!/usr/bin/env node
import { mkdtemp, mkdir, readFile, readdir, rm, writeFile, chmod } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const temp = await mkdtemp(path.join(os.tmpdir(), 'kautuka-source-staging-'));
try {
  const release = path.join(temp, 'release');
  const rootfs = path.join(temp, 'rootfs');
  const mockBin = path.join(temp, 'mock-bin');
  const sourceManifest = path.join(rootfs, 'usr/share/doc/kautuka/FEDORA_SOURCE_RPMS.txt');
  await Promise.all([
    mkdir(path.dirname(sourceManifest), { recursive: true }),
    mkdir(mockBin, { recursive: true }),
  ]);
  await writeFile(sourceManifest, 'fixture-1.0-1.src.rpm\n');
  const curl = `#!/usr/bin/env bash
set -euo pipefail
output=''
url=''
while (($#)); do
  if [[ "$1" == '--output' ]]; then output="$2"; shift 2
  elif [[ "$1" == https://* ]]; then url="$1"; shift
  else shift; fi
done
printf '%s\\n' "$url" > "$output"
printf '%s\\n' "$url" >> "$MOCK_CURL_LOG"
`;
  const runtime = `#!/usr/bin/env bash
set -euo pipefail
args=("$@")
joined=" $* "
if [[ "$joined" == *" /bin/cat "* ]]; then
  printf 'fixture-1.0-1.src.rpm\\n'
  exit 0
fi
for arg in "${'${args[@]}'}"; do
  if [[ "$arg" == *":/output/srpms:rw" ]]; then
    exit 0
  fi
done
echo 'unexpected mock runtime invocation' >&2
exit 1
`;
  for (const [name, contents] of [['curl', curl], ['mock-container-runtime', runtime]]) {
    const file = path.join(mockBin, name);
    await writeFile(file, contents);
    await chmod(file, 0o755);
  }
  const toMsysPath = (value) => {
    const resolved = path.resolve(value).replaceAll('\\', '/');
    if (process.platform !== 'win32') return resolved;
    return /^[A-Za-z]:/.test(resolved) ? `/${resolved[0].toLowerCase()}${resolved.slice(2)}` : resolved;
  };
  const quote = (value) => `'${value.replaceAll("'", "'\\''")}'`;
  const env = {
    ...process.env,
    KAUTUKA_RELEASE_CURL: toMsysPath(path.join(mockBin, 'curl')),
    KAUTUKA_RELEASE_CONTAINER_RUNTIME: toMsysPath(path.join(mockBin, 'mock-container-runtime')),
    MOCK_CURL_LOG: toMsysPath(path.join(temp, 'curl-urls.txt')),
  };
  const args = [
    '--release-dir', release,
    '--wsl-rootfs', rootfs,
    '--base-image', `ghcr.io/devkrity/kautuka-base@sha256:${'a'.repeat(64)}`,
  ];
  const command = `cd ${quote(toMsysPath(root))} && bash ./scripts/stage-third-party-sources.sh ${args.map(quote).join(' ')}`;
  const gitBash = 'C:/Program Files/Git/bin/bash.exe';
  const bash = process.platform === 'win32' && existsSync(gitBash) ? gitBash : 'bash';
  const result = spawnSync(bash, ['-c', command], { env, encoding: 'utf8' });
  if (result.status !== 0) throw new Error(`mocked source staging failed (status ${result.status}, ${result.error ?? 'no process error'}): ${result.stderr ?? ''}${result.stdout ?? ''}`);

  const sourceDir = path.join(release, 'sources');
  const archives = await readdir(sourceDir);
  const expected = [
    ['elkjs-ff5771d7165445c42c408bb8a090c8035272218c.tar.gz', 'https://codeload.github.com/kieler/elkjs/tar.gz/ff5771d7165445c42c408bb8a090c8035272218c'],
    ['elkjs-mermaid-a8304cf79fde75bc2ab1a89d28320f53f8637436.tar.gz', 'https://codeload.github.com/kieler/elkjs/tar.gz/a8304cf79fde75bc2ab1a89d28320f53f8637436'],
  ];
  for (const [filename, expectedUrl] of expected) {
    if (!archives.includes(filename)) throw new Error(`staging omitted expected exact source snapshot ${filename}`);
    const recordedUrl = (await readFile(path.join(sourceDir, filename), 'utf8')).trim();
    if (recordedUrl !== expectedUrl) throw new Error(`staging selected the wrong repository or commit for ${filename}: ${JSON.stringify(recordedUrl.slice(0, 180))}`);
  }
  const sums = await readFile(path.join(sourceDir, 'SHA256SUMS'), 'utf8');
  if (!sums.includes(expected[0][0]) || !sums.includes(expected[1][0])) throw new Error('source checksum manifest omitted an elkjs source snapshot');
  const fedoraSourceUrl = 'https://kojipkgs.fedoraproject.org/packages/fixture/1.0/1/src/fixture-1.0-1.src.rpm';
  const curlUrls = await readFile(path.join(temp, 'curl-urls.txt'), 'utf8');
  if (!curlUrls.includes(fedoraSourceUrl)) throw new Error('staging did not fall back to the exact Fedora Koji source RPM URL');
  const sourceChecksums = await readFile(path.join(sourceDir, 'Fedora-SOURCE-RPMS.sha256'), 'utf8');
  if (!sourceChecksums.includes('fixture-1.0-1.src.rpm')) throw new Error('Fedora source checksum manifest omitted the Koji-fetched RPM');
  console.log('mocked source staging selected and checksummed exact upstream source snapshots and exercised Fedora Koji fallback');
} finally {
  await rm(temp, { recursive: true, force: true });
}
