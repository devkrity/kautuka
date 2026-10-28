import { Alert } from '@/components/ui/alert';
import React from 'react';
import { CalendarClock } from 'lucide-react';
import {
  normalizeWorkspaceFilePath,
  useAgentFiles,
  useSearchAgentFiles,
  WorkspaceFilePickerDialog,
  type AgentFilesPanelAgent,
} from '@/features/agent-files';
import {
  AutopilotScheduleConfigurationEditor,
  AutopilotActivityPanels,
  useAutopilotData,
  useAutopilotWorkflowTargets,
  WorkflowStartValuesFields,
} from '@/features/autopilot';
import {
  applyWorkspaceFileReferenceCompletion,
  getWorkspaceFileReferenceQuery,
} from '@/features/chat';
import { useAgents } from '@/features/settings';
import { PageIntro } from '@/components/layout/PageIntro';
import { PageState } from '@/components/layout/PageState';
import { Button } from '@/components/ui/button';
import { cn } from '@/lib/utils';
import { X } from 'lucide-react';

interface WorkspaceReferencePill {
  id: string;
  start: number;
  end: number;
  text: string;
  path: string;
  entryType: 'file' | 'directory';
}

// ─── main page ──────────────────────────────────────────────────────────────

export const AutopilotPage: React.FC = () => {
  const {
    schedules,
    runs,
    inbox,
    loading,
    error,
    draft,
    setDraft,
    editingScheduleId,
    setEditingScheduleId,
    setDraftFromSchedule,
    save,
    pauseSchedule,
    resumeSchedule,
    runNow,
    deleteSchedule,
    markInboxRead,
    saving,
    mutating,
  } = useAutopilotData();

  const { data: agents = [] } = useAgents();

  const [showScheduleForm, setShowScheduleForm] = React.useState(false);
  const [promptSelectionStart, setPromptSelectionStart] = React.useState(0);
  const [autocompleteIndex, setAutocompleteIndex] = React.useState(0);
  const [workspaceReferencePills, setWorkspaceReferencePills] = React.useState<
    WorkspaceReferencePill[]
  >([]);
  const [selectedWorkspaceReferencePillId, setSelectedWorkspaceReferencePillId] = React.useState<
    string | null
  >(null);
  const [workspaceFilePickerOpen, setWorkspaceFilePickerOpen] = React.useState(false);
  const { workflows, workflow, targetAgentId } = useAutopilotWorkflowTargets(
    draft,
    setDraft,
    setEditingScheduleId,
    setShowScheduleForm
  );
  const promptTargetId = targetAgentId;
  const targetOptions = agents;
  const promptTextareaRef = React.useRef<HTMLTextAreaElement | null>(null);
  const pendingCaretPositionRef = React.useRef<number | null>(null);
  const previousPromptRef = React.useRef(draft.schedulePromptContext);

  const workspaceFileQuery = React.useMemo(
    () => getWorkspaceFileReferenceQuery(draft.schedulePromptContext, promptSelectionStart),
    [draft.schedulePromptContext, promptSelectionStart]
  );
  const workspaceFileDirectoryQuery = useAgentFiles(
    promptTargetId && workspaceFileQuery ? promptTargetId : null,
    workspaceFileQuery?.directoryPath ?? ''
  );
  const workspaceFileSearchQuery = useSearchAgentFiles(
    promptTargetId && workspaceFileQuery && !workspaceFileQuery.directoryPath
      ? promptTargetId
      : null,
    workspaceFileQuery?.namePrefix ?? '',
    8
  );
  const useWorkspaceSearchSuggestions = Boolean(
    workspaceFileQuery &&
    !workspaceFileQuery.directoryPath &&
    workspaceFileQuery.namePrefix.length > 0
  );
  const workspaceFileSuggestions = React.useMemo(() => {
    if (!workspaceFileQuery) {
      return [];
    }

    if (useWorkspaceSearchSuggestions) {
      return (workspaceFileSearchQuery.data?.entries ?? []).slice(0, 8);
    }

    if (workspaceFileDirectoryQuery.data?.status !== 'ready') {
      return [];
    }

    const prefix = workspaceFileQuery.namePrefix.toLowerCase();
    return workspaceFileDirectoryQuery.data.response.entries
      .filter((entry) => entry.name.toLowerCase().startsWith(prefix))
      .sort((left, right) => {
        if (left.entryType !== right.entryType) {
          return left.entryType === 'directory' ? -1 : 1;
        }

        return left.name.localeCompare(right.name);
      })
      .slice(0, 8);
  }, [
    useWorkspaceSearchSuggestions,
    workspaceFileDirectoryQuery.data,
    workspaceFileQuery,
    workspaceFileSearchQuery.data,
  ]);
  const shouldShowWorkspaceFileAutocomplete = Boolean(
    promptTargetId && workspaceFileQuery && workspaceFileSuggestions.length > 0
  );
  const promptOverlaySegments = React.useMemo(() => {
    if (workspaceReferencePills.length === 0) {
      return [{ type: 'text' as const, key: 'text-0', value: draft.schedulePromptContext }];
    }

    const orderedPills = [...workspaceReferencePills].sort(
      (left, right) => left.start - right.start
    );
    const segments: Array<
      | { type: 'text'; key: string; value: string }
      | { type: 'pill'; key: string; value: WorkspaceReferencePill }
    > = [];
    let cursor = 0;

    for (const pill of orderedPills) {
      if (pill.start > cursor) {
        segments.push({
          type: 'text',
          key: `text-${cursor}`,
          value: draft.schedulePromptContext.slice(cursor, pill.start),
        });
      }

      segments.push({ type: 'pill', key: pill.id, value: pill });
      cursor = pill.end;
    }

    if (cursor < draft.schedulePromptContext.length) {
      segments.push({
        type: 'text',
        key: `text-${cursor}`,
        value: draft.schedulePromptContext.slice(cursor),
      });
    }

    return segments;
  }, [draft.schedulePromptContext, workspaceReferencePills]);

  const focusPromptComposer = React.useCallback(() => {
    const schedule =
      typeof requestAnimationFrame === 'function'
        ? requestAnimationFrame
        : (callback: FrameRequestCallback) => globalThis.setTimeout(() => callback(0), 0);

    schedule(() => {
      if (!promptTextareaRef.current) {
        return;
      }

      promptTextareaRef.current.focus();
      if (pendingCaretPositionRef.current !== null) {
        promptTextareaRef.current.setSelectionRange(
          pendingCaretPositionRef.current,
          pendingCaretPositionRef.current
        );
        setPromptSelectionStart(pendingCaretPositionRef.current);
        pendingCaretPositionRef.current = null;
      }
    });
  }, []);

  React.useEffect(() => {
    if (!promptTextareaRef.current || document.activeElement !== promptTextareaRef.current) {
      setPromptSelectionStart(draft.schedulePromptContext.length);
    }
  }, [draft.schedulePromptContext]);

  React.useEffect(() => {
    const previousPrompt = previousPromptRef.current;
    if (previousPrompt === draft.schedulePromptContext) {
      return;
    }

    setWorkspaceReferencePills((current) =>
      current.filter(
        (pill) => draft.schedulePromptContext.slice(pill.start, pill.end) === pill.text
      )
    );
    setSelectedWorkspaceReferencePillId((current) =>
      current && workspaceReferencePills.some((pill) => pill.id === current) ? current : null
    );
    previousPromptRef.current = draft.schedulePromptContext;
  }, [draft.schedulePromptContext, workspaceReferencePills]);

  React.useEffect(() => {
    if (!shouldShowWorkspaceFileAutocomplete) {
      setAutocompleteIndex(0);
      return;
    }

    setAutocompleteIndex((current) => Math.min(current, workspaceFileSuggestions.length - 1));
  }, [shouldShowWorkspaceFileAutocomplete, workspaceFileSuggestions.length]);

  const syncWorkspaceReferencePills = React.useCallback((nextPrompt: string) => {
    const previousPrompt = previousPromptRef.current;
    if (previousPrompt === nextPrompt) {
      return;
    }

    let prefixLength = 0;
    while (
      prefixLength < previousPrompt.length &&
      prefixLength < nextPrompt.length &&
      previousPrompt.charAt(prefixLength) === nextPrompt.charAt(prefixLength)
    ) {
      prefixLength++;
    }

    let previousSuffixLength = previousPrompt.length;
    let nextSuffixLength = nextPrompt.length;
    while (
      previousSuffixLength > prefixLength &&
      nextSuffixLength > prefixLength &&
      previousPrompt.charAt(previousSuffixLength - 1) === nextPrompt.charAt(nextSuffixLength - 1)
    ) {
      previousSuffixLength--;
      nextSuffixLength--;
    }

    const removedCount = previousSuffixLength - prefixLength;
    const insertedCount = nextSuffixLength - prefixLength;
    const delta = insertedCount - removedCount;

    setWorkspaceReferencePills((current) =>
      current
        .filter((pill) => pill.end <= prefixLength || pill.start >= previousSuffixLength)
        .map((pill) => {
          if (pill.end <= prefixLength) {
            return pill;
          }

          return {
            ...pill,
            start: pill.start + delta,
            end: pill.end + delta,
          };
        })
    );
    setSelectedWorkspaceReferencePillId(null);
    previousPromptRef.current = nextPrompt;
  }, []);

  const removeWorkspaceReferencePill = React.useCallback(
    (pill: WorkspaceReferencePill, replacementText = '', placeCaretAt = pill.start) => {
      const nextPrompt = `${draft.schedulePromptContext.slice(0, pill.start)}${replacementText}${draft.schedulePromptContext.slice(pill.end)}`;
      setWorkspaceReferencePills((current) =>
        current
          .filter((entry) => entry.id !== pill.id)
          .map((entry) => {
            if (entry.start >= pill.end) {
              const delta = replacementText.length - (pill.end - pill.start);
              return {
                ...entry,
                start: entry.start + delta,
                end: entry.end + delta,
              };
            }

            return entry;
          })
      );
      setSelectedWorkspaceReferencePillId(null);
      previousPromptRef.current = nextPrompt;
      pendingCaretPositionRef.current = placeCaretAt + replacementText.length;
      setDraft((previous) => ({
        ...previous,
        schedulePromptContext: nextPrompt,
      }));
      focusPromptComposer();
    },
    [draft.schedulePromptContext, focusPromptComposer, setDraft]
  );

  const findWorkspaceReferencePillAtSelection = React.useCallback(
    (selectionStart: number, selectionEnd: number, mode: 'backspace' | 'delete' | 'insert') => {
      if (selectionStart !== selectionEnd) {
        return null;
      }

      return (
        workspaceReferencePills.find((pill) => {
          if (mode === 'backspace') {
            return selectionStart > pill.start && selectionStart <= pill.end;
          }

          if (mode === 'delete') {
            return selectionStart >= pill.start && selectionStart < pill.end;
          }

          return selectionStart > pill.start && selectionStart < pill.end;
        }) ?? null
      );
    },
    [workspaceReferencePills]
  );

  const applyWorkspaceSuggestion = React.useCallback(
    (suggestion: { path: string; entryType: 'file' | 'directory' }) => {
      if (!workspaceFileQuery) {
        return;
      }

      const completion = applyWorkspaceFileReferenceCompletion(
        draft.schedulePromptContext,
        workspaceFileQuery,
        suggestion.path,
        suggestion.entryType
      );
      const pillText = completion.text.slice(
        workspaceFileQuery.start,
        workspaceFileQuery.start + `#${suggestion.path}`.length
      );
      setWorkspaceReferencePills((current) => [
        ...current.filter(
          (pill) => pill.start < workspaceFileQuery.start || pill.end > workspaceFileQuery.end
        ),
        {
          id: `${suggestion.path}:${workspaceFileQuery.start}`,
          start: workspaceFileQuery.start,
          end: workspaceFileQuery.start + pillText.length,
          text: pillText,
          path: suggestion.path,
          entryType: suggestion.entryType,
        },
      ]);
      setSelectedWorkspaceReferencePillId(null);
      previousPromptRef.current = completion.text;
      pendingCaretPositionRef.current = completion.caretPosition;
      setDraft((previous) => ({
        ...previous,
        schedulePromptContext: completion.text,
      }));
      focusPromptComposer();
    },
    [draft.schedulePromptContext, focusPromptComposer, setDraft, workspaceFileQuery]
  );

  const handlePromptKeyDown: React.KeyboardEventHandler<HTMLTextAreaElement> = (event) => {
    const selectionStart = event.currentTarget.selectionStart ?? 0;
    const selectionEnd = event.currentTarget.selectionEnd ?? selectionStart;
    const selectedPill = selectedWorkspaceReferencePillId
      ? (workspaceReferencePills.find((pill) => pill.id === selectedWorkspaceReferencePillId) ??
        null)
      : null;

    if (selectedPill && event.key === 'Backspace') {
      event.preventDefault();
      removeWorkspaceReferencePill(selectedPill);
      return;
    }

    if (selectedPill && event.key === 'Delete') {
      event.preventDefault();
      removeWorkspaceReferencePill(selectedPill);
      return;
    }

    if (
      selectedPill &&
      event.key.length === 1 &&
      !event.metaKey &&
      !event.ctrlKey &&
      !event.altKey
    ) {
      event.preventDefault();
      removeWorkspaceReferencePill(selectedPill, event.key, selectedPill.start);
      return;
    }

    if (event.key === 'Backspace') {
      const pill = findWorkspaceReferencePillAtSelection(selectionStart, selectionEnd, 'backspace');
      if (pill) {
        event.preventDefault();
        removeWorkspaceReferencePill(pill);
        return;
      }
    }

    if (event.key === 'Delete') {
      const pill = findWorkspaceReferencePillAtSelection(selectionStart, selectionEnd, 'delete');
      if (pill) {
        event.preventDefault();
        removeWorkspaceReferencePill(pill);
        return;
      }
    }

    if (event.key.length === 1 && !event.metaKey && !event.ctrlKey && !event.altKey) {
      const pill = findWorkspaceReferencePillAtSelection(selectionStart, selectionEnd, 'insert');
      if (pill) {
        event.preventDefault();
        removeWorkspaceReferencePill(pill, event.key, pill.start);
        return;
      }
    }

    if (!shouldShowWorkspaceFileAutocomplete) {
      return;
    }

    if (event.key === 'ArrowDown') {
      event.preventDefault();
      setAutocompleteIndex((current) =>
        current + 1 >= workspaceFileSuggestions.length ? 0 : current + 1
      );
      return;
    }

    if (event.key === 'ArrowUp') {
      event.preventDefault();
      setAutocompleteIndex((current) =>
        current === 0 ? workspaceFileSuggestions.length - 1 : current - 1
      );
      return;
    }

    if (event.key === 'Tab' || event.key === 'Enter') {
      const activeSuggestion = workspaceFileSuggestions.find((_, i) => i === autocompleteIndex);
      if (activeSuggestion) {
        event.preventDefault();
        applyWorkspaceSuggestion({
          path: activeSuggestion.path,
          entryType: activeSuggestion.entryType,
        });
      }
      return;
    }

    if (event.key === 'Escape') {
      event.preventDefault();
      setPromptSelectionStart(draft.schedulePromptContext.length);
    }
  };

  const selectWorkspaceFile = React.useCallback(
    (path: string) => {
      const normalizedPath = normalizeWorkspaceFilePath(path);
      if (!normalizedPath) return;
      setDraft((previous) => ({
        ...previous,
        workspaceFilePaths: (previous.workspaceFilePaths ?? []).includes(normalizedPath)
          ? previous.workspaceFilePaths
          : [...(previous.workspaceFilePaths ?? []), normalizedPath],
      }));
    },
    [setDraft]
  );

  const removeWorkspaceFile = React.useCallback(
    (path: string) => {
      setDraft((previous) => ({
        ...previous,
        workspaceFilePaths: (previous.workspaceFilePaths ?? []).filter((item) => item !== path),
      }));
    },
    [setDraft]
  );

  const pickerAgent = React.useMemo<AgentFilesPanelAgent | null>(() => {
    if (!targetAgentId) return null;
    const targetAgent = agents.find((agent) => agent.id === targetAgentId);
    return {
      id: targetAgentId,
      name: targetAgent?.name ?? 'Agent',
      sandboxEnabled: Boolean(targetAgent?.sandbox?.enabled),
    };
  }, [agents, targetAgentId]);

  const onSubmit = async (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    const saved = await save();
    if (!saved) {
      return;
    }

    setShowScheduleForm(false);
    if (!editingScheduleId) {
      setDraft((previous) => ({
        ...previous,
        targetId: '',
        schedulePromptContext: '',
        workspaceFilePaths: [],
      }));
    }
  };

  const cancelEdit = () => {
    setShowScheduleForm(false);
    setEditingScheduleId(null);
    setDraft((previous) => ({
      ...previous,
      targetId: '',
      schedulePromptContext: '',
      workspaceFilePaths: [],
    }));
  };

  const modeIsOff = draft.mode === 'off';

  if (loading) {
    return (
      <div className="page-shell">
        <PageIntro
          kicker="Automation Control"
          title="Auto-Pilot"
          description="Set a schedule for your agents and workflows."
          animate={false}
        />
        <PageState message="Loading Auto-Pilot…" />
      </div>
    );
  }

  return (
    <div className="page-shell gap-6">
      <PageIntro
        kicker="Automation Control"
        title="Auto-Pilot"
        description="Set a repeating schedule for your agents and workflows."
      />

      {error ? (
        <Alert variant="destructive" className="p-3">
          {String(error)}
        </Alert>
      ) : null}

      {/* ── Schedule form ── */}
      {showScheduleForm || Boolean(editingScheduleId) ? (
        <section className="workspace-panel order-2 space-y-4">
          <div className="flex items-center justify-between gap-2">
            <div className="flex items-center gap-2">
              <CalendarClock className="h-4 w-4 text-primary" />
              <h2 className="text-base font-semibold">
                {editingScheduleId ? 'Edit Schedule' : 'New Schedule'}
              </h2>
            </div>
            {!editingScheduleId ? (
              <Button
                type="button"
                variant="secondary"
                size="sm"
                onClick={() => setShowScheduleForm(false)}
              >
                Close
              </Button>
            ) : null}
          </div>

          <form className="space-y-4" onSubmit={(event) => void onSubmit(event)}>
            <AutopilotScheduleConfigurationEditor
              draft={draft}
              setDraft={setDraft}
              agents={targetOptions}
              workflows={workflows.map((item) => ({
                ...item,
                hasStartFields: Object.keys(item.inputSchema?.properties ?? {}).length > 0,
              }))}
            >
              {draft.targetType === 'workflow' ? (
                workflow.data ? (
                  <WorkflowStartValuesFields
                    inputSchema={workflow.data.inputSchema}
                    value={draft.schedulePromptContext}
                    disabled={modeIsOff}
                    fileReferenceAgentId={targetAgentId}
                    onChange={(value) =>
                      setDraft((previous) => ({ ...previous, schedulePromptContext: value }))
                    }
                  />
                ) : (
                  <p className="rounded-lg border border-border p-4 text-sm text-muted-foreground">
                    {workflow.error
                      ? 'Could not load this workflow’s start fields.'
                      : 'Loading workflow fields…'}
                  </p>
                )
              ) : (
                <div className="space-y-1">
                  <label className="text-sm font-medium">
                    What should it do?{' '}
                    <span className="font-normal text-muted-foreground">(optional)</span>
                  </label>
                  <p className="text-xs text-muted-foreground">
                    Write a short instruction like "Summarize today's news" or "Check system health
                    and report any issues." Type # to reference workspace files.
                    The agent receives this as its starting instruction each time it runs.
                  </p>
                  <div className="relative mt-1 rounded-md border border-border bg-background focus-within:border-primary/40 focus-within:ring-4 focus-within:ring-primary/10">
                    <textarea
                      ref={promptTextareaRef}
                      value={draft.schedulePromptContext}
                      onChange={(event) => {
                        syncWorkspaceReferencePills(event.target.value);
                        setDraft((previous) => ({
                          ...previous,
                          schedulePromptContext: event.target.value,
                        }));
                        setSelectedWorkspaceReferencePillId(null);
                        setPromptSelectionStart(
                          event.target.selectionStart ?? event.target.value.length
                        );
                      }}
                      onKeyDown={handlePromptKeyDown}
                      onClick={(event) => {
                        setSelectedWorkspaceReferencePillId(null);
                        setPromptSelectionStart(
                          event.currentTarget.selectionStart ?? draft.schedulePromptContext.length
                        );
                      }}
                      onKeyUp={(event) => {
                        setSelectedWorkspaceReferencePillId(null);
                        setPromptSelectionStart(
                          event.currentTarget.selectionStart ?? draft.schedulePromptContext.length
                        );
                      }}
                      onSelect={(event) => {
                        setSelectedWorkspaceReferencePillId(null);
                        setPromptSelectionStart(
                          event.currentTarget.selectionStart ?? draft.schedulePromptContext.length
                        );
                      }}
                      className="relative z-10 w-full resize-none bg-transparent px-3 py-2 text-sm text-transparent caret-foreground placeholder:text-muted-foreground focus-visible:outline-none"
                      placeholder={
                        'e.g. "Summarize recent support tickets and flag anything urgent"'
                      }
                      rows={3}
                      disabled={modeIsOff}
                    />
                    <div className="pointer-events-none absolute inset-0 overflow-hidden px-3 py-2 text-sm text-foreground whitespace-pre-wrap wrap-break-word">
                      {promptOverlaySegments.map((segment) =>
                        segment.type === 'text' ? (
                          <React.Fragment key={segment.key}>{segment.value}</React.Fragment>
                        ) : (
                          <button
                            key={segment.key}
                            type="button"
                            onMouseDown={(event) => event.preventDefault()}
                            onClick={() => {
                              setSelectedWorkspaceReferencePillId(segment.value.id);
                              pendingCaretPositionRef.current = segment.value.end;
                              focusPromptComposer();
                            }}
                            aria-label={`Reference ${segment.value.text}`}
                            className={cn(
                              'pointer-events-auto inline max-w-full rounded-md align-baseline font-medium outline-1 outline-offset-0 transition-colors',
                              selectedWorkspaceReferencePillId === segment.value.id
                                ? 'bg-primary/10 text-primary outline-primary'
                                : 'bg-primary/10 text-primary outline-primary/30'
                            )}
                          >
                            <span className="truncate">{segment.value.text}</span>
                          </button>
                        )
                      )}
                    </div>

                    {shouldShowWorkspaceFileAutocomplete && !modeIsOff && (
                      <div className="absolute left-0 right-0 top-full z-20 mt-2 overflow-hidden rounded-xl border border-border bg-background shadow-xl">
                        <div className="section-label border-b border-border/50 px-3 py-2">
                          Workspace Files
                        </div>
                        <div className="max-h-64 overflow-y-auto py-1">
                          {workspaceFileSuggestions.map((suggestion, index) => (
                            <button
                              key={suggestion.path}
                              type="button"
                              onMouseDown={(event) => event.preventDefault()}
                              onClick={() =>
                                applyWorkspaceSuggestion({
                                  path: suggestion.path,
                                  entryType: suggestion.entryType,
                                })
                              }
                              className={cn(
                                'flex w-full items-center justify-between gap-3 px-3 py-2 text-left text-sm transition-colors',
                                index === autocompleteIndex
                                  ? 'bg-primary/10 text-foreground'
                                  : 'text-muted-foreground hover:bg-muted/70 hover:text-foreground'
                              )}
                            >
                              <span className="truncate font-medium">#{suggestion.path}</span>
                              <span className="section-label shrink-0">
                                {suggestion.entryType === 'directory' ? 'Folder' : 'File'}
                              </span>
                            </button>
                          ))}
                        </div>
                      </div>
                    )}
                  </div>
                </div>
              )}
              <div className="space-y-2">
                <div className="flex flex-wrap items-center gap-2">
                  <Button
                    type="button"
                    variant="outline"
                    disabled={!targetAgentId}
                    onClick={() => setWorkspaceFilePickerOpen(true)}
                  >
                    Choose files
                  </Button>
                  {targetAgentId ? (
                    <p className="text-xs text-muted-foreground">
                      Files come from {pickerAgent?.name ?? 'the selected agent'}&apos;s workspace.
                      Auto-Pilot runs on the server and can only use files stored there.
                    </p>
                  ) : (
                    <p className="text-xs text-muted-foreground">Choose a target first.</p>
                  )}
                </div>
                {(draft.workspaceFilePaths ?? []).length > 0 ? (
                  <div className="flex flex-wrap gap-2">
                    {draft.workspaceFilePaths!.map((path) => (
                      <span
                        key={path}
                        className="flex max-w-full items-center gap-2 rounded-full border border-border/70 bg-background px-3 py-1 text-xs"
                      >
                        <span className="truncate font-medium">{path}</span>
                        <button
                          type="button"
                          className="rounded-full p-0.5 text-muted-foreground hover:bg-muted hover:text-foreground"
                          aria-label={`Remove ${path}`}
                          onClick={() => removeWorkspaceFile(path)}
                        >
                          <X className="h-3 w-3" />
                        </button>
                      </span>
                    ))}
                  </div>
                ) : null}
              </div>
            </AutopilotScheduleConfigurationEditor>

            <div className="flex items-center gap-2">
              <Button type="submit" disabled={saving || !draft.targetId}>
                {saving ? 'Saving…' : editingScheduleId ? 'Save changes' : '＋ Create schedule'}
              </Button>
              {editingScheduleId ? (
                <Button type="button" variant="secondary" onClick={cancelEdit}>
                  Cancel
                </Button>
              ) : null}
            </div>
          </form>
        </section>
      ) : null}

      <WorkspaceFilePickerDialog
        open={workspaceFilePickerOpen}
        onOpenChange={setWorkspaceFilePickerOpen}
        agent={pickerAgent}
        selectedPaths={draft.workspaceFilePaths ?? []}
        onSelectPath={selectWorkspaceFile}
      />

      <AutopilotActivityPanels
        schedules={schedules}
        runs={runs}
        inbox={inbox}
        agents={agents}
        workflows={workflows}
        setDraft={setDraft}
        setEditingScheduleId={setEditingScheduleId}
        setDraftFromSchedule={setDraftFromSchedule}
        setShowScheduleForm={setShowScheduleForm}
        pauseSchedule={pauseSchedule}
        resumeSchedule={resumeSchedule}
        runNow={runNow}
        deleteSchedule={deleteSchedule}
        markInboxRead={markInboxRead}
        mutating={mutating}
      />
    </div>
  );
};

export default AutopilotPage;
