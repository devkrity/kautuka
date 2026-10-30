import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { chmod, mkdir, mkdtemp, readFile, rm, stat, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const helper = path.join(root, 'scripts/resolve-release-dotnet-sdk.sh');
const temp = await mkdtemp(path.join(tmpdir(), 'kautuka-release-sdk-'));
try {
  const fixturePath = path.join(temp, 'global.json');
  await writeFile(fixturePath, JSON.stringify({ sdk: { version: '10.0.987' } }));
  if (process.platform !== 'win32') {
    const cleanEnv = { ...process.env };
    delete cleanEnv.KAUTUKA_RELEASE_DOTNET_SDK_VERSION;
    assert.equal(execFileSync('bash', [helper, fixturePath], { encoding: 'utf8', env: cleanEnv }).trim(), '10.0.987', 'default must follow the supplied project SDK');
    assert.equal(execFileSync('bash', [helper, fixturePath], { encoding: 'utf8', env: { ...cleanEnv, KAUTUKA_RELEASE_DOTNET_SDK_VERSION: '10.0.123' } }).trim(), '10.0.123', 'explicit SDK override must reach the container build');
    const invalid = spawnSync('bash', [helper, fixturePath], { encoding: 'utf8', env: { ...cleanEnv, KAUTUKA_RELEASE_DOTNET_SDK_VERSION: 'latest' } });
    assert.notEqual(invalid.status, 0, 'non-numeric SDK overrides must fail closed');
  }

  const [builder, image, globalJson] = await Promise.all([
    readFile(path.join(root, 'scripts/build-release-assets.sh'), 'utf8'),
    readFile(path.join(root, 'src/images/release-builder/Containerfile'), 'utf8'),
    readFile(path.join(root, 'global.json'), 'utf8'),
  ]);
  const projectSdk = JSON.parse(globalJson).sdk.version;
  const normalizedBuilder = builder.replaceAll('\r\n', '\n');
  assert.match(builder, /resolve-release-dotnet-sdk\.sh.*global\.json/, 'builder must derive its SDK from project global.json');
  assert.match(builder, /--build-arg "DOTNET_SDK_VERSION=\$dotnet_sdk_version"/, 'builder must pass the selected SDK to the image');
  assert.match(builder, /KAUTUKA_RELEASE_RETAIN_FAILED_WORKDIR:-false/, 'failed work directory retention must remain opt-in');
  assert.match(image, /^ARG DOTNET_SDK_VERSION\s*$/m, 'image SDK must be supplied explicitly by the builder');
  assert.match(image, /test -n "\$DOTNET_SDK_VERSION"/, 'image build must reject a missing SDK argument');

  const mcpStage = normalizedBuilder.split('log_phase "Building built-in MCP bundles"')[1]?.split('normalize_owned_paths "$built_in_mcpb_stage_dir"')[0];
  assert.ok(mcpStage, 'builder must retain the built-in MCP build stage');
  assert.match(mcpStage, /uv_version=0\.13\.6/, 'MCP stage must install the pinned uv release in its default build container');
  assert.match(mcpStage, /f29c3e94ffabd7e6f3ba521dba169a9b7ec12100bf3257087f0f24f6123f3645/, 'MCP stage must verify the pinned uv archive digest');
  assert.ok(mcpStage.indexOf('sha256sum --check') < mcpStage.indexOf('stage-built-in-mcpb.sh'), 'MCP dependency lock verification must run only after uv archive verification');

  if (process.platform !== 'win32') {
    const logIndex = normalizedBuilder.indexOf('log_phase "Building built-in MCP bundles"');
    const start = normalizedBuilder.indexOf('run_build_container \\\n', logIndex);
    const end = normalizedBuilder.indexOf('\nnormalize_owned_paths "$built_in_mcpb_stage_dir"', start);
    assert.ok(start >= 0 && end > start, 'must locate actual MCP run_build_container invocation');
    const captured = path.join(temp, 'mcp-command.sh');
    const harness = [
      'set -euo pipefail',
      'bind_mount() { printf "%s:%s" "$1" "$2"; }',
      'run_build_container() { local previous= arg= inner=; for arg in "$@"; do if [[ "$previous" == "-lc" ]]; then inner=$arg; break; fi; previous=$arg; done; test -n "$inner"; printf "%s" "$inner" > "$CAPTURE"; bash -n -c "$inner"; }',
      normalizedBuilder.slice(start, end),
    ].join('\n');
    execFileSync('bash', ['-e', '-u', '-o', 'pipefail', '-c', harness], {
      env: { ...process.env, CAPTURE: captured, source_root: '/source', built_in_mcpb_stage_dir: '/output', node_build_image: 'quay.io/fedora/fedora:44', node_version: '26.3.0', node_arch: 'x64' },
    });
    const innerCommand = await readFile(captured, 'utf8');
    assert.match(innerCommand, /uv_version=0\.13\.6/, 'outer builder must preserve inner uv variables for the container');
    assert.match(innerCommand, /releases\/download\/\$\{uv_version\}\/\$\{uv_archive\}/, 'container must expand pinned uv URL variables inside the container');
    assert.match(innerCommand, /printf '%s  %s\\n' "\$uv_sha256"/, 'container must expand the expected uv digest only inside the container');
    assert.match(innerCommand, /nodejs\.org\/dist\/v26\.3\.0\//, 'configured Node version must be expanded by the outer builder');
  }

  const assembly = builder.match(/chmod \+x "\$stage_dir\/bin\/kautuka-manager"[\s\S]*?mkdir -p "\$stage_dir\/services\/webhost\/wwwroot"/)?.[0];
  assert.ok(assembly, 'platform assembly must chmod staged executables');
  const chmodLines = assembly.split(/\r?\n/).filter((line) => line.includes('kautuka-webhost'));
  assert.equal(chmodLines.at(-1), '  "$stage_dir/services/webhost/kautuka-webhost"', 'last chmod target must not continue into the next command');

  if (process.platform !== 'win32') {
    const fixtureStage = path.join(temp, 'stage');
    const fixtureSource = path.join(temp, 'source');
    const executables = [
      'bin/kautuka-manager',
      'bin/kautuka-apply-release',
      'services/gateway/kautuka-gateway',
      'services/credentialshost/kautuka-credentialshost',
      'services/channelhost/kautuka-channelhost',
      'services/updateservice/kautuka-updateservice',
      'services/webhost/kautuka-webhost',
    ];
    for (const relative of executables) {
      const target = path.join(fixtureStage, relative);
      await mkdir(path.dirname(target), { recursive: true });
      await writeFile(target, 'fixture executable');
    }
    await mkdir(path.join(fixtureSource, 'scripts'), { recursive: true });
    await writeFile(path.join(fixtureSource, 'scripts/kautuka-apply-release.sh'), 'fixture apply script');
    await mkdir(path.join(fixtureSource, 'src/frontend/web/dist'), { recursive: true });
    await writeFile(path.join(fixtureSource, 'src/frontend/web/dist/index.html'), 'fixture web payload');
    const start = builder.indexOf('cp "$source_root/scripts/kautuka-apply-release.sh"');
    const end = builder.indexOf('\nassert_staged_sidecar_seal "$stage_dir"', start);
    assert.ok(start >= 0 && end > start, 'must locate the complete final assembly command block');
    execFileSync('bash', ['-e', '-u', '-o', 'pipefail', '-c', builder.slice(start, end)], {
      env: { ...process.env, stage_dir: fixtureStage, source_root: fixtureSource },
    });
    for (const relative of executables) {
      assert.equal((await stat(path.join(fixtureStage, relative))).mode & 0o111, 0o111, `executable mode must be set for ${relative}`);
    }
    assert.equal(await readFile(path.join(fixtureStage, 'services/webhost/wwwroot/index.html'), 'utf8'), 'fixture web payload');
  }

  console.log(`Release build SDK, MCP toolchain, and platform assembly contracts pass (project SDK ${projectSdk}, uv 0.13.6).`);
} finally {
  await rm(temp, { recursive: true, force: true });
}
