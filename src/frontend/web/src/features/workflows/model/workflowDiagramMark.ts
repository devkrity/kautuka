interface ChoiceEvent {
  type: string;
  from?: string;
  to?: string;
  op?: string;
  caseIndex?: number;
}

/** Same id the diagram comment and the choice executor already share: edge-{from}-{to}-{caseIndex}. */
export function takenChoiceIds(events: readonly ChoiceEvent[]): ReadonlySet<string> {
  const taken = new Set<string>();
  for (const event of events) {
    if (
      event.type !== 'workflow.edge.taken' ||
      !event.from ||
      !event.to ||
      typeof event.caseIndex !== 'number'
    ) {
      continue;
    }
    taken.add(`edge-${event.from}-${event.to}-${event.caseIndex}`);
  }
  return taken;
}

export function markTakenChoices(chart: string, taken: ReadonlySet<string>): string {
  if (taken.size === 0) {
    return chart;
  }
  const styles: string[] = [];
  for (const line of chart.split('\n')) {
    const match = /^%% choice (\d+) (\S+)$/.exec(line.trim());
    if (!match || !taken.has(match[2]!)) {
      continue;
    }
    styles.push(`  linkStyle ${match[1]} stroke:#1b7f4e,stroke-width:2.5px`);
  }
  if (styles.length === 0) {
    return chart;
  }
  return `${chart}\n${styles.join('\n')}`;
}
