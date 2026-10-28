import React from 'react';
import { Bot } from 'lucide-react';

interface TargetItem {
  id: string;
  name: string;
}

interface MemoriesTargetSidebarProps {
  targetList: TargetItem[];
  selectedId: string | null;
  onSelect: (id: string) => void;
}

export function MemoriesTargetSidebar({
  targetList,
  selectedId,
  onSelect,
}: MemoriesTargetSidebarProps) {
  return (
    <aside className="workspace-panel-compact">
      <div className="mb-3">
        <p className="section-label">
          Agents
        </p>
      </div>

      <div className="workspace-scroll space-y-1">
        {targetList.length === 0 ? (
          <p className="py-4 text-center text-xs text-muted-foreground">No agents</p>
        ) : (
          targetList.map((item) => (
            <button
              key={item.id}
              type="button"
              onClick={() => onSelect(item.id)}
              className={`workspace-select-item p-2.5 text-sm ${selectedId === item.id ? 'workspace-select-item-active' : ''}`}
            >
              <div className="inline-flex items-center gap-2">
                <Bot className="h-4 w-4 text-primary" />
                <div className="truncate font-medium">{item.name}</div>
              </div>
            </button>
          ))
        )}
      </div>
    </aside>
  );
}
