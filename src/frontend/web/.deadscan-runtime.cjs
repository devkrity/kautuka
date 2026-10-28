const fs = require('node:fs');
const path = require('node:path');
const ts = require('typescript');

const root = path.resolve('src');
const exts = ['.ts', '.tsx', '.js', '.jsx'];

function walk(dir) {
  let out = [];
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    if (e.name === 'node_modules' || e.name === 'dist') continue;
    const p = path.join(dir, e.name);
    if (e.isDirectory()) out = out.concat(walk(p));
    else out.push(p);
  }
  return out;
}

const files = walk(root).filter((f) => exts.includes(path.extname(f)) && !f.endsWith('.d.ts'));
const byNorm = new Map(files.map((f) => [path.normalize(f), f]));

const metadataByFile = new Map();
const reexportedTargets = new Set();

function isExported(node) {
  return (
    Array.isArray(node.modifiers) &&
    node.modifiers.some((modifier) => modifier.kind === ts.SyntaxKind.ExportKeyword)
  );
}

function isDefaultExport(node) {
  return (
    Array.isArray(node.modifiers) &&
    node.modifiers.some((modifier) => modifier.kind === ts.SyntaxKind.DefaultKeyword)
  );
}

function addIdentifierExports(target, declarationName) {
  if (ts.isIdentifier(declarationName)) {
    target.add(declarationName.text);
  }
}

function isTestFile(filePath) {
  return filePath.includes(`${path.sep}test${path.sep}`) || /\.test\.[^.]+$/.test(filePath);
}

function toReportPath(filePath) {
  return path.relative(process.cwd(), filePath).replaceAll('\\', '/');
}

function resolveSpec(fromFile, spec) {
  let base = null;
  if (spec.startsWith('@/')) base = path.join(root, spec.slice(2));
  else if (spec.startsWith('./') || spec.startsWith('../'))
    base = path.resolve(path.dirname(fromFile), spec);
  else return null;

  const candidates = [];
  const baseExt = path.extname(base);
  if (exts.includes(baseExt)) {
    candidates.push(base);
  } else {
    for (const e of exts) candidates.push(base + e);
    for (const e of exts) candidates.push(path.join(base, 'index' + e));
  }

  for (const c of candidates) {
    const n = path.normalize(c);
    if (byNorm.has(n)) return n;
  }
  return null;
}

const importRegexes = [
  /import\s+[^'"`]*?from\s*['"]([^'"]+)['"]/g,
  /export\s+[^'"`]*?from\s*['"]([^'"]+)['"]/g,
  /import\s*\(\s*['"]([^'"]+)['"]\s*\)/g,
];

const edges = new Map();
for (const f of files) {
  const txt = fs.readFileSync(f, 'utf8');
  const source = ts.createSourceFile(
    f,
    txt,
    ts.ScriptTarget.Latest,
    true,
    f.endsWith('.tsx') ? ts.ScriptKind.TSX : ts.ScriptKind.TS
  );
  const deps = new Set();
  const directExports = new Set();
  const importedNamesByTarget = new Map();
  const reexportTargets = new Set();
  let hasReexports = false;
  let hasNamespaceImports = false;

  for (const re of importRegexes) {
    re.lastIndex = 0;
    let m;
    while ((m = re.exec(txt)) !== null) {
      const resolved = resolveSpec(f, m[1]);
      if (resolved) deps.add(resolved);
    }
  }

  for (const statement of source.statements) {
    if (ts.isImportDeclaration(statement) && ts.isStringLiteral(statement.moduleSpecifier)) {
      const resolved = resolveSpec(f, statement.moduleSpecifier.text);
      if (resolved) {
        const importClause = statement.importClause;
        if (importClause) {
          const usedNames = importedNamesByTarget.get(resolved) ?? new Set();
          if (importClause.name) {
            usedNames.add('default');
          }

          if (importClause.namedBindings) {
            if (ts.isNamespaceImport(importClause.namedBindings)) {
              hasNamespaceImports = true;
              usedNames.add('*');
            } else {
              for (const element of importClause.namedBindings.elements) {
                usedNames.add(element.propertyName ? element.propertyName.text : element.name.text);
              }
            }
          }

          importedNamesByTarget.set(resolved, usedNames);
        }
      }
      continue;
    }

    if (ts.isExportDeclaration(statement)) {
      if (statement.moduleSpecifier && ts.isStringLiteral(statement.moduleSpecifier)) {
        hasReexports = true;
        const resolved = resolveSpec(f, statement.moduleSpecifier.text);
        if (resolved) {
          reexportTargets.add(resolved);
          reexportedTargets.add(resolved);
        }
      }

      if (statement.exportClause && ts.isNamedExports(statement.exportClause)) {
        if (!statement.moduleSpecifier) {
          for (const element of statement.exportClause.elements) {
            directExports.add(element.name.text);
          }
        }
      }
      continue;
    }

    if (ts.isExportAssignment(statement)) {
      if (!statement.isExportEquals) {
        directExports.add('default');
      }
      continue;
    }

    if (!isExported(statement)) {
      continue;
    }

    if (
      ts.isFunctionDeclaration(statement) ||
      ts.isClassDeclaration(statement) ||
      ts.isInterfaceDeclaration(statement) ||
      ts.isTypeAliasDeclaration(statement) ||
      ts.isEnumDeclaration(statement)
    ) {
      if (!isDefaultExport(statement) && statement.name) {
        directExports.add(statement.name.text);
      }
      continue;
    }

    if (ts.isVariableStatement(statement)) {
      for (const declaration of statement.declarationList.declarations) {
        addIdentifierExports(directExports, declaration.name);
      }
    }
  }

  edges.set(path.normalize(f), deps);
  metadataByFile.set(path.normalize(f), {
    directExports,
    importedNamesByTarget,
    hasNamespaceImports,
    hasReexports,
    reexportTargets,
  });
}

