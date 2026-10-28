import { PageState } from '@/components/layout/PageState';
import { StatusPanels } from '@/components/layout/StatusPanels';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Select } from '@/components/ui/select';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { useAdvancedTab } from '../hooks/useAdvancedTab';
import { ObservabilitySection } from './ObservabilitySection';
import { PersonaDefinitionsSection } from './PersonaDefinitionsSection';
import { Banner } from './SettingsShared';
import { WorkspaceAndSandboxSection } from './WorkspaceAndSandboxSection';
import { PackageCacheStatsSection } from './PackageCacheStatsSection';

const structuredJsonStrictPromotionThresholdLabel = 'Structured JSON strict promotion threshold';
const structuredJsonStrictPromotionThresholdId = 'structured-json-strict-promotion-threshold';
const structuredJsonMinObservationsLabel = 'Structured JSON min observations';
const structuredJsonMinObservationsId = 'structured-json-min-observations';
const providerPathFallbackModeLabel = 'Provider path fallback mode enabled';
const providerPathFallbackModeId = 'provider-path-fallback-mode-enabled';

export function AdvancedTab() {
  const {
    loading,
    saving,
    result,
    captureMessageContent,
    logLevel,
    releaseChannel,
    locale,
    timeZone,
    modelTimeoutSeconds,
    maxRunDurationSeconds,
    hitlExpirySeconds,
    hitlExpiryAction,
    allowMultipleToolCalls,
    allowConcurrentInvocation,
    maxParallelTools,
    sandboxImage,
    sandboxCpus,
    sandboxMemory,
    sandboxTimeout,
    documentIngestionEnabled,
    internalParserConnectorId,
    internalParserToolName,
    parserTimeoutSeconds,
    parseRequiredExtensions,
    pdfImageFallbackEnabled,
    visionRouteAllowed,
    personaStyles,
    personaTones,
    personaVerbosityLevels,
    providerTypeCatalog,
    toolNativePromotionThreshold,
    toolMinObservations,
    toolNativeDemotionThreshold,
    structuredJsonStrictPromotionThreshold,
    structuredJsonMinObservations,
    visionEnableThreshold,
    observationDecayWindowDays,
    providerPathFallbackModeEnabled,
    embeddingsEnabled,
    factPersistThreshold,
    factInjectThreshold,
    maxInjectedFacts,
    injectionTokenBudget: memoryInjectionBudget,
    sessionCompactionModelId,
    systemSettings,
    kautukaSettings,
    statusItems,
    dirtyTabs,
    dirtyTabCount,
    hasUnsavedChanges,
    setCaptureMessageContent,
    setLogLevel,
    setReleaseChannel,
    setLocale,
    setTimeZone,
    setModelTimeoutSeconds,
    setMaxRunDurationSeconds,
    setHitlExpirySeconds,
    setHitlExpiryAction,
    setAllowMultipleToolCalls,
    setAllowConcurrentInvocation,
    setMaxParallelTools,
    setSandboxImage,
    setSandboxCpus,
    setSandboxMemory,
    setSandboxTimeout,
    setDocumentIngestionEnabled,
    setInternalParserConnectorId,
    setInternalParserToolName,
    setParserTimeoutSeconds,
    setParseRequiredExtensions,
    setPdfImageFallbackEnabled,
    setVisionRouteAllowed,
    setPersonaStyles,
    setPersonaTones,
    setPersonaVerbosityLevels,
    setProviderTypeCatalog,
    setToolNativePromotionThreshold,
    setToolMinObservations,
    setToolNativeDemotionThreshold,
    setStructuredJsonStrictPromotionThreshold,
    setStructuredJsonMinObservations,
    setVisionEnableThreshold,
    setObservationDecayWindowDays,
    setProviderPathFallbackModeEnabled,
    setEmbeddingsEnabled,
    setFactPersistThreshold,
    setFactInjectThreshold,
    setMaxInjectedFacts,
    setInjectionTokenBudget: setMemoryInjectionBudget,
    setSessionCompactionModelId,
    save,
    resetChanges,
  } = useAdvancedTab();

  const runAsync = (action: () => Promise<unknown>) => {
    action().catch((error) => {
      console.error('Advanced settings action failed:', error);
    });
  };

  return (
    <div className="space-y-4">
      {result && <Banner success={result.success} message={result.message} />}

      {loading ? (
        <PageState message="Loading advanced system settings..." minHeightClassName="min-h-36" />
      ) : (
        <>
          <div className="sticky top-0 z-10 rounded-lg border border-border bg-background/95 p-3 backdrop-blur supports-[backdrop-filter]:bg-background/80">
            <div className="flex flex-col gap-3 md:flex-row md:items-center md:justify-between">
              <div>
                <div className="text-sm font-medium">Advanced Settings</div>
                <p className="text-xs text-muted-foreground">
                  {hasUnsavedChanges
                    ? `Unsaved changes across ${dirtyTabCount} tab${dirtyTabCount === 1 ? '' : 's'}.`
                    : 'All tabs are in sync with the last saved state.'}
                </p>
              </div>
              <div className="flex gap-2">
                <Button
                  variant="outline"
                  onClick={() => runAsync(async () => resetChanges())}
                  disabled={!hasUnsavedChanges || saving}
                >
                  Discard Changes
                </Button>
                <Button onClick={() => runAsync(save)} disabled={!hasUnsavedChanges || saving}>
                  {saving ? 'Saving...' : 'Save All Changes'}
                </Button>
              </div>
            </div>
          </div>

          <Tabs defaultValue="workspace" className="mt-1">
            <TabsList className="mb-4 flex h-auto w-full flex-wrap justify-start gap-1 rounded-xl border border-border/70 bg-muted/40 p-1">
              <TabsTrigger
                className="rounded-lg border border-transparent px-3 py-2 text-sm data-[state=active]:border-border data-[state=active]:bg-card data-[state=active]:text-foreground"
                value="workspace"
              >
                <TabLabel label="Workspace" dirty={dirtyTabs.workspace} />
              </TabsTrigger>
              <TabsTrigger
                className="rounded-lg border border-transparent px-3 py-2 text-sm data-[state=active]:border-border data-[state=active]:bg-card data-[state=active]:text-foreground"
                value="document-ingestion"
              >
                <TabLabel label="Document Ingestion" dirty={dirtyTabs['document-ingestion']} />
              </TabsTrigger>
              <TabsTrigger
                className="rounded-lg border border-transparent px-3 py-2 text-sm data-[state=active]:border-border data-[state=active]:bg-card data-[state=active]:text-foreground"
                value="updates"
              >
                <TabLabel label="Updates" dirty={dirtyTabs.updates} />
              </TabsTrigger>
              <TabsTrigger
                className="rounded-lg border border-transparent px-3 py-2 text-sm data-[state=active]:border-border data-[state=active]:bg-card data-[state=active]:text-foreground"
                value="runtime"
              >
                <TabLabel label="Runtime" dirty={dirtyTabs.runtime} />
              </TabsTrigger>
              <TabsTrigger
                className="rounded-lg border border-transparent px-3 py-2 text-sm data-[state=active]:border-border data-[state=active]:bg-card data-[state=active]:text-foreground"
                value="observability"
              >
                <TabLabel label="Observability" dirty={dirtyTabs.observability} />
              </TabsTrigger>
              <TabsTrigger
                className="rounded-lg border border-transparent px-3 py-2 text-sm data-[state=active]:border-border data-[state=active]:bg-card data-[state=active]:text-foreground"
                value="persona"
              >
                <TabLabel label="Persona" dirty={dirtyTabs.persona} />
              </TabsTrigger>
              <TabsTrigger
                className="rounded-lg border border-transparent px-3 py-2 text-sm data-[state=active]:border-border data-[state=active]:bg-card data-[state=active]:text-foreground"
                value="provider-catalog"
              >
                <TabLabel label="Provider Catalog" dirty={dirtyTabs['provider-catalog']} />
              </TabsTrigger>
              <TabsTrigger
                className="rounded-lg border border-transparent px-3 py-2 text-sm data-[state=active]:border-border data-[state=active]:bg-card data-[state=active]:text-foreground"
                value="provider-path"
              >
                <TabLabel label="Provider Path" dirty={dirtyTabs['provider-path']} />
              </TabsTrigger>
              <TabsTrigger
                className="rounded-lg border border-transparent px-3 py-2 text-sm data-[state=active]:border-border data-[state=active]:bg-card data-[state=active]:text-foreground"
                value="memory"
              >
                <TabLabel label="Memory" dirty={dirtyTabs.memory} />
              </TabsTrigger>
            </TabsList>

            <TabsContent value="workspace" className="space-y-4">
              <WorkspaceAndSandboxSection
                kautukaSettings={kautukaSettings}
                sandboxImage={sandboxImage}
                sandboxCpus={sandboxCpus}
                sandboxMemory={sandboxMemory}
                sandboxTimeout={sandboxTimeout}
                documentIngestionEnabled={documentIngestionEnabled}
                internalParserConnectorId={internalParserConnectorId}
                internalParserToolName={internalParserToolName}
                parserTimeoutSeconds={parserTimeoutSeconds}
                parseRequiredExtensions={parseRequiredExtensions}
                pdfImageFallbackEnabled={pdfImageFallbackEnabled}
                visionRouteAllowed={visionRouteAllowed}
                onSandboxImageChange={setSandboxImage}
                onSandboxCpusChange={setSandboxCpus}
                onSandboxMemoryChange={setSandboxMemory}
                onSandboxTimeoutChange={setSandboxTimeout}
                onDocumentIngestionEnabledChange={setDocumentIngestionEnabled}
                onInternalParserConnectorIdChange={setInternalParserConnectorId}
                onInternalParserToolNameChange={setInternalParserToolName}
                onParserTimeoutSecondsChange={setParserTimeoutSeconds}
                onParseRequiredExtensionsChange={setParseRequiredExtensions}
                onPdfImageFallbackEnabledChange={setPdfImageFallbackEnabled}
                onVisionRouteAllowedChange={setVisionRouteAllowed}
                hideDocumentIngestion
              />
              <PackageCacheStatsSection />
            </TabsContent>

            <TabsContent value="document-ingestion" className="space-y-4">
              <DocumentIngestionTab
                documentIngestionEnabled={documentIngestionEnabled}
                internalParserConnectorId={internalParserConnectorId}
                internalParserToolName={internalParserToolName}
                parserTimeoutSeconds={parserTimeoutSeconds}
                parseRequiredExtensions={parseRequiredExtensions}
                pdfImageFallbackEnabled={pdfImageFallbackEnabled}
                visionRouteAllowed={visionRouteAllowed}
                onDocumentIngestionEnabledChange={setDocumentIngestionEnabled}
                onInternalParserConnectorIdChange={setInternalParserConnectorId}
                onInternalParserToolNameChange={setInternalParserToolName}
                onParserTimeoutSecondsChange={setParserTimeoutSeconds}
                onParseRequiredExtensionsChange={setParseRequiredExtensions}
                onPdfImageFallbackEnabledChange={setPdfImageFallbackEnabled}
                onVisionRouteAllowedChange={setVisionRouteAllowed}
              />
            </TabsContent>

            <TabsContent value="updates" className="space-y-4">
              <div className="rounded border border-border p-4">
                <div className="mb-3 text-sm font-medium">Update channel</div>
                <div className="flex items-end gap-3">
                  <div className="w-56">
                    <label
                      className="mb-1 block text-xs text-muted-foreground"
                      htmlFor="update-track"
                    >
                      Update channel
                    </label>
                    <Select
                      id="update-track"
                      value={releaseChannel}
                      onChange={(event) =>
                        setReleaseChannel(event.target.value === 'preview' ? 'preview' : 'stable')
                      }
                      options={[
                        { value: 'preview', label: 'Preview' },
                        { value: 'stable', label: 'Stable' },
                      ]}
                    />
                    <p className="mt-1 text-xs text-muted-foreground">
                      {releaseChannel === 'preview'
                        ? 'Preview builds are for testing new features early and may have bugs.'
                        : 'Switching tracks never downgrades. Stable waits for a newer stable release.'}
                    </p>
                  </div>
                </div>
              </div>
            </TabsContent>

            <TabsContent value="runtime" className="space-y-4">
              <div className="rounded border border-border p-4">
                <div className="mb-3 text-sm font-medium">Runtime Grounding</div>
                <div className="grid gap-3 md:grid-cols-2">
                  <Field label="Locale" id="runtime-grounding-locale">
                    <Input
                      id="runtime-grounding-locale"
                      value={locale}
                      onChange={(event) => setLocale(event.target.value)}
                      placeholder="en-US"
                    />
                  </Field>
                  <Field label="Time zone" id="runtime-grounding-timezone">
                    <Input
                      id="runtime-grounding-timezone"
                      value={timeZone}
                      onChange={(event) => setTimeZone(event.target.value)}
                      placeholder="UTC"
                    />
                  </Field>
                </div>
                <p className="mt-2 text-xs text-muted-foreground">
                  These values are used to ground the agent with the current local date and time at
                  runtime.
                </p>
              </div>
              <div className="rounded border border-border p-4">
                <div className="mb-3 text-sm font-medium">Run limits</div>
                <div className="grid gap-3 md:grid-cols-2">
                  <Field label="Model timeout (seconds)" id="runtime-model-timeout-seconds">
                    <Input
                      id="runtime-model-timeout-seconds"
                      type="number"
                      min={1}
                      value={modelTimeoutSeconds}
                      onChange={(event) => setModelTimeoutSeconds(event.target.value)}
                    />
                  </Field>
                  <Field label="Max run duration (seconds)" id="runtime-max-run-duration-seconds">
                    <Input
                      id="runtime-max-run-duration-seconds"
                      type="number"
                      min={0}
                      value={maxRunDurationSeconds}
                      onChange={(event) => setMaxRunDurationSeconds(event.target.value)}
                    />
                  </Field>
                  <Field label="HITL expiry (seconds)" id="runtime-hitl-expiry-seconds">
                    <Input
                      id="runtime-hitl-expiry-seconds"
                      type="number"
                      min={0}
                      value={hitlExpirySeconds}
                      onChange={(event) => setHitlExpirySeconds(event.target.value)}
                    />
                  </Field>
                  <Field label="On HITL expiry" id="runtime-hitl-expiry-action">
                    <Select
                      id="runtime-hitl-expiry-action"
                      value={hitlExpiryAction}
                      onChange={(event) => {
                        const next = event.target.value;
                        setHitlExpiryAction(next === 'inform' || next === 'retry' ? next : 'abort');
                      }}
                      options={[
                        { value: 'abort', label: 'Abort' },
                        { value: 'inform', label: 'Inform' },
                        { value: 'retry', label: 'Retry once' },
                      ]}
                    />
                  </Field>
                </div>
                <p className="mt-2 text-xs text-muted-foreground">
                  Model timeout seeds provider HTTP when a provider does not set its own timeout. 0
                  on max run duration is infinite; 0 on HITL expiry means never. Expiry is not a
                  deny: abort stops the run, inform resumes with an expired tool result, and retry
                  issues one replacement interrupt.
                </p>
              </div>
              <div className="rounded border border-border p-4">
                <div className="mb-3 text-sm font-medium">Tool invocation</div>
                <div className="grid gap-3 md:grid-cols-2">
                  <div className="rounded border border-border/70 p-3">
                    <label
                      className="flex items-center gap-2 text-sm font-medium"
                      htmlFor="runtime-allow-multiple-tool-calls"
                    >
                      <input
                        id="runtime-allow-multiple-tool-calls"
                        type="checkbox"
                        checked={allowMultipleToolCalls}
                        onChange={(event) => setAllowMultipleToolCalls(event.target.checked)}
                      />
                      Allow multiple tool calls
                    </label>
                    <p className="mt-2 text-xs text-muted-foreground">
                      Lets the model request several tools in one turn. Turn this off on hosts that
                      should never fan out a single response into more than one call.
                    </p>
                  </div>
                  <div className="rounded border border-border/70 p-3">
                    <label
                      className="flex items-center gap-2 text-sm font-medium"
                      htmlFor="runtime-allow-concurrent-invocation"
                    >
                      <input
                        id="runtime-allow-concurrent-invocation"
                        type="checkbox"
                        checked={allowConcurrentInvocation}
                        disabled={!allowMultipleToolCalls}
                        onChange={(event) => setAllowConcurrentInvocation(event.target.checked)}
                      />
                      Allow concurrent invocation
                    </label>
                    <p className="mt-2 text-xs text-muted-foreground">
                      Starts independent tool calls together instead of one after another. Requires
                      multiple tool calls. Keep this off unless the tools on this host are safe to
                      run in parallel.
                    </p>
                  </div>
                  <Field label="Max parallel tools" id="runtime-max-parallel-tools">
                    <Input
                      id="runtime-max-parallel-tools"
                      type="number"
                      min={0}
                      max={64}
                      disabled={!allowMultipleToolCalls || !allowConcurrentInvocation}
                      value={maxParallelTools}
                      onChange={(event) => setMaxParallelTools(event.target.value)}
                    />
                  </Field>
                </div>
                <p className="mt-2 text-xs text-muted-foreground">
                  0 means no cap. 1 forces serial execution even when concurrent invocation is on.
                  The cap is per agent run and does not count human-approval waits. Maximum 64.
                  Agents can inherit or override these defaults on their Runtime tab.
                </p>
              </div>
            </TabsContent>

            <TabsContent value="observability" className="space-y-4">
              <ObservabilitySection
                captureMessageContent={captureMessageContent}
                logLevel={logLevel}
                observabilityIncludeBinaryContent={Boolean(
                  systemSettings?.observability?.includeBinaryContent
                )}
                effectiveLogLevel={systemSettings?.logLevel ?? logLevel}
                onCaptureMessageContentChange={setCaptureMessageContent}
                onLogLevelChange={setLogLevel}
                showSaveAction={false}
              />
            </TabsContent>

            <TabsContent value="persona" className="space-y-4">
              <PersonaDefinitionsSection
                styles={personaStyles}
                tones={personaTones}
                verbosityLevels={personaVerbosityLevels}
                onStylesChange={setPersonaStyles}
                onTonesChange={setPersonaTones}
                onVerbosityLevelsChange={setPersonaVerbosityLevels}
                showSaveAction={false}
              />
            </TabsContent>

            <TabsContent value="provider-catalog" className="space-y-4">
              <ProviderCatalogTab
                providerTypeCatalog={providerTypeCatalog}
                onChange={setProviderTypeCatalog}
              />
            </TabsContent>

            <TabsContent value="provider-path" className="space-y-4">
              <div className="rounded border border-border p-4">
                <div className="mb-3 text-sm font-medium">Provider Path Policy</div>
                <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-4">
                  <Field
                    label="Tool native promotion threshold"
                    id="tool-native-promotion-threshold"
                  >
                    <Input
                      id="tool-native-promotion-threshold"
                      type="number"
                      min={0}
                      max={1}
                      step="0.01"
                      value={toolNativePromotionThreshold}
                      onChange={(e) => setToolNativePromotionThreshold(e.target.value)}
                    />
                  </Field>
                  <Field label="Tool min observations" id="tool-min-observations">
                    <Input
                      id="tool-min-observations"
                      type="number"
                      min={1}
                      value={toolMinObservations}
                      onChange={(e) => setToolMinObservations(e.target.value)}
                    />
                  </Field>
                  <Field label="Tool native demotion threshold" id="tool-native-demotion-threshold">
                    <Input
                      id="tool-native-demotion-threshold"
                      type="number"
                      min={0}
                      max={1}
                      step="0.01"
                      value={toolNativeDemotionThreshold}
                      onChange={(e) => setToolNativeDemotionThreshold(e.target.value)}
                    />
                  </Field>
                  <Field
                    label={structuredJsonStrictPromotionThresholdLabel}
                    id={structuredJsonStrictPromotionThresholdId}
                  >
                    <Input
                      id={structuredJsonStrictPromotionThresholdId}
                      type="number"
                      min={0}
                      max={1}
                      step="0.01"
                      value={structuredJsonStrictPromotionThreshold}
                      onChange={(e) => setStructuredJsonStrictPromotionThreshold(e.target.value)}
                    />
                  </Field>
                  <Field
                    label={structuredJsonMinObservationsLabel}
                    id={structuredJsonMinObservationsId}
                  >
                    <Input
                      id={structuredJsonMinObservationsId}
                      type="number"
                      min={1}
                      value={structuredJsonMinObservations}
                      onChange={(e) => setStructuredJsonMinObservations(e.target.value)}
                    />
                  </Field>
                  <Field label="Vision enable threshold" id="vision-enable-threshold">
                    <Input
                      id="vision-enable-threshold"
                      type="number"
                      min={0}
                      max={1}
                      step="0.01"
                      value={visionEnableThreshold}
                      onChange={(e) => setVisionEnableThreshold(e.target.value)}
                    />
                  </Field>
                  <Field label="Observation decay window days" id="observation-decay-window-days">
                    <Input
                      id="observation-decay-window-days"
                      type="number"
                      min={1}
                      value={observationDecayWindowDays}
                      onChange={(e) => setObservationDecayWindowDays(e.target.value)}
                    />
                  </Field>
                  <div className="rounded border border-border/70 p-3">
                    <label
                      className="flex items-center gap-2 text-sm font-medium"
                      htmlFor={providerPathFallbackModeId}
                    >
                      <input
                        id={providerPathFallbackModeId}
                        type="checkbox"
                        checked={providerPathFallbackModeEnabled}
                        onChange={(e) => setProviderPathFallbackModeEnabled(e.target.checked)}
                      />
                      {providerPathFallbackModeLabel}
                    </label>
                    <p className="mt-2 text-xs text-muted-foreground">
                      Allows runtime fallback when observations do not justify a stricter route.
                    </p>
                  </div>
                </div>
              </div>
            </TabsContent>

            <TabsContent value="memory" className="space-y-4">
              <div className="rounded border border-border p-4">
                <div className="mb-3 text-sm font-medium">Memory</div>
                <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-4">
                  <div className="rounded border border-border/70 p-3 md:col-span-2 xl:col-span-4">
                    <label
                      className="flex items-center gap-2 text-sm font-medium"
                      htmlFor="embeddings-enabled"
                    >
                      <input
                        id="embeddings-enabled"
                        type="checkbox"
                        checked={embeddingsEnabled}
                        onChange={(e) => setEmbeddingsEnabled(e.target.checked)}
                      />
                      Experimental embeddings
                    </label>
                    <p className="mt-2 text-xs text-muted-foreground">
                      When turned on, saved memory facts also get background vector embeddings for
                      experimental retrieval features. Turning this off still keeps normal fact
                      extraction and storage working.
                    </p>
                  </div>
                  <Field label="Fact persist threshold" id="fact-persist-threshold">
                    <Input
                      id="fact-persist-threshold"
                      type="number"
                      min={0}
                      max={1}
                      step="0.01"
                      value={factPersistThreshold}
                      onChange={(e) => setFactPersistThreshold(e.target.value)}
                    />
                  </Field>
                  <Field label="Fact inject threshold" id="fact-inject-threshold">
                    <Input
                      id="fact-inject-threshold"
                      type="number"
                      min={0}
                      max={1}
                      step="0.01"
                      value={factInjectThreshold}
                      onChange={(e) => setFactInjectThreshold(e.target.value)}
                    />
                  </Field>
                  <Field label="Max injected facts" id="max-injected-facts">
                    <Input
                      id="max-injected-facts"
                      type="number"
                      min={1}
                      value={maxInjectedFacts}
                      onChange={(e) => setMaxInjectedFacts(e.target.value)}
                    />
                  </Field>
                  <Field label="Injection budget" id="injection-budget">
                    <Input
                      id="injection-budget"
                      type="number"
                      min={1}
                      value={memoryInjectionBudget}
                      onChange={(e) => setMemoryInjectionBudget(e.target.value)}
                    />
                  </Field>
                  <Field label="Session compaction model" id="session-compaction-model-id">
                    <Input
                      id="session-compaction-model-id"
                      value={sessionCompactionModelId}
                      onChange={(e) => setSessionCompactionModelId(e.target.value)}
                      placeholder="Optional model id for summarization compaction"
                    />
                  </Field>
                </div>
              </div>
            </TabsContent>
          </Tabs>

          <StatusPanels items={statusItems} />
        </>
      )}
    </div>
  );
}

