import React from 'react';

interface WorkflowDiagramProps {
  chart: string;
  description?: string;
}

export const WorkflowDiagram: React.FC<WorkflowDiagramProps> = ({
  chart,
  description = 'Starts at the first step. A pause waits for your review. A label on an arrow is the choice that takes that path.',
}) => {
  const ownerFriendlyChart = chart.replace(
    /Pause after step (\d+)/g,
    'Step $1 · Waits for your OK'
  );
  const host = React.useRef<HTMLDivElement>(null);
  const [failed, setFailed] = React.useState(false);

  React.useEffect(() => {
    let cancelled = false;
    setFailed(false);
    const renderId = `workflow-diagram-${Math.random().toString(36).slice(2, 10)}`;
    void import('mermaid')
      .then(async ({ default: mermaid }) => {
        mermaid.initialize({
          startOnLoad: false,
          securityLevel: 'strict',
          theme: 'base',
          fontFamily: 'Inter, ui-sans-serif, system-ui, sans-serif',
          flowchart: { htmlLabels: false, curve: 'basis', padding: 12 },
        });
        const drawn = await mermaid.render(renderId, ownerFriendlyChart);
        const holder = document.getElementById(`d${renderId}`);
        const rendered = holder?.querySelector('svg') ?? null;
        const parsed = rendered
          ? null
          : new DOMParser().parseFromString(drawn.svg, 'text/html').querySelector('svg');
        const svg = rendered ?? parsed;
        if (!cancelled && host.current && svg) {
          host.current.replaceChildren(svg);
        } else if (!cancelled) {
          setFailed(true);
        }
        holder?.remove();
      })
      .catch(() => {
        if (!cancelled) {
          setFailed(true);
        }
      });
    return () => {
      cancelled = true;
    };
  }, [ownerFriendlyChart]);

  return (
    <section className="workspace-panel space-y-2" data-testid="workflow-diagram">
      <h2 className="text-sm font-semibold">How this workflow runs</h2>
      <p className="text-xs text-muted-foreground">{description}</p>
      {failed ? (
        <p className="text-sm text-muted-foreground">The picture could not be drawn.</p>
      ) : (
        <div ref={host} className="overflow-x-auto [&_svg]:h-auto [&_svg]:max-w-full" />
      )}
    </section>
  );
};
