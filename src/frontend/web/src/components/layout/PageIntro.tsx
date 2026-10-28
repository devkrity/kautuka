import React from 'react';

interface PageIntroProps {
  kicker: string;
  title: React.ReactNode;
  description: string;
  actions?: React.ReactNode;
  actionsClassName?: string;
  animate?: boolean;
}

export const PageIntro: React.FC<PageIntroProps> = ({
  kicker,
  title,
  description,
  actions,
  actionsClassName,
  animate = true,
}) => {
  return (
    <div className={animate ? 'mb-6 reveal-up' : 'mb-6'}>
      <p className="section-label mb-2">{kicker}</p>
      <div className="flex flex-wrap items-end justify-between gap-4">
        <div className="min-w-0 flex-1">
          <h1 className="page-title mb-2">{title}</h1>
          <p className="text-sm text-muted-foreground">{description}</p>
        </div>
        {actions ? (
          <div className={`min-w-0 max-w-full ${actionsClassName ?? ''}`}>{actions}</div>
        ) : null}
      </div>
    </div>
  );
};
