export interface WorkspaceFileReferenceQuery {
  start: number;
  end: number;
  rawPath: string;
  directoryPath: string;
  namePrefix: string;
}

const workspaceReferencePattern = /(^|[\s([\{\n])#([^#\r\n]+)/g;

export const normalizeWorkspaceFileReferencePath = (value: string): string =>
  value.replace(/\\/g, '/').replace(/^\/+/, '').trim();

export const extractWorkspaceFileReferencePaths = (text: string): string[] => {
  const references = new Set<string>();

  for (const match of text.matchAll(workspaceReferencePattern)) {
    const rawPath = normalizeWorkspaceFileReferencePath(match[2] ?? '');
    if (!rawPath) {
      continue;
    }

    references.add(rawPath);
  }

  return [...references];
};

export const getWorkspaceFileReferenceQuery = (
  text: string,
  caretPosition: number
): WorkspaceFileReferenceQuery | null => {
  const boundedCaret = Math.max(0, Math.min(caretPosition, text.length));
  const prefix = text.slice(0, boundedCaret);
  const match = /(^|[\s([\{\n])#([^#\r\n]*)$/.exec(prefix);

  if (!match) {
    return null;
  }

  if ((match[2] ?? '').endsWith(' ')) {
    return null;
  }

  const rawPath = normalizeWorkspaceFileReferencePath(match[2] ?? '');
  const hashOffset = match[0].lastIndexOf('#');
  const start = boundedCaret - (match[0].length - hashOffset);
  const lastSlashIndex = rawPath.lastIndexOf('/');

  return {
    start,
    end: boundedCaret,
    rawPath,
    directoryPath: lastSlashIndex >= 0 ? rawPath.slice(0, lastSlashIndex) : '',
    namePrefix: lastSlashIndex >= 0 ? rawPath.slice(lastSlashIndex + 1) : rawPath,
  };
};

export const applyWorkspaceFileReferenceCompletion = (
  text: string,
  query: WorkspaceFileReferenceQuery,
  selectedPath: string,
  entryType: 'file' | 'directory'
): { text: string; caretPosition: number } => {
  const normalizedPath = normalizeWorkspaceFileReferencePath(selectedPath);
  const completedReference =
    entryType === 'directory' ? `#${normalizedPath}/` : `#${normalizedPath} `;
  const nextText = `${text.slice(0, query.start)}${completedReference}${text.slice(query.end)}`;

  return {
    text: nextText,
    caretPosition: query.start + completedReference.length,
  };
};