function ProviderCatalogTab(props: {
  providerTypeCatalog: Array<{
    type: string;
    friendlyName: string;
    defaultBaseUrl?: string | null;
    defaultHttpTimeoutSeconds?: number | null;
    requiresApiKey: boolean;
    requiresBaseUrl: boolean;
    supportsHttpTimeoutSeconds: boolean;
    isBuiltIn: boolean;
    seedVersion: number;
  }>;
  onChange(
    next: Array<{
      type: string;
      friendlyName: string;
      defaultBaseUrl?: string | null;
      defaultHttpTimeoutSeconds?: number | null;
      requiresApiKey: boolean;
      requiresBaseUrl: boolean;
      supportsHttpTimeoutSeconds: boolean;
      isBuiltIn: boolean;
      seedVersion: number;
    }>
  ): void;
}) {
  const updateEntry = (
    type: string,
    patch: Partial<{
      friendlyName: string;
      defaultBaseUrl?: string | null;
      defaultHttpTimeoutSeconds?: number | null;
    }>
  ) => {
    props.onChange(
      props.providerTypeCatalog.map((entry) =>
        entry.type === type
          ? {
              ...entry,
              ...patch,
            }
          : entry
      )
    );
  };

  return (
    <div className="rounded border border-border p-4">
      <div className="mb-3 text-sm font-medium">Provider Catalog Defaults</div>
      <p className="mb-4 text-xs text-muted-foreground">
        These defaults only affect providers created after the next save. Existing saved providers
        keep their own instance settings.
      </p>
      <div className="space-y-4">
        {props.providerTypeCatalog.map((entry) => (
          <div key={entry.type} className="rounded border border-border/70 p-4">
            <div className="mb-3 flex flex-wrap items-center gap-2">
              <div className="text-sm font-medium">{entry.type}</div>
              <span className="rounded border border-border px-2 py-0.5 text-[11px] text-muted-foreground">
                {entry.isBuiltIn ? 'built-in' : 'custom'}
              </span>
              <span className="rounded border border-border px-2 py-0.5 text-[11px] text-muted-foreground">
                seed v{entry.seedVersion}
              </span>
            </div>
            <div className="grid gap-3 md:grid-cols-3">
              <Field label="Friendly name" id={`provider-catalog-name-${entry.type}`}>
                <Input
                  id={`provider-catalog-name-${entry.type}`}
                  value={entry.friendlyName}
                  onChange={(event) =>
                    updateEntry(entry.type, { friendlyName: event.target.value })
                  }
                />
              </Field>
              <Field label="Default base URL" id={`provider-catalog-base-url-${entry.type}`}>
                <Input
                  id={`provider-catalog-base-url-${entry.type}`}
                  value={entry.defaultBaseUrl ?? ''}
                  onChange={(event) =>
                    updateEntry(entry.type, {
                      defaultBaseUrl: event.target.value.trim() ? event.target.value : null,
                    })
                  }
                  placeholder={
                    entry.requiresBaseUrl
                      ? 'Required default for new providers'
                      : 'Optional default'
                  }
                />
              </Field>
              <Field
                label="Default timeout (seconds)"
                id={`provider-catalog-timeout-${entry.type}`}
              >
                <Input
                  id={`provider-catalog-timeout-${entry.type}`}
                  type="number"
                  min={1}
                  disabled={!entry.supportsHttpTimeoutSeconds}
                  value={entry.defaultHttpTimeoutSeconds ?? ''}
                  onChange={(event) =>
                    updateEntry(entry.type, {
                      defaultHttpTimeoutSeconds: event.target.value.trim()
                        ? Number.parseInt(event.target.value, 10) || null
                        : null,
                    })
                  }
                  placeholder={entry.supportsHttpTimeoutSeconds ? '30' : 'Not supported'}
                />
              </Field>
            </div>
            <p className="mt-3 text-xs text-muted-foreground">
              Requires API key: {entry.requiresApiKey ? 'yes' : 'no'} · Requires base URL:{' '}
              {entry.requiresBaseUrl ? 'yes' : 'no'} · Supports timeout override:{' '}
              {entry.supportsHttpTimeoutSeconds ? 'yes' : 'no'}
            </p>
          </div>
        ))}
      </div>
    </div>
  );
}

