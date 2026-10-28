import { useState } from 'react';
import {
  deleteFact,
  deleteMemorySession,
  fetchMemoriesForAgent,
  updateFact,
  type MemoriesBundle,
} from '@/features/memories';
import { parseApiTimestamp } from '@/lib/utils';

export function useMemoriesPage() {
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [memoriesData, setMemoriesData] = useState<MemoriesBundle | null>(null);
  const [expandedSession, setExpandedSession] = useState<string | null>(null);
  const [memoriesLoading, setMemoriesLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const [editingFactId, setEditingFactId] = useState<string | null>(null);
  const [editDraft, setEditDraft] = useState<{
    title: string;
    content: string;
    category: string;
  } | null>(null);
  const [savingFactId, setSavingFactId] = useState<string | null>(null);
  const [deletingFactId, setDeletingFactId] = useState<string | null>(null);
  const [deletingSessionId, setDeletingSessionId] = useState<string | null>(null);

  const loadMemories = async (id: string) => {
    setMemoriesLoading(true);
    setMemoriesData(null);
    setExpandedSession(null);
    setEditingFactId(null);
    setEditDraft(null);

    try {
      const data = await fetchMemoriesForAgent(id);
      setMemoriesData(data);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to load memories');
    } finally {
      setMemoriesLoading(false);
    }
  };

  const handleSelect = (id: string) => {
    setSelectedId(id);
    setError(null);
    void loadMemories(id);
  };

  const handleEditFact = (factId: string, title: string, content: string, category: string) => {
    setEditingFactId(factId);
    setEditDraft({ title, content, category });
  };

  const handleSaveFact = async (factId: string) => {
    if (!editDraft) return;
    setSavingFactId(factId);
    try {
      await updateFact(factId, editDraft);
      setEditingFactId(null);
      setEditDraft(null);
      if (selectedId) void loadMemories(selectedId);
    } finally {
      setSavingFactId(null);
    }
  };

  const handleDeleteFact = async (factId: string) => {
    setDeletingFactId(factId);
    try {
      await deleteFact(factId);
      setMemoriesData((prev) =>
        prev ? { ...prev, facts: prev.facts.filter((f) => f.id !== factId) } : prev
      );
    } finally {
      setDeletingFactId(null);
    }
  };

  const handleDeleteSession = async (sessionId: string) => {
    setDeletingSessionId(sessionId);
    try {
      await deleteMemorySession(sessionId);
      setMemoriesData((prev) =>
        prev ? { ...prev, sessions: prev.sessions.filter((s) => s.id !== sessionId) } : prev
      );
      if (expandedSession === sessionId) setExpandedSession(null);
    } finally {
      setDeletingSessionId(null);
    }
  };

  const factsTimeline = (memoriesData?.facts ?? []).map((fact) => {
    const summary = fact.content;

    return {
      key: fact.id,
      id: fact.id,
      title: fact.title,
      summary,
      category: fact.category,
      source: fact.source,
      confidence: fact.confidence,
      updatedAt: parseApiTimestamp(fact.updatedAt),
      sessionId: fact.sessionId ?? null,
    };
  });

  const sessionIdSet = new Set((memoriesData?.sessions ?? []).map((s) => s.id));

  return {
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
  };
}
