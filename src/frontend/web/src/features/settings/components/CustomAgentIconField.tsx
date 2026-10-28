import { Input } from '@/components/ui/input';

interface CustomAgentIconFieldProps {
  id: string;
  value: string;
  onChange: (value: string) => void;
}

export function CustomAgentIconField({ id, value, onChange }: Readonly<CustomAgentIconFieldProps>) {
  return (
    <>
      <label className="mt-3 block text-xs text-muted-foreground" htmlFor={id}>
        Or enter another emoji
      </label>
      <Input
        id={id}
        value={value}
        maxLength={16}
        onChange={(event) => onChange(event.target.value)}
        aria-label="Custom agent emoji"
        className="mt-1 w-24 text-center text-lg"
      />
    </>
  );
}