function TabLabel(props: { label: string; dirty: boolean }) {
  return (
    <span className="inline-flex items-center gap-1">
      <span>{props.label}</span>
      {props.dirty ? (
        <span aria-label={`${props.label} has unsaved changes`} className="text-attention">
          *
        </span>
      ) : null}
    </span>
  );
}

function Field(props: { label: string; id: string; children: React.ReactNode }) {
  return (
    <div>
      <label className="mb-1 block text-xs text-muted-foreground" htmlFor={props.id}>
        {props.label}
      </label>
      {props.children}
    </div>
  );
}

function DocumentIngestionTab(props: {
  documentIngestionEnabled: boolean;
  internalParserConnectorId: string;
  internalParserToolName: string;
  parserTimeoutSeconds: string;
  parseRequiredExtensions: string;
  pdfImageFallbackEnabled: boolean;
  visionRouteAllowed: boolean;
  onDocumentIngestionEnabledChange(value: boolean): void;
  onInternalParserConnectorIdChange(value: string): void;
  onInternalParserToolNameChange(value: string): void;
  onParserTimeoutSecondsChange(value: string): void;
  onParseRequiredExtensionsChange(value: string): void;
  onPdfImageFallbackEnabledChange(value: boolean): void;
  onVisionRouteAllowedChange(value: boolean): void;
}) {
  return (
    <div className="rounded border border-border p-4">
      <div className="mb-3 text-sm font-medium">Document Ingestion</div>
      <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-4">
        <div className="rounded border border-border/70 p-3 md:col-span-2 xl:col-span-4">
          <label
            className="flex items-center gap-2 text-sm font-medium"
            htmlFor="document-ingestion-enabled"
          >
            <input
              id="document-ingestion-enabled"
              type="checkbox"
              checked={props.documentIngestionEnabled}
              onChange={(event) => props.onDocumentIngestionEnabledChange(event.target.checked)}
            />
            Enable document ingestion
          </label>
          <p className="mt-2 text-xs text-muted-foreground">
            Allows uploaded files to route through the internal parser toolchain before model
            execution.
          </p>
        </div>
        <Field label="Parser connector id" id="internal-parser-connector-id">
          <Input
            id="internal-parser-connector-id"
            value={props.internalParserConnectorId}
            onChange={(event) => props.onInternalParserConnectorIdChange(event.target.value)}
          />
        </Field>
        <Field label="Parser tool name" id="internal-parser-tool-name">
          <Input
            id="internal-parser-tool-name"
            value={props.internalParserToolName}
            onChange={(event) => props.onInternalParserToolNameChange(event.target.value)}
          />
        </Field>
        <Field label="Parser timeout seconds" id="parser-timeout-seconds">
          <Input
            id="parser-timeout-seconds"
            type="number"
            min={1}
            value={props.parserTimeoutSeconds}
            onChange={(event) => props.onParserTimeoutSecondsChange(event.target.value)}
          />
        </Field>
        <Field label="Required extensions" id="parse-required-extensions">
          <Input
            id="parse-required-extensions"
            value={props.parseRequiredExtensions}
            onChange={(event) => props.onParseRequiredExtensionsChange(event.target.value)}
          />
        </Field>
        <div className="rounded border border-border/70 p-3 md:col-span-2 xl:col-span-2">
          <label
            className="flex items-center gap-2 text-sm font-medium"
            htmlFor="pdf-image-fallback-enabled"
          >
            <input
              id="pdf-image-fallback-enabled"
              type="checkbox"
              checked={props.pdfImageFallbackEnabled}
              onChange={(event) => props.onPdfImageFallbackEnabledChange(event.target.checked)}
            />
            PDF image fallback
          </label>
          <p className="mt-2 text-xs text-muted-foreground">
            Falls back to image-based handling when direct PDF parsing is not sufficient.
          </p>
        </div>
        <div className="rounded border border-border/70 p-3 md:col-span-2 xl:col-span-2">
          <label
            className="flex items-center gap-2 text-sm font-medium"
            htmlFor="vision-route-allowed"
          >
            <input
              id="vision-route-allowed"
              type="checkbox"
              checked={props.visionRouteAllowed}
              onChange={(event) => props.onVisionRouteAllowedChange(event.target.checked)}
            />
            Allow vision route
          </label>
          <p className="mt-2 text-xs text-muted-foreground">
            Lets ingestion route compatible files through a vision-capable model path when needed.
          </p>
        </div>
      </div>
    </div>
  );
}
