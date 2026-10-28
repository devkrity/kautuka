import React from 'react';
import { ChevronDown } from 'lucide-react';
import { Button } from '@/components/ui/button';

export function ShowMoreButton({
  count,
  onClick,
}: Readonly<{ count: number; onClick: () => void }>) {
  if (count <= 0) return null;
  return (
    <div className="flex justify-center pt-2">
      <Button type="button" variant="outline" onClick={onClick}>
        <ChevronDown className="mr-2 h-4 w-4" /> Show {Math.min(9, count)} more
        <span className="ml-1 text-muted-foreground">({count} remaining)</span>
      </Button>
    </div>
  );
}
