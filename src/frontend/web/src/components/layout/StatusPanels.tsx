import React from 'react';

export interface StatusPanelItem {
  title: string;
  value: string;
}

interface StatusPanelsProps {
  items: StatusPanelItem[];
}

export const StatusPanels: React.FC<StatusPanelsProps> = ({ items }) => {
  return (
    <div className="grid grid-cols-1 gap-3 md:grid-cols-3">
      {items.map((item) => (
        <div key={item.title} className="surface-strong p-4">
          <p className="section-label mb-2">
            {item.title}
          </p>
          <p className="text-sm text-foreground">{item.value}</p>
        </div>
      ))}
    </div>
  );
};
