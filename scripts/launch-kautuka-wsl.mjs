import { spawn } from 'node:child_process';
import { createHash, randomUUID } from 'node:crypto';
import { existsSync, realpathSync, readdirSync, statSync } from 'node:fs';
import { createServer } from 'node:net';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const markerPath = '/etc/kautuka-dev.json';

export function parseOptions(args, defaultDistro = 'KautukaDev') {
  const options = { distro: defaultDistro, port: 20229, skipBuild: false, stop: false, help: false, service: null, action: null };
  for (let i = 0; i < args.length; i++) {
    switch (args[i]) {
      case '--distro': options.distro = args[++i]; break;
      case '--port': options.port = Number(args[++i]); break;
      case '--skip-build': options.skipBuild = true; break;
      case '--stop': options.stop = true; break;
      case '--service': options.service = args[++i]; break;
      case '--action': options.action = args[++i]; break;
      case '--help': case '-h': options.help = true; break;
      default: throw new Error(`Unknown option: ${args[i]}. Use --help.`);
    }
  }
  if (!/^KautukaDev(?:-[A-Za-z0-9-]+)?$/.test(options.distro ?? '')) {
    throw new Error('The distro must be named KautukaDev or KautukaDev-<name>. Existing application/test/Podman distros cannot be adopted.');
  }
  if (!Number.isInteger(options.port) || options.port < 1024 || options.port > 65535) throw new Error('Invalid --port.');
  if (options.service !== null && !['frontend', 'webhost', 'gateway', 'channelhost', 'credentialshost', 'manager', 'updateservice'].includes(options.service)) throw new Error('Invalid --service. Choose frontend, webhost, gateway, channelhost, credentialshost, manager or updateservice.');
  if (options.action !== null && !['start', 'restart', 'rebuild'].includes(options.action)) throw new Error('Invalid --action. Choose start, restart or rebuild.');
  if (options.action && !options.service) throw new Error('--action requires --service.');
  if (options.service && (options.stop || options.skipBuild)) throw new Error('--service cannot be combined with --stop or --skip-build. Use --action start, restart or rebuild.');
  if (options.service && !options.action) options.action = 'restart';
  return options;
}

export function workspaceIdentity(workspace) {
  return createHash('sha256').update(workspace.replaceAll('\\', '/').toLowerCase()).digest('hex');
}

export function assertOwnership(text, identity) {
  let marker;
  try { marker = JSON.parse(text.replaceAll('\0', '')); } catch { throw new Error('The selected distro has no valid Kautuka development ownership marker. It was not changed.'); }
  if (marker.schemaVersion !== 1 || marker.workspaceId !== identity) throw new Error('The selected development distro belongs to another checkout. Choose a different --distro KautukaDev-<name>.');
}

function execute(command, args, { capture = false, input, cwd = repo } = {}) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, { cwd, windowsHide: true, stdio: [input === undefined ? 'inherit' : 'pipe', capture ? 'pipe' : 'inherit', capture ? 'pipe' : 'inherit'] });
    let output = '', errorOutput = '';
    child.stdout?.on('data', b => { output += b.toString(); });
    child.stderr?.on('data', b => { errorOutput += b.toString(); });
    child.once('error', reject);
    child.once('exit', code => code === 0 ? resolve(output.replaceAll('\0', '')) : reject(new Error(`${command} failed (${code}). ${capture ? errorOutput.slice(-2000) : 'See output above.'}`)));
    if (input !== undefined) child.stdin.end(input);
  });
}

export async function requireFreePort(port) {
  for (const host of ['127.0.0.1', '::1']) {
    await new Promise((resolve, reject) => {
      const server = createServer();
      server.once('error', error => {
        if (host === '::1' && ['EAFNOSUPPORT', 'EADDRNOTAVAIL'].includes(error.code)) resolve();
        else reject(new Error(`Port ${port} is already in use. Stop the existing dev stack with Ctrl+C, or use --port <unused-port>.`));
      });
      server.listen(port, host, () => server.close(resolve));
    });
  }
}

async function stageAssets({ web = true, bundles = true } = {}) {
  if (web) await execute(process.execPath, ['node_modules/vite/bin/vite.js', 'build'], { cwd: path.join(repo, 'src/frontend/web') });
  if (!bundles) return;
  const bundle = path.join(repo, 'artifacts/mcpb/kautuka-web-tools-dev.mcpb');
  const newestInput = directory => {
    let newest = statSync(directory).mtimeMs;
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      if (['.venv', '.build', '__pycache__', 'node_modules', '.git'].includes(entry.name)) continue;
      const file = path.join(directory, entry.name);
      newest = Math.max(newest, entry.isDirectory() ? newestInput(file) : statSync(file).mtimeMs);
    }
    return newest;
  };
  if (existsSync(bundle) && statSync(bundle).mtimeMs >= newestInput(path.join(repo, 'mcps/kautuka-web-tools'))) return;
  const args = ['-y', '@anthropic-ai/mcpb', 'pack', 'mcps/kautuka-web-tools', 'artifacts/mcpb/kautuka-web-tools-dev.mcpb'];
  if (process.platform === 'win32') {
    await execute(process.execPath, [path.join(path.dirname(process.execPath), 'node_modules/npm/bin/npx-cli.js'), ...args]);
  } else await execute('npx', args);
}