const entry = path.normalize(path.resolve('src/main.tsx'));
const visited = new Set();
const stack = [entry];

while (stack.length) {
  const f = stack.pop();
  if (!f || visited.has(f)) continue;
  visited.add(f);
  const deps = edges.get(f);
  if (!deps) continue;
  for (const d of deps) {
    if (!visited.has(d)) stack.push(d);
  }
}

const unreachable = files
  .map((f) => path.normalize(f))
  .filter((f) => !visited.has(f))
  .sort();

const unreachableReport = unreachable.map(toReportPath);
const unreachableAppReport = unreachable.filter((f) => !isTestFile(f)).map(toReportPath);
const unreachableTestReport = unreachable.filter(isTestFile).map(toReportPath);

const importedNamesByFile = new Map();
const filesToSkipForUnusedExports = new Set();
for (const [filePath, metadata] of metadataByFile.entries()) {
  if (!visited.has(filePath)) {
    continue;
  }

  if (metadata.hasNamespaceImports) {
    filesToSkipForUnusedExports.add(filePath);
  }

  for (const [targetPath, names] of metadata.importedNamesByTarget.entries()) {
    const targetNames = importedNamesByFile.get(targetPath) ?? new Set();
    for (const name of names) {
      targetNames.add(name);
      if (name === '*') {
        filesToSkipForUnusedExports.add(targetPath);
      }
    }
    importedNamesByFile.set(targetPath, targetNames);
  }
}

const unusedExportsReport = [];
for (const filePath of files.map((f) => path.normalize(f)).sort()) {
  if (!visited.has(filePath) || isTestFile(filePath)) {
    continue;
  }

  const metadata = metadataByFile.get(filePath);
  if (!metadata) {
    continue;
  }

  if (metadata.hasReexports || filesToSkipForUnusedExports.has(filePath)) {
    continue;
  }

  if (reexportedTargets.has(filePath)) {
    continue;
  }

  const baseName = path.basename(filePath);
  if (baseName.startsWith('index.')) {
    continue;
  }

  if (toReportPath(filePath).startsWith('src/pages/')) {
    continue;
  }

  if (toReportPath(filePath).startsWith('src/lib/api/generated/')) {
    continue;
  }

  const importedNames = importedNamesByFile.get(filePath) ?? new Set();
  for (const exportName of Array.from(metadata.directExports).sort()) {
    if (exportName === 'default') {
      continue;
    }

    if (!importedNames.has(exportName)) {
      unusedExportsReport.push(`${toReportPath(filePath)} :: ${exportName}`);
    }
  }
}

fs.writeFileSync('dead-files-runtime.txt', unreachableReport.join('\n'));
fs.writeFileSync('dead-files-app-runtime.txt', unreachableAppReport.join('\n'));
fs.writeFileSync('dead-files-tests-runtime.txt', unreachableTestReport.join('\n'));
fs.writeFileSync('dead-exports-runtime.txt', unusedExportsReport.join('\n'));

console.log('TOTAL_FILES', files.length);
console.log('REACHABLE', visited.size);
console.log('UNREACHABLE', unreachable.length);
console.log('REPORT', path.resolve('dead-files-runtime.txt'));
console.log('UNREACHABLE_APP', unreachableAppReport.length);
console.log('UNREACHABLE_TEST', unreachableTestReport.length);
console.log('UNUSED_EXPORTS', unusedExportsReport.length);
console.log('APP_REPORT', path.resolve('dead-files-app-runtime.txt'));
console.log('TEST_REPORT', path.resolve('dead-files-tests-runtime.txt'));
console.log('EXPORT_REPORT', path.resolve('dead-exports-runtime.txt'));
