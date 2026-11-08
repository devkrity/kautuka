export function LocalNetworkChoice({
  checked,
  onChange,
  disabled = false,
}: {
  checked: boolean;
  onChange: (checked: boolean) => void;
  disabled?: boolean;
}) {
  return (
    <label className="flex items-start gap-2 rounded-lg border border-border p-3 text-sm">
      <input
        type="checkbox"
        className="mt-0.5 size-4 shrink-0 accent-primary"
        checked={checked}
        disabled={disabled}
        onChange={(event) => onChange(event.target.checked)}
      />
      <span>
        <span className="block font-medium">Allow local-network access</span>
        <span className="mt-1 block text-xs text-muted-foreground">
          Include LAN/private addresses for this exact destination. Metadata and protected addresses
          stay blocked.
        </span>
      </span>
    </label>
  );
}
