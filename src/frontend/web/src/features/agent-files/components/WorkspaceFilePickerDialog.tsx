import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog';
import React from 'react';
import { AgentFilesPanel, type AgentFilesPanelAgent } from './AgentFilesPanel';

export interface WorkspaceFilePickerDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  agent: AgentFilesPanelAgent | null;
  selectedPaths: string[];
  onSelectPath: (path: string) => void;
}

export function normalizeWorkspaceFilePath(path: string): string {
  return path.replace(/\\/g, '/').replace(/^\/+/, '').replace(/\/{2,}/g, '/');
}

export const WorkspaceFilePickerDialog: React.FC<WorkspaceFilePickerDialogProps> = ({
  open,
  onOpenChange,
  agent,
  selectedPaths,
  onSelectPath,
}) => (
  <Dialog open={open} onOpenChange={onOpenChange}>
    <DialogContent className="flex h-[85vh] max-w-6xl flex-col gap-4 overflow-hidden">
      <DialogHeader>
        <DialogTitle>
          {agent ? `Choose files from ${agent.name}\u2019s workspace` : 'Choose workspace files'}
        </DialogTitle>
        <DialogDescription>
          Select files already stored in this workspace, or upload files here.
        </DialogDescription>
      </DialogHeader>
      <AgentFilesPanel
        agent={agent}
        selectLabel="Use this file"
        selectedPaths={selectedPaths.map(normalizeWorkspaceFilePath)}
        onSelectFile={(entry) => onSelectPath(normalizeWorkspaceFilePath(entry.path))}
        onFileUploaded={onSelectPath}
        className="min-h-0 flex-1"
      />
    </DialogContent>
  </Dialog>
);
