import React from 'react';
import { Alert, AlertDescription } from '@/components/ui/alert';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';
import { Checkbox } from '@/components/ui/checkbox';
import { Label } from '@/components/ui/label';
import { Select } from '@/components/ui/select';

export const ObservabilitySection: React.FC<{
  captureMessageContent: boolean;
  logLevel: string;
  observabilityIncludeBinaryContent: boolean;
  effectiveLogLevel: string;
  saving?: boolean;
  showSaveAction?: boolean;
  onCaptureMessageContentChange(value: boolean): void;
  onLogLevelChange(value: string): void;
  onSave?(): void;
}> = ({
  captureMessageContent,
  logLevel,
  observabilityIncludeBinaryContent,
  effectiveLogLevel,
  saving = false,
  showSaveAction = true,
  onCaptureMessageContentChange,
  onLogLevelChange,
  onSave,
}) => (
  <Card className="card-solid">
    <CardHeader>
      <CardTitle>Observability</CardTitle>
      <CardDescription>
        Control what telemetry data is emitted to diagnostic backends.
      </CardDescription>
    </CardHeader>
    <CardContent className="space-y-4">
      <div className="rounded-lg border border-border/80 p-3">
        <div className="flex items-start gap-3">
          <Checkbox
            checked={captureMessageContent}
            onChange={(e) => onCaptureMessageContentChange(e.target.checked)}
          />
          <div className="space-y-1">
            <Label className="font-medium">Include all raw message in traces and logs</Label>
            <p className="text-xs font-semibold text-destructive">
              Danger: this records full prompts and model outputs, including secrets, credentials,
              personal data, and regulated content. Keep off by default and enable only for short,
              controlled troubleshooting windows.
            </p>
          </div>
        </div>
      </div>

      <div className="space-y-2">
        <Label htmlFor="system-log-level">System log level</Label>
        <Select
          id="system-log-level"
          value={logLevel}
          onChange={(e) => onLogLevelChange(String(e.target.value || 'DEBUG').toUpperCase())}
        >
          <option value="DEBUG">DEBUG</option>
          <option value="INFO">INFO</option>
          <option value="WARNING">WARNING</option>
          <option value="ERROR">ERROR</option>
          <option value="CRITICAL">CRITICAL</option>
          <option value="NOTSET">NOTSET</option>
        </Select>
        <p className="text-xs text-muted-foreground">
          Increase to DEBUG when diagnosing startup and runtime orchestration behavior.
        </p>
      </div>

      <div className="grid grid-cols-1 gap-3 text-sm text-muted-foreground md:grid-cols-2">
        <div>
          <p className="font-medium text-foreground">Include Binary Content</p>
          <p>{observabilityIncludeBinaryContent ? 'enabled' : 'disabled'}</p>
        </div>
        <div>
          <p className="font-medium text-foreground">Raw Message Capture</p>
          <p>{captureMessageContent ? 'enabled' : 'disabled'}</p>
        </div>
        <div>
          <p className="font-medium text-foreground">System Log Level</p>
          <p>{effectiveLogLevel}</p>
        </div>
      </div>

      {showSaveAction ? (
        <div className="flex justify-end">
          <Button onClick={onSave} disabled={saving}>
            {saving ? 'Saving...' : 'Save Advanced Settings'}
          </Button>
        </div>
      ) : null}
    </CardContent>
  </Card>
);
