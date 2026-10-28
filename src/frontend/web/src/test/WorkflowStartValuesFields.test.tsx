import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { WorkflowStartValuesFields } from '@/features/autopilot/components/WorkflowStartValuesFields';
import { fireEvent, render, screen } from '@testing-library/react';
import React from 'react';
import { describe, expect, it } from 'vitest';

describe('WorkflowStartValuesFields', () => {
  it('keeps array fields as text inputs', () => {
    render(
      <QueryClientProvider client={new QueryClient()}>
        <WorkflowStartValuesFields
          inputSchema={{ properties: { items: { type: 'array', items: { type: 'string' } } } }}
          value="{}"
          disabled={false}
          onChange={() => undefined}
        />
      </QueryClientProvider>
    );

    expect(screen.getByLabelText('Items')).toHaveAttribute('type', 'text');
  });

  it('capitalizes only the first letter of untitled field labels', () => {
    render(
      <QueryClientProvider client={new QueryClient()}>
        <WorkflowStartValuesFields
          inputSchema={{
            properties: { priority: { type: 'string' }, dueDate: { type: 'string' } },
          }}
          value="{}"
          disabled={false}
          onChange={() => undefined}
        />
      </QueryClientProvider>
    );
    expect(screen.getByLabelText('Priority')).toBeInTheDocument();
    expect(screen.getByLabelText('Due date')).toBeInTheDocument();
  });

  it('shows field titles and a select for pick-list values', () => {
    let nextValue = '{}';
    render(
      <QueryClientProvider client={new QueryClient()}>
        <WorkflowStartValuesFields
          inputSchema={{
            properties: {
              receivedAfter: { type: 'string', title: 'Received after' },
              recommendation: { type: 'string', title: 'Recommendation', enum: ['lodge', 'hold'] },
            },
          }}
          value={nextValue}
          disabled={false}
          onChange={(value) => {
            nextValue = value;
          }}
        />
      </QueryClientProvider>
    );
    expect(screen.getByLabelText('Received after')).toBeInTheDocument();
    const select = screen.getByLabelText('Recommendation');
    expect(select.tagName).toBe('SELECT');
    fireEvent.change(select, { target: { value: 'hold' } });
    expect(nextValue).toContain('"recommendation":"hold"');
  });
});
