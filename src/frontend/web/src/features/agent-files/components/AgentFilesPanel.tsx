import axios from 'axios';
import { Alert } from '@/components/ui/alert';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { apiError } from '@/features/settings';
import { fetchConversationAssetBlob, type ConversationAssetPayload } from '@/features/chat';
import { cn } from '@/lib/utils';
import { ChevronRight, Download, FileText, Folder, FolderPlus, HardDriveUpload, Home, LoaderCircle, RefreshCw, Trash2, X } from 'lucide-react';
import React, { startTransition, useEffect, useRef, useState } from 'react';
import { toast } from 'sonner';
import { downloadAgentFile } from '../api/agentFiles';
import { useAgentFiles, useCreateAgentFolder, useDeleteAgentFileEntry, useUploadAgentFile } from '../api/queries';
import type { AgentFileEntry } from '../schemas/agentFileSchema';

export interface AgentFilesPanelAgent {
  id: string;
  name: string;
  sandboxEnabled: boolean;
}

interface AgentFilesPanelProps {
  agent: AgentFilesPanelAgent | null;
  conversationAssets?: ConversationAssetPayload[];
  onClose?: () => void;
  onSelectFile?: (entry: AgentFileEntry) => void;
  onFileUploaded?: (path: string) => void;
  selectLabel?: string;
  selectedPaths?: string[];
  className?: string;
}

const entryTimestampFormatter = new Intl.DateTimeFormat(undefined, {
  day: '2-digit',
  month: 'short',
  hour: 'numeric',
  minute: '2-digit',
});

const formatEntryTimestamp = (value: string) => entryTimestampFormatter.format(new Date(value));
const formatEntrySize = (value: number | null | undefined) => {
  if (typeof value !== 'number' || !Number.isFinite(value) || value < 0) {
    return '0 B';
  }

  if (value < 1024) {
    return `${value} B`;
  }

  if (value < 1024 * 1024) {
    return `${(value / 1024).toFixed(1)} KB`;
  }

  return `${(value / (1024 * 1024)).toFixed(1)} MB`;
};

const formatEntryCount = (value: number | null | undefined) => {
  const count = value ?? 0;
  return `${count} ${count === 1 ? 'file' : 'files'}`;
};

const isConflictError = (error: unknown) => axios.isAxiosError(error) && error.response?.status === 409;

const triggerBrowserDownload = (blob: Blob, fileName: string) => {
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = fileName;
  link.rel = 'noreferrer';
  document.body.append(link);
  link.click();
  link.remove();
  URL.revokeObjectURL(url);
};

