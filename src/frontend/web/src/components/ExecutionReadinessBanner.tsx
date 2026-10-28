import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert';
import { useExecutionReadiness } from '@/features/dashboard';
import { AlertTriangle } from 'lucide-react';

export interface ExecutionReadinessBannerProps {
  authenticated: boolean;
}

function formatDependencyName(dependency: string): string {
  switch (dependency.trim().toLowerCase()) {
    case 'manager':
      return 'Manager';
    case 'credentials_host':
    case 'credentialshost':
      return 'CredentialsHost';
    case 'container_runtime':
    case 'podman':
      return 'Container runtime';
    default:
      return dependency;
  }
}

export function ExecutionReadinessBanner({ authenticated }: ExecutionReadinessBannerProps) {
  const readiness = useExecutionReadiness(authenticated);

  if (!authenticated || (readiness.data?.ready === true && !readiness.isError)) {
    return null;
  }

  if (!readiness.data && !readiness.isError) {
    return null;
  }

  const failedDependencies = readiness.data?.dependencies.filter((item) => !item.ready) ?? [];
  const message = readiness.isError
    ? 'Kautuka could not verify the services required to run agents.'
    : readiness.data?.message;
    const administratorGuidanceAlreadyPresent = [
    message,
    ...failedDependencies.map((dependency) => dependency.message),
  ].some((value) =>
    /check that (?:the )?(?:manager|credentialshost)|install rootless podman|restore credentialshost/i.test(
      value ?? ''
    )
  );

  return (
    <div className="px-3 pt-3 sm:px-6">
      <Alert variant="destructive" role="alert" aria-live="polite">
        <div className="flex min-w-0 gap-3">
          <div className="mt-0.5 rounded-full bg-destructive/15 p-2">
            <AlertTriangle className="h-4 w-4" aria-hidden="true" />
          </div>
          <div className="min-w-0">
            <AlertTitle>Agent runs are unavailable</AlertTitle>
            <AlertDescription>
              <p>
                {message}
                {!administratorGuidanceAlreadyPresent
                  ? ' Restore the services listed below, then retry a chat.'
                  : ''}
              </p>
              {failedDependencies.length > 0 ? (
                <ul className="mt-2 list-disc space-y-1 pl-5">
                  {failedDependencies.map((dependency) => (
                    <li key={dependency.dependency}>
                      <span className="font-medium">
                        {formatDependencyName(dependency.dependency)}:
                      </span>{' '}
                      {dependency.message}
                    </li>
                  ))}
                </ul>
              ) : null}
            </AlertDescription>
          </div>
        </div>
      </Alert>
    </div>
  );
}
