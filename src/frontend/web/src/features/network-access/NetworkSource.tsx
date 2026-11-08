import {
  identityContext,
  identityDetails,
  identityName,
  type NetworkIdentity,
} from './networkAccess';

export function NetworkSource({ identity }: { identity: NetworkIdentity }) {
  return (
    <div className="min-w-0 space-y-1">
      <p className="font-semibold break-words">{identityName(identity)}</p>
      <p className="text-sm text-muted-foreground break-words">{identityContext(identity)}</p>
      <details className="text-xs text-muted-foreground">
        <summary className="cursor-pointer">Technical identity</summary>
        <p className="mt-1 break-all font-mono">{identityDetails(identity)}</p>
      </details>
    </div>
  );
}