export const AgentFilesPanel: React.FC<AgentFilesPanelProps> = ({
  agent,
  conversationAssets = [],
  onClose,
  onSelectFile,
  onFileUploaded,
  selectLabel = 'Use this file',
  selectedPaths = [],
  className,
}) => {
  const [currentPath, setCurrentPath] = useState('');
  const [folderName, setFolderName] = useState('');
  const [isDragActive, setIsDragActive] = useState(false);
  const fileInputRef = useRef<HTMLInputElement | null>(null);
  const dragDepthRef = useRef(0);

  useEffect(() => {
    setCurrentPath('');
    setFolderName('');
  }, [agent?.id]);

  const query = useAgentFiles(agent?.id ?? null, currentPath);
  const uploadMutation = useUploadAgentFile(agent?.id ?? null, currentPath);
  const createFolderMutation = useCreateAgentFolder(agent?.id ?? null, currentPath);
  const deleteMutation = useDeleteAgentFileEntry(agent?.id ?? null, currentPath);

  const breadcrumbs = currentPath.split('/').filter(Boolean);
  const readyResponse = query.data?.status === 'ready' ? query.data.response : null;
  const disabledResponse = query.data?.status === 'disabled' ? query.data.response : null;
  const entries = [...(readyResponse?.entries ?? [])].sort((left, right) => {
    if (left.entryType !== right.entryType) {
      return left.entryType === 'directory' ? -1 : 1;
    }

    return left.name.localeCompare(right.name);
  });
  const folderEntries = entries.filter((entry) => entry.entryType === 'directory');
  const fileEntries = entries.filter((entry) => entry.entryType === 'file');
  const actionsDisabled = !agent || Boolean(disabledResponse) || uploadMutation.isPending || createFolderMutation.isPending || deleteMutation.isPending;

  const navigateTo = (path: string) => {
    startTransition(() => {
      setCurrentPath(path);
    });
  };

  const navigateUp = () => {
    if (!currentPath) {
      return;
    }

    const segments = currentPath.split('/').filter(Boolean);
    navigateTo(segments.slice(0, -1).join('/'));
  };

  const handlePickFiles = () => {
    fileInputRef.current?.click();
  };

  const uploadSelectedFile = (selectedFile: File) => {
    if (!agent) {
      return;
    }

    void (async () => {
      try
      {
        const response = await uploadMutation.mutateAsync({ file: selectedFile });
        toast.success(`Uploaded ${response.fileName}.`);
        onFileUploaded?.(
          [response.path, response.fileName].filter(Boolean).join('/').replace(/\\/g, '/').replace(/^\/+/, '')
        );
      }
      catch (error)
      {
        if (isConflictError(error)
          && globalThis.confirm(`Replace the existing file "${selectedFile.name}" in this folder?`))
        {
          try
          {
            const response = await uploadMutation.mutateAsync({ file: selectedFile, overwrite: true });
            toast.success(`Replaced ${response.fileName}.`);
            return;
          }
          catch (overwriteError)
          {
            toast.error(apiError(overwriteError));
            return;
          }
        }

        toast.error(apiError(error));
      }
      finally
      {
        setIsDragActive(false);
        dragDepthRef.current = 0;
        if (fileInputRef.current) {
          fileInputRef.current.value = '';
        }
      }
    })();
  };

  const handleFileSelection: React.ChangeEventHandler<HTMLInputElement> = (event) => {
    const files = event.target.files;
    if (!files || files.length === 0) {
      return;
    }

    const selectedFile = files[0];
    uploadSelectedFile(selectedFile);
  };

  const handleDragEnter: React.DragEventHandler<HTMLDivElement> = (event) => {
    if (actionsDisabled || !event.dataTransfer?.types.includes('Files')) {
      return;
    }

    event.preventDefault();
    dragDepthRef.current += 1;
    setIsDragActive(true);
  };

  const handleDragOver: React.DragEventHandler<HTMLDivElement> = (event) => {
    if (actionsDisabled || !event.dataTransfer?.types.includes('Files')) {
      return;
    }

    event.preventDefault();
    event.dataTransfer.dropEffect = 'copy';
  };

  const handleDragLeave: React.DragEventHandler<HTMLDivElement> = (event) => {
    if (actionsDisabled || !event.dataTransfer?.types.includes('Files')) {
      return;
    }

    event.preventDefault();
    dragDepthRef.current = Math.max(0, dragDepthRef.current - 1);
    if (dragDepthRef.current === 0) {
      setIsDragActive(false);
    }
  };

  const handleDrop: React.DragEventHandler<HTMLDivElement> = (event) => {
    if (actionsDisabled || !event.dataTransfer?.files.length) {
      return;
    }

    event.preventDefault();
    const droppedFile = event.dataTransfer.files[0];
    uploadSelectedFile(droppedFile);
  };

  const handleCreateFolder = () => {
    const nextFolderName = folderName.trim();
    if (!nextFolderName) {
      toast.error('Folder name is required.');
      return;
    }

    void createFolderMutation
      .mutateAsync({ name: nextFolderName })
      .then((response) => {
        setFolderName('');
        toast.success(`Created ${response.name}.`);
      })
      .catch((error) => {
        toast.error(apiError(error));
      });
  };

  const handleDownload = async (entry: AgentFileEntry) => {
    if (!agent) {
      return;
    }

    try {
      const downloaded = await downloadAgentFile(agent.id, entry.path);
      triggerBrowserDownload(downloaded.blob, downloaded.fileName);
    } catch (error) {
      toast.error(apiError(error));
    }
  };

  const handleConversationAssetDownload = async (asset: ConversationAssetPayload) => {
    try {
      const blob = await fetchConversationAssetBlob(asset.downloadUrl);
      triggerBrowserDownload(blob, asset.fileName);
    } catch (error) {
      toast.error(apiError(error));
    }
  };

  const handleDelete = (entry: AgentFileEntry) => {
    if (!agent) {
      return;
    }

    const label = entry.entryType === 'directory' ? 'folder' : 'file';
    if (!globalThis.confirm(`Delete this ${label}: "${entry.name}"?`)) {
      return;
    }

    void deleteMutation.mutateAsync({ entryPath: entry.path })
      .then(() => {
        toast.success(`Deleted ${entry.name}.`);
      })
      .catch((error) => {
        toast.error(apiError(error));
      });
  };

  return (
    <section className={cn(
      'reveal-up flex max-h-full flex-col overflow-hidden rounded-2xl border border-border bg-card shadow-[0_8px_24px_-12px_rgba(16,36,72,0.18)]',
      className
    )}>
      <div className="flex flex-wrap items-center justify-between gap-3 border-b border-border bg-muted px-5 py-4 dark:border-border/80 dark:bg-muted/20">
        <div className="min-w-0">
          <h3 className="text-sm font-semibold text-foreground">Files</h3>
          <p className="section-label mt-0.5">
            Explorer
          </p>
        </div>

        <div className="flex flex-wrap items-center gap-2">
          <Button
            type="button"
            variant="outline"
            size="sm"
            onClick={() => {
              void query.refetch();
            }}
            disabled={!agent || query.isFetching}
          >
            {query.isFetching ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <RefreshCw className="h-4 w-4" />}
            Refresh
          </Button>
          <Button type="button" variant="outline" size="sm" onClick={handlePickFiles} disabled={actionsDisabled}>
            <HardDriveUpload className="h-4 w-4" />
            Upload
          </Button>
          {onClose && (
            <Button type="button" variant="ghost" size="icon" onClick={onClose} aria-label="Close files">
              <X className="h-4 w-4" />
            </Button>
          )}
        </div>
      </div>

      <input
        ref={fileInputRef}
        type="file"
        className="hidden"
        onChange={handleFileSelection}
        disabled={actionsDisabled}
      />

      <div className="workspace-scroll flex-1 overflow-y-auto p-5">
        {!agent && (
          <div className="rounded-2xl border border-dashed border-border/80 bg-muted/30 px-4 py-8 text-sm text-muted-foreground">
            Select an agent to browse its workspace files.
          </div>
        )}

        {agent && (
          <div className="space-y-4">
            <div className="overflow-hidden rounded-2xl border border-border/70 bg-background/80">
              <div className="flex items-center justify-between gap-3 border-b border-border/70 bg-muted px-4 py-3 dark:bg-muted/20">
                <div>
                  <div className="text-sm font-semibold text-foreground">Conversation attachments</div>
                  <div className="text-xs text-muted-foreground">Files attached to messages in this conversation</div>
                </div>
                <Badge variant="muted">
                  {conversationAssets.length} {conversationAssets.length === 1 ? 'item' : 'items'}
                </Badge>
              </div>
              {conversationAssets.length === 0 ? (
                <div className="px-4 py-5 text-sm text-muted-foreground">No attachments in this conversation.</div>
              ) : (
                <div className="divide-y divide-border/60">
                  {conversationAssets.map((asset) => (
                    <div key={asset.id} className="flex items-center gap-3 px-4 py-3 text-sm">
                      <FileText className="h-4 w-4 shrink-0 text-muted-foreground" />
                      <div className="min-w-0 flex-1">
                        <div className="truncate font-medium text-foreground">{asset.fileName}</div>
                        <div className="text-xs text-muted-foreground">
                          {formatEntrySize(asset.sizeBytes)} · {formatEntryTimestamp(asset.createdAt)}
                        </div>
                      </div>
                      <Button
                        type="button"
                        variant="ghost"
                        size="icon"
                        aria-label={`Download ${asset.fileName}`}
                        onClick={() => void handleConversationAssetDownload(asset)}
                      >
                        <Download className="h-4 w-4" />
                      </Button>
                    </div>
                  ))}
                </div>
              )}
            </div>

            <div className="rounded-2xl border border-border/70 bg-background/90 p-3 shadow-sm">
              <div className="flex flex-wrap items-center justify-between gap-3 rounded-xl border border-border/70 bg-muted/20 px-2 py-2">
                <div className="flex flex-wrap items-center gap-2">
                  <Button type="button" variant="ghost" size="sm" onClick={() => navigateTo('')} disabled={query.isPending}>
                    <Home className="h-4 w-4" />
                    {agent.name}
                  </Button>
                  {breadcrumbs.map((segment, index) => {
                    const breadcrumbPath = breadcrumbs.slice(0, index + 1).join('/');
                    return (
                      <React.Fragment key={breadcrumbPath}>
                        <ChevronRight className="h-4 w-4 text-muted-foreground" />
                        <Button
                          type="button"
                          variant="ghost"
                          size="sm"
                          onClick={() => navigateTo(breadcrumbPath)}
                          disabled={query.isPending}
                        >
                          {segment}
                        </Button>
                      </React.Fragment>
                    );
                  })}
                  {currentPath && (
                    <Button type="button" variant="outline" size="sm" onClick={navigateUp} disabled={query.isPending}>
                      Up
                    </Button>
                  )}
                </div>
                {readyResponse && (
                  <div className="rounded-full border border-border/70 bg-background/80 px-3 py-1 text-xs text-muted-foreground">
                    <span className="font-medium text-foreground">{readyResponse.totalFileCount}</span> files
                    {' • '}
                    <span className="font-medium text-foreground">{formatEntrySize(readyResponse.totalSizeBytes)}</span>
                  </div>
                )}
              </div>
            </div>

            <div className="grid gap-4 lg:grid-cols-[14rem_minmax(0,1fr)]">
              <aside className="overflow-hidden rounded-2xl border border-border/70 bg-muted dark:bg-muted/10">
                <div className="border-b border-border/70 px-4 py-3">
                  <div className="flex items-center justify-between gap-2">
                    <div className="section-label">
                      Folders
                    </div>
                    <Button
                      type="button"
                      variant="ghost"
                      size="icon"
                      className="h-8 w-8 rounded-full"
                      onClick={handleCreateFolder}
                      disabled={actionsDisabled}
                      aria-label="Create folder"
                    >
                      {createFolderMutation.isPending ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <FolderPlus className="h-4 w-4" />}
                    </Button>
                  </div>
                  <div className="mt-3 flex items-center gap-2">
                    <Input
                      value={folderName}
                      onChange={(event) => setFolderName(event.target.value)}
                      placeholder="New folder"
                      disabled={actionsDisabled}
                      className="h-9 bg-background/80"
                    />
                  </div>
                </div>
                <div className="space-y-1 p-2">
                  <button
                    type="button"
                    onClick={() => navigateTo('')}
                    className={cn(
                      'flex w-full items-center gap-2 rounded-xl px-3 py-2 text-left text-sm transition-colors',
                      currentPath.length === 0 ? 'bg-primary/10 text-primary' : 'hover:bg-muted'
                    )}
                  >
                    <Home className="h-4 w-4 shrink-0" />
                    <span className="truncate">Workspace Root</span>
                    {readyResponse && (
                      <span className="ml-auto shrink-0 rounded-full bg-background/80 px-2 py-0.5 text-[10px] font-medium text-muted-foreground">
                        {formatEntryCount(readyResponse.totalFileCount)}
                      </span>
                    )}
                  </button>
                  {folderEntries.map((entry) => (
                    <button
                      key={entry.path}
                      type="button"
                      onClick={() => navigateTo(entry.path)}
                      className="flex w-full items-center gap-2 rounded-xl px-3 py-2 text-left text-sm text-foreground transition-colors hover:bg-muted"
                    >
                      <Folder className="h-4 w-4 shrink-0 text-primary" />
                      <span className="truncate">{entry.name}</span>
                      <span className="ml-auto shrink-0 rounded-full bg-background/80 px-2 py-0.5 text-[10px] font-medium text-muted-foreground">
                        {formatEntryCount(entry.fileCount)}
                      </span>
                    </button>
                  ))}
                  {folderEntries.length === 0 && (
                    <div className="px-3 py-2 text-sm text-muted-foreground">No folders here.</div>
                  )}
                </div>
              </aside>

              <div className="space-y-4">
                <div className="flex items-center justify-between gap-3 rounded-2xl border border-border/70 bg-background/70 px-4 py-3">
                  <div>
                    <div className="text-sm font-semibold text-foreground">{currentPath.length > 0 ? currentPath : 'Workspace root'}</div>
                    <div className="text-xs text-muted-foreground">Current location</div>
                  </div>
                  <Badge variant="muted">
                    {entries.length} {entries.length === 1 ? 'item' : 'items'}
                  </Badge>
                </div>

                {disabledResponse && (
                  <Alert variant="attention">
                    <div className="font-semibold">Files unavailable</div>
                    <p className="mt-1">{disabledResponse.message}</p>
                  </Alert>
                )}

                {query.isPending && (
                  <div className="flex items-center gap-2 rounded-2xl border border-border/70 bg-muted/20 px-4 py-6 text-sm text-muted-foreground">
                    <LoaderCircle className="h-4 w-4 animate-spin" />
                    Loading files...
                  </div>
                )}

                {query.error && !disabledResponse && (
                  <Alert variant="destructive">
                    {apiError(query.error)}
                  </Alert>
                )}

                {!query.isPending && !disabledResponse && !query.error && (
                  <div
                    className={cn(
                      'overflow-hidden rounded-2xl border border-border/70 bg-background/80 transition-colors',
                      isDragActive && 'border-primary/60 bg-primary/5 ring-2 ring-primary/20'
                    )}
                    onDragEnter={handleDragEnter}
                    onDragOver={handleDragOver}
                    onDragLeave={handleDragLeave}
                    onDrop={handleDrop}
                  >
                    <div className="section-label grid grid-cols-[minmax(0,1.8fr)_120px_96px_120px] gap-3 border-b border-border/70 bg-muted px-4 py-3 dark:bg-muted/20">
                      <div>Name</div>
                      <div>Modified</div>
                      <div className="text-right">Size</div>
                      <div className="text-right">Action</div>
                    </div>

                    {isDragActive && (
                      <div className="border-b border-dashed border-primary/30 bg-primary/10 px-4 py-3 text-sm font-medium text-primary">
                        Drop file to upload here
                      </div>
                    )}

                    {entries.length === 0 ? (
                      <div className="px-4 py-10 text-sm text-muted-foreground">This folder is empty.</div>
                    ) : (
                      <div className="divide-y divide-border/60">
                        {entries.map((entry) => {
                          const isDirectory = entry.entryType === 'directory';

                          return (
            <div key={entry.path} className="grid grid-cols-[minmax(0,1.8fr)_120px_96px_156px] gap-3 px-4 py-3 text-sm transition-colors hover:bg-muted/20">
                              <div className="min-w-0">
                                <button
                                  type="button"
                                  className={cn(
                                    'flex w-full items-center gap-3 rounded-xl px-2 py-2 text-left transition-colors',
                                    isDirectory ? 'hover:bg-muted/60' : 'cursor-default'
                                  )}
                                  onClick={() => {
                                    if (isDirectory) {
                                      navigateTo(entry.path);
                                    }
                                  }}
                                  disabled={!isDirectory}
                                >
                                  {isDirectory ? (
                                    <Folder className="h-4 w-4 shrink-0 text-primary" />
                                  ) : (
                                    <FileText className="h-4 w-4 shrink-0 text-muted-foreground" />
                                  )}
                                  <div className="min-w-0 flex items-center gap-2">
                                    <div className="truncate font-medium text-foreground">{entry.name}</div>
                                    <Badge variant={isDirectory ? 'info' : 'muted'} className="shrink-0">
                                      {isDirectory ? 'Folder' : 'File'}
                                    </Badge>
                                  </div>
                                </button>
                              </div>
                              <div className="flex items-center text-xs text-muted-foreground">
                                {formatEntryTimestamp(entry.modifiedAt)}
                              </div>
                              <div className="flex items-center justify-end text-right text-xs text-muted-foreground">
                                {isDirectory ? '--' : formatEntrySize(entry.sizeBytes)}
                              </div>
                              <div className="flex items-center justify-end gap-1">
                                {!isDirectory && onSelectFile && (
                                  <Button
                                    type="button"
                                    variant="outline"
                                    size="sm"
                                    className="h-8 whitespace-nowrap px-2 text-xs"
                                    onClick={() => onSelectFile(entry)}
                                    aria-label={selectedPaths.includes(entry.path.replace(/\\/g, '/').replace(/^\/+/, ''))
                                      ? `Added ${entry.name}`
                                      : `${selectLabel}: ${entry.name}`}
                                    disabled={deleteMutation.isPending || selectedPaths.includes(entry.path.replace(/\\/g, '/').replace(/^\/+/, ''))}
                                  >
                                    {selectedPaths.includes(entry.path.replace(/\\/g, '/').replace(/^\/+/, ''))
                                      ? 'Added'
                                      : selectLabel}
                                  </Button>
                                )}
                                {!isDirectory && (
                                  <Button
                                    type="button"
                                    variant="ghost"
                                    size="icon"
                                    className="h-8 w-8 rounded-full text-muted-foreground hover:text-foreground"
                                    onClick={() => void handleDownload(entry)}
                                    aria-label={`Download ${entry.name}`}
                                    disabled={deleteMutation.isPending}
                                  >
                                    <Download className="h-4 w-4" />
                                  </Button>
                                )}
                                <Button
                                  type="button"
                                  variant="ghost"
                                  size="icon"
                                  className="h-8 w-8 rounded-full text-muted-foreground hover:text-destructive"
                                  onClick={() => handleDelete(entry)}
                                  aria-label={`Delete ${entry.name}`}
                                  disabled={deleteMutation.isPending}
                                >
                                  <Trash2 className="h-4 w-4" />
                                </Button>
                              </div>
                            </div>
                          );
                        })}
                      </div>
                    )}
                  </div>
                )}

                {!query.isPending && !disabledResponse && !query.error && fileEntries.length > 0 && (
                  <div className="rounded-2xl border border-border/70 bg-muted px-4 py-3 text-xs text-muted-foreground dark:bg-muted/10">
                    Drag and drop files here to upload.
                  </div>
                )}
              </div>
            </div>
          </div>
        )}
      </div>
    </section>
  );
};
