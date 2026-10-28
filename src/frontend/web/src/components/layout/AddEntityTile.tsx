import React from 'react';
import { Plus } from 'lucide-react';

interface AddEntityTileProps {
  title: string;
  description: string;
  onClick: () => void;
}

export const AddEntityTile: React.FC<AddEntityTileProps> = ({ title, description, onClick }) => {
  return (
    <button
      type="button"
      onClick={onClick}
      className="surface-strong flex min-h-56 flex-col items-center justify-center gap-3 border-dashed p-6 text-center hover:bg-muted/30"
    >
      <span className="inline-flex h-11 w-11 items-center justify-center rounded-full bg-accent text-primary">
        <Plus className="h-5 w-5" />
      </span>
      <p className="text-lg font-bold text-foreground">{title}</p>
      <p className="max-w-[18rem] text-sm text-muted-foreground">{description}</p>
    </button>
  );
};
