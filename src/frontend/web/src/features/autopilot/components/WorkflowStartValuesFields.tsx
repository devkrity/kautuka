import React from 'react';
import { useAgentFiles, useSearchAgentFiles } from '@/features/agent-files';
import {
  applyWorkspaceFileReferenceCompletion,
  getWorkspaceFileReferenceQuery,
} from '@/features/chat';

interface WorkflowStartValuesFieldsProps {
  inputSchema: Record<string, unknown>;
  value: string;
  disabled: boolean;
  onChange: (value: string) => void;
  fileReferenceAgentId?: string;
}

export const WorkflowStartValuesFields: React.FC<WorkflowStartValuesFieldsProps> = ({
  inputSchema,
  value,
  disabled,
  onChange,
  fileReferenceAgentId,
}) => {
  const schema = inputSchema as {
    properties?: Record<
      string,
      { type?: string; description?: string; title?: string; enum?: string[] }
    >;
    required?: string[];
  };
  let parsedValues: Record<string, unknown> = Object.create(null) as Record<string, unknown>;
  try {
    const parsed: unknown = JSON.parse(value || '{}');
    if (parsed && typeof parsed === 'object' && !Array.isArray(parsed))
      parsedValues = parsed as Record<string, unknown>;
  } catch {
    parsedValues = Object.create(null) as Record<string, unknown>;
  }
  const properties = Object.entries(schema.properties ?? {}).filter(([name]) =>
    /^[A-Za-z][A-Za-z0-9_]{0,63}$/.test(name)
  );
  const values: Record<string, unknown> = Object.create(null) as Record<string, unknown>;
  for (const [name] of properties) {
    if (Object.prototype.hasOwnProperty.call(parsedValues, name))
      Object.defineProperty(values, name, { value: parsedValues[name], enumerable: true });
  }

  const setValue = (name: string, rawValue: string, type: string) => {
    const next: Record<string, unknown> = Object.assign(
      Object.create(null) as Record<string, unknown>,
      values
    );
    if (rawValue === '') delete next[name];
    else if (type === 'boolean')
      Object.defineProperty(next, name, {
        value: rawValue === 'true',
        enumerable: true,
        configurable: true,
        writable: true,
      });
    else if (type === 'number' || type === 'integer') {
      const numeric = Number(rawValue);
      if (Number.isFinite(numeric))
        Object.defineProperty(next, name, {
          value: numeric,
          enumerable: true,
          configurable: true,
          writable: true,
        });
    } else
      Object.defineProperty(next, name, {
        value: rawValue,
        enumerable: true,
        configurable: true,
        writable: true,
      });
    onChange(JSON.stringify(next));
  };

  return (
    <div className="space-y-3 rounded-lg border border-border p-4">
      <div>
        <h3 className="text-sm font-semibold">Workflow start fields</h3>
        <p className="text-xs text-muted-foreground">
          These values are supplied when each scheduled run starts.
        </p>
      </div>
      {properties.map(([name, field]) => {
        const required = schema.required?.includes(name) ?? false;
        const type = field.type ?? 'string';
        const current = Object.prototype.hasOwnProperty.call(values, name)
          ? values[name]
          : undefined;
        return (
          <div key={name} className="space-y-1">
            <label className="text-sm font-medium" htmlFor={`autopilot-workflow-field-${name}`}>
              {field.title ??
                name
                  .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
                  .replace(/_/g, ' ')
                  .toLowerCase()
                  .replace(/^\p{L}/u, (letter) => letter.toUpperCase())}
              {required ? ' (required)' : ''}
            </label>
            {field.description ? (
              <p className="text-xs text-muted-foreground">{field.description}</p>
            ) : null}
            {field.enum?.length ? (
              <select
                id={`autopilot-workflow-field-${name}`}
                value={current === undefined ? '' : String(current)}
                onChange={(event) => setValue(name, event.target.value, type)}
                required={required}
                disabled={disabled}
                className="w-full rounded-md border border-border bg-background px-3 py-2 text-sm"
              >
                <option value="">— select —</option>
                {field.enum.map((answer) => (
                  <option key={answer} value={answer}>
                    {answer}
                  </option>
                ))}
              </select>
            ) : type === 'boolean' ? (
              <select
                id={`autopilot-workflow-field-${name}`}
                value={current === undefined ? '' : String(current)}
                onChange={(event) => setValue(name, event.target.value, type)}
                required={required}
                disabled={disabled}
                className="w-full rounded-md border border-border bg-background px-3 py-2 text-sm"
              >
                <option value="">— select —</option>
                <option value="true">Yes</option>
                <option value="false">No</option>
              </select>
            ) : type === 'string' ? (
              <WorkflowStartStringField
                id={`autopilot-workflow-field-${name}`}
                value={current === undefined ? '' : String(current)}
                disabled={disabled}
                required={required}
                fileReferenceAgentId={fileReferenceAgentId}
                onChange={(next) => setValue(name, next, type)}
              />
            ) : type === 'number' || type === 'integer' ? (
              <input
                id={`autopilot-workflow-field-${name}`}
                type="number"
                step={type === 'integer' ? '1' : undefined}
                value={current === undefined ? '' : String(current)}
                onChange={(event) => setValue(name, event.target.value, type)}
                required={required}
                disabled={disabled}
                className="w-full rounded-md border border-border bg-background px-3 py-2 text-sm"
              />
            ) : (
              <WorkflowStartStringField
                id={`autopilot-workflow-field-${name}`}
                value={current === undefined ? '' : String(current)}
                disabled={disabled}
                required={required}
                fileReferenceAgentId={fileReferenceAgentId}
                onChange={(next) => setValue(name, next, 'string')}
              />
            )}
          </div>
        );
      })}
      {Object.keys(schema.properties ?? {}).length === 0 ? (
        <p className="text-sm text-muted-foreground">This workflow has no start fields.</p>
      ) : null}
    </div>
  );
};

