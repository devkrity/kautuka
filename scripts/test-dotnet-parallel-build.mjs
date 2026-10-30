import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const [quality, release, platformAssets, linuxBuilder] = await Promise.all([
  readFile(path.join(root, '.github/workflows/dotnet-quality.yml'), 'utf8'),
  readFile(path.join(root, '.github/workflows/release-pipeline.yml'), 'utf8'),
  readFile(path.join(root, '.github/workflows/release-build-platform-assets.yml'), 'utf8'),
  readFile(path.join(root, 'scripts/build-release-assets.sh'), 'utf8'),
]);

assert.match(quality, /dotnet build src\/Kautuka\.slnx --no-restore --maxcpucount/,
  'PR solution build must use MSBuild parallel project builds');
assert.match(release, /dotnet build src\/Kautuka\.slnx --no-restore --maxcpucount/,
  'release validation solution build must use MSBuild parallel project builds');
assert.match(quality, /dotnet publish src\/backend\/Kautuka\.CredentialsHost\/Kautuka\.CredentialsHost\.csproj[^\n]*--maxcpucount/,
  'published credentials boundary builds must enable parallel project references');
assert.match(quality, /dotnet publish src\/backend\/Kautuka\.Gateway\/Kautuka\.Gateway\.csproj[^\n]*--maxcpucount/,
  'Gateway Native AOT builds must enable parallel project references');
assert.match(platformAssets, /dotnet publish src\/installer\/Kautuka\.Installer\.Windows\/Kautuka\.Installer\.Windows\.csproj \\\r?\n\s+--maxcpucount/,
  'Windows installer publishes must enable parallel project references');
assert.match(linuxBuilder, /dotnet publish "\$project_path" \\\r?\n\s+--maxcpucount/,
  'Linux host publishes must enable parallel project references');
assert.match(linuxBuilder, /dotnet publish src\/backend\/connectors\/m365-mail\/kautuka-m365-mail\.csproj \\\r?\n\s+--maxcpucount/,
  'M365 connector publish must enable parallel project references');
assert.match(linuxBuilder, /dotnet publish src\/backend\/connectors\/google-mail\/kautuka-google-mail\.csproj \\\r?\n\s+--maxcpucount/,
  'Google Mail connector publish must enable parallel project references');

console.log('PR and release .NET builds enable parallel project builds.');
