import { lazy, Suspense, type ComponentProps } from 'react';
import type { ImportModal as ImportDialog } from './ImportModal';
import type { ConnectorFormModal as LocalDialog } from './ConnectorFormModal';

const ImportEditor = lazy(() =>
  import('./ImportModal').then((module) => ({ default: module.ImportModal }))
);
const LocalEditor = lazy(() =>
  import('./ConnectorFormModal').then((module) => ({ default: module.ConnectorFormModal }))
);

export function ImportModal(props: ComponentProps<typeof ImportDialog>) {
  return (
    <Suspense fallback={<p role="status">Loading connector import…</p>}>
      <ImportEditor {...props} />
    </Suspense>
  );
}

export function ConnectorFormModal(props: ComponentProps<typeof LocalDialog>) {
  return (
    <Suspense fallback={<p role="status">Loading connector setup…</p>}>
      <LocalEditor {...props} />
    </Suspense>
  );
}
