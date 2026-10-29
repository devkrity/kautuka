import { relative, resolve } from 'node:path';

export function ownedWorkspacePath(workspaceRoot: string, workspaceId: string): string {
  if (!/^[a-zA-Z0-9-]+$/.test(workspaceId)) throw new Error('Acceptance workspace id must be a safe path segment.');
  const root = resolve(workspaceRoot);
  const workspace = resolve(root, workspaceId);
  if (relative(root, workspace) !== workspaceId) throw new Error('Acceptance workspace must be an exact child of its configured root.');
  return workspace;
}
