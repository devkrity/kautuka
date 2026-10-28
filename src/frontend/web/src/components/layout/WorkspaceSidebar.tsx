import { Button } from '@/components/ui/button';
import { cn } from '@/lib/utils';
import { Plus } from 'lucide-react';
import React from 'react';

export interface WorkspaceSidebarItem {
  id: string;
  name: string;
  description?: string;
  color: string;
  icon: React.ReactNode;
  actions?: React.ReactNode;
  actionsOnHover?: boolean;
  children?: React.ReactNode;
}

interface WorkspaceSidebarProps {
  title: string;
  ariaLabel: string;
  addLabel: string;
  selectedId?: string | null;
  items: WorkspaceSidebarItem[];
  onSelect: (id: string) => void;
  onAdd: () => void;
  renderChildren?: (item: WorkspaceSidebarItem) => React.ReactNode;
  emptyText?: string;
  footer?: React.ReactNode;
}

export const WorkspaceSidebar: React.FC<WorkspaceSidebarProps> = ({
  title,
  ariaLabel,
  addLabel,
  selectedId,
  items,
  onSelect,
  onAdd,
  renderChildren,
  emptyText,
  footer,
}) => (
  <aside className="workspace-panel-compact max-h-[42vh] gap-3 rounded-xl p-3 lg:max-h-none lg:rounded-2xl lg:p-4">
    <div className="flex items-center justify-between gap-2 px-1">
      <h1 className="section-label">{title}</h1>
      <Button variant="outline" size="sm" onClick={onAdd}>
        <Plus className="mr-1.5 h-4 w-4" />
        {addLabel}
      </Button>
    </div>
    <nav className="workspace-scroll space-y-1 overflow-x-hidden" aria-label={ariaLabel}>
      {items.map((item) => {
        const selected = selectedId === item.id;
        return (
          <div key={item.id}>
            <div
              className={cn(
                'group flex items-center gap-1 rounded-lg pr-1 transition-colors',
                selected ? 'bg-primary/10 text-primary' : 'hover:bg-muted'
              )}
            >
              <button
                type="button"
                aria-current={selected ? 'page' : undefined}
                aria-expanded={selected}
                title={item.name}
                onClick={() => onSelect(item.id)}
                style={selected ? { borderLeft: `3px solid ${item.color}` } : undefined}
                className={cn(
                  'flex min-w-0 flex-1 items-center gap-2 rounded-lg px-2.5 py-2 text-left',
                  selected ? 'text-base font-bold' : 'font-medium text-foreground'
                )}
              >
                <span
                  className="flex h-7 w-7 shrink-0 items-center justify-center rounded-full text-base"
                  style={{ backgroundColor: `${item.color}20` }}
                  aria-hidden="true"
                >
                  {item.icon}
                </span>
                <span className="min-w-0 truncate">
                  <span className="block truncate">{item.name}</span>
                  {item.description ? (
                    <span className="block truncate text-xs font-normal text-muted-foreground">
                      {item.description}
                    </span>
                  ) : null}
                </span>
              </button>
              {item.actions ? (
                <div
                  className={cn(
                    'flex shrink-0 items-center',
                    item.actionsOnHover && !selected && 'opacity-0 transition-opacity group-hover:opacity-100 group-focus-within:opacity-100'
                  )}
                >
                  {item.actions}
                </div>
              ) : null}
            </div>
            {selected ? (
              <div
                className="ml-5 mt-1 max-h-[30vh] overflow-y-auto overscroll-contain border-l-2 pl-3 lg:max-h-[50vh]"
                style={{ borderLeftColor: item.color }}
              >
                {renderChildren ? renderChildren(item) : item.children}
              </div>
            ) : null}
          </div>
        );
      })}
      {items.length === 0 && emptyText ? (
        <p className="px-2.5 py-2 text-xs text-muted-foreground">{emptyText}</p>
      ) : null}
      {footer}
    </nav>
  </aside>
);
