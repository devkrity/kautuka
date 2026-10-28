import React from 'react';
import { useNavigate } from 'react-router-dom';
import {
  useMemoriesPage,
  MemoriesTargetSidebar,
  FactTimeline,
  SessionHistory,
} from '@/features/memories';
import { useAgents } from '@/features/settings';
import { Alert } from '@/components/ui/alert';
import { PageIntro } from '@/components/layout/PageIntro';
import { PageState } from '@/components/layout/PageState';

export const MemoriesPage: React.FC = () => {
  const { data: agents = [], isLoading: agentsLoading } = useAgents();
  const loading = agentsLoading;
  const navigate = useNavigate();

  const {
    selectedId,
    memoriesData,
    expandedSession,
    memoriesLoading,
    error,
    editingFactId,
    editDraft,
    savingFactId,
    deletingFactId,
    deletingSessionId,
    factsTimeline,
    sessionIdSet,
    setExpandedSession,
    setEditDraft,
    setEditingFactId,
    handleSelect,
    handleEditFact,
    handleSaveFact,
    handleDeleteFact,
    handleDeleteSession,
  } = useMemoriesPage();

  const targetList = agents.map((a) => ({ id: a.id, name: a.name }));
  const selectedName = agents.find((a) => a.id === selectedId)?.name;

  return (
    <div className="page-shell-fill">
      <PageIntro
        kicker="Memory Workspace"
        title="Memories"
        description="Search through past conversations and view long-term facts that agents remember between sessions."
      />

      <div className="workspace-grid-fill grid-cols-1 lg:grid-cols-[17rem_minmax(0,1fr)]">
        <MemoriesTargetSidebar
          targetList={targetList}
          selectedId={selectedId}
          onSelect={handleSelect}
        />

        <div className="workspace-scroll space-y-4 pr-1">
          {(() => {
            if (selectedId == null) {
              return (
                <PageState
                  message="Select an agent to view memories"
                  minHeightClassName="min-h-48"
                />
              );
            }

            if (memoriesLoading) {
              return <PageState message="Loading memories..." minHeightClassName="min-h-48" />;
            }

            if (error) {
              return (
                <Alert variant="destructive">
                  {error}
                </Alert>
              );
            }

            if (!memoriesData) {
              return null;
            }

            return (
              <>
                <FactTimeline
                  selectedName={selectedName}
                  facts={factsTimeline}
                  editingFactId={editingFactId}
                  editDraft={editDraft}
                  savingFactId={savingFactId}
                  deletingFactId={deletingFactId}
                  sessionIdSet={sessionIdSet}
                  onEditDraftChange={setEditDraft}
                  onStartEdit={(fact) =>
                    handleEditFact(fact.key, fact.title, fact.summary, fact.category)
                  }
                  onCancelEdit={() => {
                    setEditingFactId(null);
                    setEditDraft(null);
                  }}
                  onSaveFact={(factId) => handleSaveFact(factId)}
                  onDeleteFact={(factId) => handleDeleteFact(factId)}
                  onOpenSession={(sessionId) => {
                    navigate(
                      `/workspace/${encodeURIComponent(selectedId)}?sessionId=${encodeURIComponent(sessionId)}`
                    );
                  }}
                />

                <SessionHistory
                  sessions={memoriesData.sessions}
                  expandedSession={expandedSession}
                  deletingSessionId={deletingSessionId}
                  onToggle={(sessionId) =>
                    setExpandedSession(expandedSession === sessionId ? null : sessionId)
                  }
                  onOpen={(sessionId) =>
                    navigate(
                      `/workspace/${encodeURIComponent(selectedId)}?sessionId=${encodeURIComponent(sessionId)}`
                    )
                  }
                  onDelete={(sessionId) => handleDeleteSession(sessionId)}
                />

                {factsTimeline.length === 0 && memoriesData.sessions.length > 0 && (
                  <div className="workspace-block">
                    <p className="text-sm text-muted-foreground">
                      No durable fact timeline yet. Use explicit facts in conversation to avoid
                      relying on full transcript memory.
                    </p>
                  </div>
                )}
              </>
            );
          })()}
        </div>
      </div>
    </div>
  );
};
