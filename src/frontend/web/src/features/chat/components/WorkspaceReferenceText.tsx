import React from 'react';

const inlineWorkspaceReferencePattern = /(^|[\s([{\n])(#[^\s#[{\n]+)/g;

export const WorkspaceReferenceText: React.FC<{ text: string }> = ({ text }) => {
  const segments: React.ReactNode[] = [];
  let cursor = 0;

  for (const match of text.matchAll(inlineWorkspaceReferencePattern)) {
    const prefix = match[1] ?? '';
    const reference = match[2] ?? '';
    const referenceStart = (match.index ?? 0) + prefix.length;

    if (referenceStart > cursor) {
      segments.push(text.slice(cursor, referenceStart));
    }

    segments.push(
      <span
        key={`${referenceStart}:${reference}`}
        className="rounded-sm bg-black/10 px-0.5 font-medium dark:bg-white/15"
      >
        {reference}
      </span>
    );
    cursor = referenceStart + reference.length;
  }

  if (cursor < text.length) {
    segments.push(text.slice(cursor));
  }

  return <>{segments}</>;
};
