export interface ParsedServiceFinding { readonly service: string; readonly level: 'warning' | 'error'; readonly message: string; }

const structuredHeader = /^\s*(?:\[[^\]]+\]\s*)?(warn(?:ing)?|fail(?:ure)?|crit(?:ical)?|error)\s*:/i;
const anyLevelHeader = /^\s*(?:\[[^\]]+\]\s*)?(?:trace|debug|info(?:rmation)?|warn(?:ing)?|fail(?:ure)?|crit(?:ical)?|error)\s*:/i;
const secret = /\b(?:authorization|cookie|credential|password|secret|token|api[-_]?key)\s*[=:]\s*\S+|\bbearer\s+\S+/gi;

/** Projects structured service failures without treating message text as a severity signal. */
export function parseServiceFindings(service: string, output: string): ParsedServiceFinding[] {
  const lines = output.split(/\r?\n/);
  const findings: ParsedServiceFinding[] = [];
  for (let index = 0; index < lines.length; index += 1) {
    const header = structuredHeader.exec(lines[index]);
    if (!header) continue;
    const context = [sanitize(lines[index])];
    for (let next = index + 1; next < lines.length && context.length < 3 && !anyLevelHeader.test(lines[next]); next += 1) {
      if (lines[next].trim()) context.push(sanitize(lines[next]));
    }
    findings.push({ service, level: /warn/i.test(header[1]) ? 'warning' : 'error', message: context.join('\n') });
  }
  return findings.slice(-50);
}

function sanitize(line: string): string {
  return line.replace(secret, '[REDACTED]').trim().slice(0, 800);
}