interface WorkflowStartStringFieldProps {
  id: string;
  value: string;
  disabled: boolean;
  required: boolean;
  fileReferenceAgentId?: string;
  onChange: (value: string) => void;
}

const WorkflowStartStringField: React.FC<WorkflowStartStringFieldProps> = ({
  id,
  value,
  disabled,
  required,
  fileReferenceAgentId,
  onChange,
}) => {
  const inputRef = React.useRef<HTMLInputElement | null>(null);
  const [caretPosition, setCaretPosition] = React.useState(value.length);
  const query = React.useMemo(
    () => getWorkspaceFileReferenceQuery(value, caretPosition),
    [caretPosition, value]
  );
  const directoryQuery = useAgentFiles(
    fileReferenceAgentId && query ? fileReferenceAgentId : null,
    query?.directoryPath ?? ''
  );
  const searchQuery = useSearchAgentFiles(
    fileReferenceAgentId && query && !query.directoryPath ? fileReferenceAgentId : null,
    query?.namePrefix ?? '',
    8
  );
  const suggestions = React.useMemo(() => {
    if (!query) return [];
    if (!query.directoryPath && query.namePrefix)
      return (searchQuery.data?.entries ?? []).slice(0, 8);
    if (directoryQuery.data?.status !== 'ready') return [];
    return directoryQuery.data.response.entries
      .filter((entry) => entry.name.toLowerCase().startsWith(query.namePrefix.toLowerCase()))
      .sort((left, right) =>
        left.entryType === right.entryType
          ? left.name.localeCompare(right.name)
          : left.entryType === 'directory'
            ? -1
            : 1
      )
      .slice(0, 8);
  }, [directoryQuery.data, query, searchQuery.data]);

  const selectSuggestion = (path: string, entryType: 'file' | 'directory') => {
    if (!query) return;
    const next = applyWorkspaceFileReferenceCompletion(value, query, path, entryType);
    onChange(next.text);
    setCaretPosition(next.caretPosition);
    requestAnimationFrame(() =>
      inputRef.current?.setSelectionRange(next.caretPosition, next.caretPosition)
    );
  };

  return (
    <div className="relative">
      <input
        ref={inputRef}
        id={id}
        type="text"
        value={value}
        onChange={(event) => {
          onChange(event.target.value);
          setCaretPosition(event.target.selectionStart ?? event.target.value.length);
        }}
        onSelect={(event) => setCaretPosition(event.currentTarget.selectionStart ?? value.length)}
        required={required}
        disabled={disabled}
        className="w-full rounded-md border border-border bg-background px-3 py-2 text-sm"
      />
      {query && fileReferenceAgentId && suggestions.length > 0 && !disabled ? (
        <div className="absolute left-0 right-0 top-full z-20 mt-2 overflow-hidden rounded-xl border border-border bg-background shadow-xl">
          <div className="section-label border-b border-border/50 px-3 py-2">Workspace Files</div>
          <div className="max-h-64 overflow-y-auto py-1">
            {suggestions.map((suggestion) => (
              <button
                key={suggestion.path}
                type="button"
                onMouseDown={(event) => event.preventDefault()}
                onClick={() => selectSuggestion(suggestion.path, suggestion.entryType)}
                className="flex w-full items-center justify-between gap-3 px-3 py-2 text-left text-sm text-muted-foreground transition-colors hover:bg-muted/70 hover:text-foreground"
              >
                <span className="truncate font-medium">#{suggestion.path}</span>
                <span className="section-label shrink-0">
                  {suggestion.entryType === 'directory' ? 'Folder' : 'File'}
                </span>
              </button>
            ))}
          </div>
        </div>
      ) : null}
    </div>
  );
};
