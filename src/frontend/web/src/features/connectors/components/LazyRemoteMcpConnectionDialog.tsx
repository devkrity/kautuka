import { lazy, Suspense, type ComponentProps } from 'react';
import type { RemoteMcpConnectionDialog as RemoteDialog } from './RemoteMcpConnectionDialog';

const Editor = lazy(() =>
  import('./RemoteMcpConnectionDialog').then((module) => ({
    default: module.RemoteMcpConnectionDialog,
  }))
);

export function RemoteMcpConnectionDialog(props: ComponentProps<typeof RemoteDialog>) {
  return (
    <Suspense fallback={<p role="status">Loading remote connector setup…</p>}>
      <Editor {...props} />
    </Suspense>
  );
}
