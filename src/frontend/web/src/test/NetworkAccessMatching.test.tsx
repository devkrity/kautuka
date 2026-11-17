// @vitest-environment jsdom
import React from 'react';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { PendingApprovalCard } from '@/features/network-access/components/PendingApprovalCard';
import type { NetworkAccessRequestDto } from '@/features/network-access/networkAccess';
import { UserNetworkPrivilegesForm } from '@/features/network-access/components/UserNetworkPrivilegesForm';
import { RulesList } from '@/features/network-access/components/RulesList';
import { RuleScopeEditor } from '@/features/network-access/components/RuleScopeEditor';
import { GroupedPendingCards } from '@/features/network-access/components/GroupedPendingCards';
import type { NetworkAccessRuleDto } from '@/features/network-access/networkAccess';

vi.mock('@/features/settings/api/queries', () => ({ useAgents: () => ({ data: [] }) }));
vi.mock('@/features/connectors/api/queries', () => ({ useConnectors: () => ({ data: [{ id: 'web', name: 'Web Tools', scope: 'user', ownerUserId: 'alice' }] }) }));

describe('network access user scopes', () => {
  it('uses the revision returned by each decision when allowing a group', async () => {
    const request: NetworkAccessRequestDto = {
      id: 'one', userId: 'alice', agentId: 'agent-1', agentDisplayName: 'My Assistant',
      systemwide: false, attribution: 'user-agent', destination: 'one.example', destKind: 'host',
      port: 443, actorKind: 'agent', firstSeenUtc: '2026-01-01T00:00:00Z',
      lastSeenUtc: '2026-01-01T00:00:00Z', attempts: 1, status: 'pending', origin: 'container',
    };
    const onDecide = vi.fn().mockImplementation(async (decision: { expectedRevision: number }) => {
      const expected = 4 + onDecide.mock.calls.length - 1;
      if (decision.expectedRevision !== expected) throw new Error('revision conflict');
      return { revision: expected + 1 };
    });
    render(<GroupedPendingCards requests={[request, { ...request, id: 'two', destination: 'two.example' }]}
      currentUser={{ id: 'alice', role: 'user' }} userPrivileges={{ canApproveOwn: true }} expectedRevision={4} onDecide={onDecide} />);
    fireEvent.click(screen.getByRole('button', { name: 'Allow all 2' }));
    await waitFor(() => expect(onDecide).toHaveBeenCalledTimes(2));
    expect(onDecide).toHaveBeenNthCalledWith(2, expect.objectContaining({ expectedRevision: 5 }), 'two');
  });
  it('stops the group when a decision fails', async () => {
    const request: NetworkAccessRequestDto = {
      id: 'one', userId: 'alice', agentId: 'agent-1', agentDisplayName: 'My Assistant',
      systemwide: false, attribution: 'user-agent', destination: 'one.example', destKind: 'host',
      port: 443, actorKind: 'agent', firstSeenUtc: '2026-01-01T00:00:00Z',
      lastSeenUtc: '2026-01-01T00:00:00Z', attempts: 1, status: 'pending', origin: 'container',
    };
    const onDecide = vi.fn().mockRejectedValue(new Error('revision conflict'));
    render(<GroupedPendingCards requests={[request, { ...request, id: 'two', destination: 'two.example' }]}
      currentUser={{ id: 'alice', role: 'user' }} userPrivileges={{ canApproveOwn: true }} expectedRevision={4} onDecide={onDecide} />);
    fireEvent.click(screen.getByRole('button', { name: 'Allow all 2' }));
    await waitFor(() => expect(onDecide).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Allow all 2' })).toBeEnabled());
  });
  it('explains each approval scope without exposing runtime connector IDs', async () => {
    const request: NetworkAccessRequestDto = {
      id: 'request-scopes', userId: 'alice', userDisplayName: 'Alice', agentId: 'agent-1',
      agentDisplayName: 'My Assistant', connectorId: 'alice::web', connectorDisplayName: 'Web Tools',
      systemwide: true, attribution: 'user-agent', destination: 'www.bbc.com', destKind: 'host',
      port: 443, actorKind: 'connector', firstSeenUtc: '2026-01-01T00:00:00Z',
      lastSeenUtc: '2026-01-01T00:00:00Z', attempts: 1, status: 'pending', origin: 'container',
    };
    const onDecide = vi.fn().mockResolvedValue(undefined);
    render(<PendingApprovalCard request={request} currentUser={{ id: 'alice', role: 'user' }}
      userPrivileges={{ canApproveOwn: true }} expectedRevision={4} onDecide={onDecide} />);
    expect(screen.getByRole('button', { name: 'Allow My Assistant to access this site' })).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Allow this site whenever I use Web Tools' }));
    await waitFor(() => expect(onDecide).toHaveBeenCalledWith(expect.objectContaining({ scope: 'connector' })));
    expect(screen.getByRole('button', { name: 'Allow this site for everything I use' })).toBeInTheDocument();
    expect(screen.queryByText(/alice::web/)).not.toBeInTheDocument();
  });
  it('offers one permission to manage own network access', () => {
    const save = vi.fn();
    render(<UserNetworkPrivilegesForm canApproveOwn={false} isAdminViewer onSave={save} />);
    fireEvent.click(screen.getByRole('checkbox', { name: 'Can manage their own network access' }));
    expect(save).toHaveBeenCalledWith(expect.objectContaining({ canApproveOwn: true }));
    expect(screen.getAllByRole('checkbox')).toHaveLength(1);
  });

  it('allows a user to edit the scope of their connector rule', () => {
    const rule: NetworkAccessRuleDto = {
      id: 'rule-1', scopeKind: 'user', userId: 'alice', connectorId: 'web',
      destination: 'example.com', destKind: 'host', port: 443, allowed: true,
      allowLan: false, source: 'user', paused: false, summaryText: '', decidedBy: 'alice',
      decidedAtUtc: '2026-01-01T00:00:00Z', openInternetBadge: false,
    };
    const edit = vi.fn();
    render(<RulesList rules={[rule]} expectedRevision={4} currentUser={{ id: 'alice', role: 'user' }}
      onDeleteRule={vi.fn()} onEditRule={edit} canManageOwn />);
    fireEvent.click(screen.getByRole('button', { name: 'Edit' }));
    expect(edit).toHaveBeenCalledWith(rule);
  });

  it('submits a scope change as one replacement of the existing rule', async () => {
    const rule: NetworkAccessRuleDto = {
      id: 'rule-1', scopeKind: 'user', userId: 'alice', connectorId: 'web',
      destination: 'example.com', destKind: 'host', port: 443, allowed: true,
      allowLan: false, source: 'user', paused: false, summaryText: '', decidedBy: 'alice',
      decidedAtUtc: '2026-01-01T00:00:00Z', openInternetBadge: false,
    };
    const save = vi.fn().mockResolvedValue(undefined);
    render(<RuleScopeEditor rule={rule} userId="alice" isAdmin={false} expectedRevision={4} onCancel={vi.fn()} onSave={save} />);
    fireEvent.change(screen.getByLabelText('Applies to'), { target: { value: 'user' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save rule' }));
    await waitFor(() => expect(save).toHaveBeenCalledWith(expect.objectContaining({ scopeKind: 'user', userId: 'alice', connectorId: undefined, replaceRuleId: 'rule-1', expectedRevision: 4 })));
  });

  it('resets editor choices when switching to another rule', async () => {
    const rule: NetworkAccessRuleDto = {
      id: 'rule-one', scopeKind: 'user', userId: 'alice', connectorId: 'web',
      destination: 'one.example', destKind: 'host', port: 443, allowed: true,
      allowLan: false, source: 'user', paused: false, summaryText: '', decidedBy: 'alice',
      decidedAtUtc: '2026-01-01T00:00:00Z', openInternetBadge: false,
    };
    const save = vi.fn().mockResolvedValue(undefined);
    const editor = (value: NetworkAccessRuleDto) => <RuleScopeEditor rule={value} userId="alice" isAdmin={false} expectedRevision={4} onCancel={vi.fn()} onSave={save} />;
    const { rerender } = render(editor(rule));
    fireEvent.change(screen.getByLabelText('Decision'), { target: { value: 'block' } });
    rerender(editor({ ...rule, id: 'rule-two', destination: 'two.example', connectorId: undefined, allowed: true }));
    expect(screen.getByLabelText('Applies to')).toHaveValue('user');
    expect(screen.getByLabelText('Decision')).toHaveValue('allow');
    fireEvent.click(screen.getByRole('button', { name: 'Save rule' }));
    await waitFor(() => expect(save).toHaveBeenCalledWith(expect.objectContaining({ replaceRuleId: 'rule-two', allowed: true, scopeKind: 'user' })));
  });

  it('lets the owner approve their connector without agent context regardless of availability', async () => {
    const request: NetworkAccessRequestDto = {
      id: 'request-1', userId: 'alice', connectorId: 'web', systemwide: true,
      attribution: 'user', destination: 'example.com', destKind: 'host', port: 443,
      actorKind: 'connector', firstSeenUtc: '2026-01-01T00:00:00Z',
      lastSeenUtc: '2026-01-01T00:00:00Z', attempts: 1, status: 'pending', origin: 'container',
    };
    const onDecide = vi.fn().mockResolvedValue(undefined);
    render(<PendingApprovalCard request={request} currentUser={{ id: 'alice', role: 'user' }}
      userPrivileges={{ canApproveOwn: true }} expectedRevision={4} onDecide={onDecide} />);
    fireEvent.click(screen.getByRole('button', { name: 'Allow access' }));
    await waitFor(() => expect(onDecide).toHaveBeenCalledWith(expect.objectContaining({ allow: true, scope: 'connector', expectedRevision: 4 })));
    expect(screen.queryByText(/Waiting for an administrator/i)).not.toBeInTheDocument();
  });
});
