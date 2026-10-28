import { listAgentFiles } from '@/features/agent-files';
import { extractWorkspaceFileReferencePaths } from './workspaceFileReferences';

export interface ResolvedWorkspaceFilesResult {
  workspaceFiles: string[];
  validationError: string | null;
}

export async function resolveWorkspaceFilesForAgent(
  agentId: string,
  text: string
): Promise<ResolvedWorkspaceFilesResult> {
  const workspaceFileCandidates = extractWorkspaceFileReferencePaths(text);
  if (workspaceFileCandidates.length === 0) {
    return { workspaceFiles: [], validationError: null };
  }

  const directoryCache = new Map<string, Awaited<ReturnType<typeof listAgentFiles>>>();
  const resolvedWorkspaceFiles: string[] = [];

  const getDirectoryResult = async (directoryPath: string) => {
    const cached = directoryCache.get(directoryPath);
    if (cached) {
      return cached;
    }

    const result = await listAgentFiles(agentId, directoryPath);
    directoryCache.set(directoryPath, result);
    return result;
  };

  const createCandidateVariants = (candidatePath: string): string[] => {
    const variants: string[] = [];
    let current = candidatePath.trim().replace(/[),.;:!?]+$/g, '').trimEnd();

    while (current.length > 0) {
      variants.push(current);

      const lastSpaceIndex = current.lastIndexOf(' ');
      if (lastSpaceIndex < 0) {
        break;
      }

      current = current.slice(0, lastSpaceIndex).replace(/[),.;:!?]+$/g, '').trimEnd();
    }

    return variants;
  };

  for (const workspaceFileCandidate of workspaceFileCandidates) {
    const candidateVariants = createCandidateVariants(workspaceFileCandidate);
    let resolvedWorkspaceFile: string | null = null;

    for (const candidateVariant of candidateVariants) {
      const segments = candidateVariant.split('/').filter((segment) => segment.length > 0);
      if (segments.length === 0) {
        continue;
      }

      const directoryPath = segments.slice(0, -1).join('/');
      const entryName = segments[segments.length - 1] ?? '';
      const directoryResult = await getDirectoryResult(directoryPath);
      if (directoryResult.status === 'disabled') {
        return {
          workspaceFiles: [],
          validationError: directoryResult.response.message,
        };
      }

      const matchingEntry = directoryResult.response.entries.find(
        (entry) =>
          entry.entryType === 'file' && entry.name === entryName && entry.path === candidateVariant
      );

      if (matchingEntry) {
        resolvedWorkspaceFile = candidateVariant;
        break;
      }
    }

    if (!resolvedWorkspaceFile) {
      return {
        workspaceFiles: [],
        validationError: `Referenced workspace file not found: #${workspaceFileCandidate.trim()}`,
      };
    }

    if (!resolvedWorkspaceFiles.includes(resolvedWorkspaceFile)) {
      resolvedWorkspaceFiles.push(resolvedWorkspaceFile);
    }
  }

  return { workspaceFiles: resolvedWorkspaceFiles, validationError: null };
}