export async function launchDevelopmentAppliance(args) {
  const identity = workspaceIdentity(realpathSync(repo));
  const options = parseOptions(args, 'KautukaDev-' + identity.slice(0, 8));
  if (options.help) {
    console.log('Usage: npm run launch -- [--distro KautukaDev[-name]] [--port 20229] [--skip-build] [--stop]\n       npm run launch -- --service <name> [--action start|restart|rebuild]\nWindows: runs the complete Native AOT stack in its own Fedora 44 WSL distro.\nFirst launch installs the distro/prerequisites and builds all services and .NET connectors with Native AOT. Data persists between launches.\n--skip-build starts the last successful AOT build. Ctrl+C stops only this development stack.\n--service targets an initialized development stack; default action is restart.\nServices: frontend, webhost, gateway, channelhost, credentialshost, manager, updateservice.\nfrontend rebuild replaces only static assets; core service rebuild publishes only that service with Native AOT.\nCore actions briefly restart Manager and may interrupt active tool calls; other core containers remain running.\nManager/UpdateService support start/restart; rebuild them with a normal full launch.');
    return;
  }
  if (process.platform !== 'win32') throw new Error('The isolated development launcher currently requires Windows WSL. Use the Native AOT Linux appliance for Linux testing; host-process/JIT fallback is not supported.');
  const wsl = ['-d', options.distro, '-u', 'root', '--cd', '/', '--exec'];
  console.log(`[launch-kautuka] Development distro: ${options.distro}`);
  const distros = (await execute('wsl.exe', ['--list', '--quiet'], { capture: true })).split(/\r?\n/).map(v => v.trim());
  if (!distros.includes(options.distro)) {
    if (options.stop || options.skipBuild || options.service) throw new Error('No development distro/build exists. Run npm run launch first.');
    await requireFreePort(options.port);
    console.log(`[launch-kautuka] Creating dedicated Fedora 44 WSL distro ${options.distro}...`);
    await execute('wsl.exe', ['--install', '--distribution', 'FedoraLinux-44', '--name', options.distro, '--vhd-size', '64GB', '--no-launch']);
    await execute('wsl.exe', [...wsl, 'sh', '-c', 'umask 077; test ! -e /etc/kautuka-dev.json && cat > /etc/kautuka-dev.json'], { input: JSON.stringify({ schemaVersion: 1, workspaceId: identity }) });
    await execute('wsl.exe', [...wsl, 'sh', '-c', 'printf "[boot]\\nsystemd=true\\n" > /etc/wsl.conf']);
    await execute('wsl.exe', ['--terminate', options.distro]);
  }
  assertOwnership(await execute('wsl.exe', [...wsl, 'cat', markerPath], { capture: true }), identity);
  const linuxRepo = (await execute('wsl.exe', [...wsl, 'wslpath', '-a', repo], { capture: true })).trim();
  const driver = `${linuxRepo}/scripts/dev-appliance.py`;
  const common = ['python3', '-B', driver, '--repo', linuxRepo, '--workspace-id', identity, '--port', String(options.port)];
  if (options.stop) { await execute('wsl.exe', [...wsl, ...common, 'stop']); return; }
  if (options.service) {
    if (options.action === 'rebuild' && ['frontend', 'webhost'].includes(options.service)) await stageAssets({ bundles: false });
    console.log(`[launch-kautuka] ${options.action} ${options.service}; retaining application data and other core containers.`);
    await execute('wsl.exe', [...wsl, ...common, '--service', options.service, '--service-action', options.action, 'service']);
    return;
  }
  common.push('--session', randomUUID());
  await requireFreePort(options.port);
  if (!options.skipBuild) {
    console.log('[launch-kautuka] Preparing frontend and built-in connector bundles...');
    await stageAssets();
    await execute('wsl.exe', [...wsl, 'bash', `${linuxRepo}/scripts/dev-appliance-bootstrap.sh`, identity]);
    await execute('wsl.exe', [...wsl, ...common, 'build']);
  } else if (!existsSync(path.join(repo, 'scripts/dev-appliance.py'))) throw new Error('Missing development driver.');
  let stopping;
  const stop = () => stopping ??= (async () => {
    console.log('\n[launch-kautuka] Stopping the development stack; retaining data...');
    try { await execute('wsl.exe', [...wsl, ...common, 'stop']); } catch (error) { console.error(error.message); process.exitCode = 1; }
  })();
  process.once('SIGINT', stop);
  process.once('SIGTERM', stop);
  try {
    await execute('wsl.exe', [...wsl, ...common, 'run']);
  } finally {
    await stop();
    process.removeListener('SIGINT', stop);
    process.removeListener('SIGTERM', stop);
  }
}
