const isDocumentationPath = (file) =>
  file.toLowerCase().endsWith('.md') || file === 'LICENSE' || file === 'NOTICE';

export function classifyQualityScope(files) {
  if (!Array.isArray(files) || files.length === 0) return 'full';

  const changedFiles = files.filter(Boolean);
  if (changedFiles.length === 0) return 'full';
  if (changedFiles.every(isDocumentationPath)) return 'docs';

  if (changedFiles.every(
    (file) =>
      isDocumentationPath(file) ||
      (file.startsWith('src/frontend/') && file !== 'src/frontend/web/public/assets/icons/kautuka.ico'),
  )) {
    return 'frontend';
  }

  return 'full';
}
