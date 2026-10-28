import { settingsAdmin } from '@/features/settings';
import { useEffect, useMemo, useState } from 'react';
import type {
  AdvancedSettingsEntry,
  KautukaInfrastructureSettingsEntry,
  ProviderCatalogEntry,
  SystemSettingsEntry,
} from '../types/settingsTypes';
import { apiError, normalizePersonaDefinitions, normalizeSystemRuntimeLimits } from '../utils/settingsUtils';

type AdvancedSettingsTabKey =
  | 'workspace'
  | 'document-ingestion'
  | 'updates'
  | 'runtime'
  | 'observability'
  | 'persona'
  | 'provider-catalog'
  | 'provider-path'
  | 'memory';

type AdvancedSettingsDraft = {
  captureMessageContent: boolean;
  logLevel: string;
  releaseChannel: 'stable' | 'preview';
  locale: string;
  timeZone: string;
  modelTimeoutSeconds: string;
  maxRunDurationSeconds: string;
  hitlExpirySeconds: string;
  hitlExpiryAction: 'inform' | 'retry' | 'abort';
  allowMultipleToolCalls: boolean;
  allowConcurrentInvocation: boolean;
  maxParallelTools: string;
  sandboxImage: string;
  sandboxCpus: string;
  sandboxMemory: string;
  sandboxTimeout: string;
  documentIngestionEnabled: boolean;
  internalParserConnectorId: string;
  internalParserToolName: string;
  parserTimeoutSeconds: string;
  parseRequiredExtensions: string;
  pdfImageFallbackEnabled: boolean;
  visionRouteAllowed: boolean;
  personaStyles: string;
  personaTones: string;
  personaVerbosityLevels: string;
  providerCatalogSignature: string;
  toolNativePromotionThreshold: string;
  toolMinObservations: string;
  toolNativeDemotionThreshold: string;
  structuredJsonStrictPromotionThreshold: string;
  structuredJsonMinObservations: string;
  visionEnableThreshold: string;
  observationDecayWindowDays: string;
  providerPathFallbackModeEnabled: boolean;
  embeddingsEnabled: boolean;
  factPersistThreshold: string;
  factInjectThreshold: string;
  maxInjectedFacts: string;
  injectionTokenBudget: string;
  sessionCompactionModelId: string;
};

const emptyDirtyTabs: Record<AdvancedSettingsTabKey, boolean> = {
  workspace: false,
  'document-ingestion': false,
  updates: false,
  runtime: false,
  observability: false,
  persona: false,
  'provider-catalog': false,
  'provider-path': false,
  memory: false,
};

function parseCsvList(input: string): string[] {
  const values = input
    .split(',')
    .map((part) => part.trim())
    .filter((part) => part.length > 0);

  return values.length > 0 ? values : [];
}

function normalizeProviderCatalog(entries: ProviderCatalogEntry[]): ProviderCatalogEntry[] {
  return [...entries]
    .map((entry) => ({
      ...entry,
      defaultBaseUrl: entry.defaultBaseUrl ?? null,
      defaultHttpTimeoutSeconds: entry.defaultHttpTimeoutSeconds ?? null,
    }))
    .sort((left, right) => left.type.localeCompare(right.type));
}

function providerCatalogSignature(entries: ProviderCatalogEntry[]): string {
  return JSON.stringify(
    normalizeProviderCatalog(entries).map((entry) => ({
      type: entry.type,
      friendlyName: entry.friendlyName,
      defaultBaseUrl: entry.defaultBaseUrl ?? null,
      defaultHttpTimeoutSeconds: entry.defaultHttpTimeoutSeconds ?? null,
    }))
  );
}

function buildDraft(settings: AdvancedSettingsEntry): AdvancedSettingsDraft {
  const personaDefinitions = normalizePersonaDefinitions(settings.personaDefinitions);

  return {
    captureMessageContent: Boolean(settings.system.observability.captureMessageContent),
    logLevel: String(settings.system.logLevel || 'DEBUG').toUpperCase(),
    releaseChannel: settings.system.updates.releaseChannel === 'preview' ? 'preview' : 'stable',
    locale: settings.system.runtimeGrounding.locale ?? 'en-US',
    timeZone: settings.system.runtimeGrounding.timeZone ?? 'UTC',
    modelTimeoutSeconds: String(settings.system.runtimeLimits?.modelTimeoutSeconds ?? 300),
    maxRunDurationSeconds: String(settings.system.runtimeLimits?.maxRunDurationSeconds ?? 0),
    hitlExpirySeconds: String(settings.system.runtimeLimits?.hitlExpirySeconds ?? 0),
    hitlExpiryAction:
      settings.system.runtimeLimits?.hitlExpiryAction === 'inform' ||
      settings.system.runtimeLimits?.hitlExpiryAction === 'retry'
        ? settings.system.runtimeLimits.hitlExpiryAction
        : 'abort',
    allowMultipleToolCalls: settings.system.runtimeLimits?.allowMultipleToolCalls !== false,
    allowConcurrentInvocation: Boolean(settings.system.runtimeLimits?.allowConcurrentInvocation),
    maxParallelTools: String(settings.system.runtimeLimits?.maxParallelTools ?? 0),
    sandboxImage: settings.kautuka.sandbox.image ?? 'kautuka-base:latest',
    sandboxCpus: String(settings.kautuka.sandbox.defaultCpus ?? 2),
    sandboxMemory: settings.kautuka.sandbox.defaultMemory ?? '4g',
    sandboxTimeout: String(settings.kautuka.sandbox.defaultTimeout ?? 120),
    documentIngestionEnabled: Boolean(settings.kautuka.documentIngestion.enabled),
    internalParserConnectorId:
      settings.kautuka.documentIngestion.internalParserConnectorId ?? 'markitdown',
    internalParserToolName:
      settings.kautuka.documentIngestion.internalParserToolName ?? 'convert_to_markdown',
    parserTimeoutSeconds: String(settings.kautuka.documentIngestion.parserTimeoutSeconds ?? 30),
    parseRequiredExtensions:
      settings.kautuka.documentIngestion.parseRequiredExtensions ?? '.pdf,.docx,.pptx,.xlsx',
    pdfImageFallbackEnabled: Boolean(settings.kautuka.documentIngestion.pdfImageFallbackEnabled),
    visionRouteAllowed: Boolean(settings.kautuka.documentIngestion.visionRouteAllowed),
    personaStyles: personaDefinitions.styles.join(', '),
    personaTones: personaDefinitions.tones.join(', '),
    personaVerbosityLevels: personaDefinitions.verbosity_levels.join(', '),
    providerCatalogSignature: providerCatalogSignature(settings.providerTypeCatalog),
    toolNativePromotionThreshold: String(settings.providerPathPolicy.toolNativePromotionThreshold),
    toolMinObservations: String(settings.providerPathPolicy.toolMinObservations),
    toolNativeDemotionThreshold: String(settings.providerPathPolicy.toolNativeDemotionThreshold),
    structuredJsonStrictPromotionThreshold: String(
      settings.providerPathPolicy.structuredJsonStrictPromotionThreshold
    ),
    structuredJsonMinObservations: String(
      settings.providerPathPolicy.structuredJsonMinObservations
    ),
    visionEnableThreshold: String(settings.providerPathPolicy.visionEnableThreshold),
    observationDecayWindowDays: String(settings.providerPathPolicy.observationDecayWindowDays),
    providerPathFallbackModeEnabled: Boolean(
      settings.providerPathPolicy.providerPathFallbackModeEnabled
    ),
    embeddingsEnabled: Boolean(settings.memory.embeddingsEnabled),
    factPersistThreshold: String(settings.memory.factPersistThreshold),
    factInjectThreshold: String(settings.memory.factInjectThreshold),
    maxInjectedFacts: String(settings.memory.maxInjectedFacts),
    injectionTokenBudget: String(settings.memory.injectionTokenBudget),
    sessionCompactionModelId: settings.memory.sessionCompactionModelId ?? '',
  };
}

function haveFieldsChanged(
  baselineDraft: AdvancedSettingsDraft | null,
  currentDraft: AdvancedSettingsDraft,
  keys: ReadonlyArray<keyof AdvancedSettingsDraft>
): boolean {
  if (!baselineDraft) {
    return false;
  }

  return keys.some((key) => Reflect.get(baselineDraft, key) !== Reflect.get(currentDraft, key));
}

export function useAdvancedTab() {
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [result, setResult] = useState<{ success: boolean; message: string } | null>(null);
  const [systemSettings, setSystemSettings] = useState<SystemSettingsEntry | null>(null);
  const [kautukaSettings, setKautukaSettings] = useState<KautukaInfrastructureSettingsEntry | null>(
    null
  );
  const [captureMessageContent, setCaptureMessageContent] = useState(false);
  const [logLevel, setLogLevel] = useState('DEBUG');
  const [releaseChannel, setReleaseChannel] = useState<'stable' | 'preview'>('stable');
  const [locale, setLocale] = useState('en-US');
  const [timeZone, setTimeZone] = useState('UTC');
  const [modelTimeoutSeconds, setModelTimeoutSeconds] = useState('300');
  const [maxRunDurationSeconds, setMaxRunDurationSeconds] = useState('0');
  const [hitlExpirySeconds, setHitlExpirySeconds] = useState('0');
  const [hitlExpiryAction, setHitlExpiryAction] = useState<'inform' | 'retry' | 'abort'>('abort');
  const [allowMultipleToolCalls, setAllowMultipleToolCalls] = useState(true);
  const [allowConcurrentInvocation, setAllowConcurrentInvocation] = useState(false);
  const [maxParallelTools, setMaxParallelTools] = useState('0');
  const [sandboxImage, setSandboxImage] = useState('kautuka-base:latest');
  const [sandboxCpus, setSandboxCpus] = useState('2');
  const [sandboxMemory, setSandboxMemory] = useState('4g');
  const [sandboxTimeout, setSandboxTimeout] = useState('120');
  const [documentIngestionEnabled, setDocumentIngestionEnabled] = useState(true);
  const [internalParserConnectorId, setInternalParserConnectorId] = useState('markitdown');
  const [internalParserToolName, setInternalParserToolName] = useState('convert_to_markdown');
  const [parserTimeoutSeconds, setParserTimeoutSeconds] = useState('30');
  const [parseRequiredExtensions, setParseRequiredExtensions] = useState('.pdf,.docx,.pptx,.xlsx');
  const [pdfImageFallbackEnabled, setPdfImageFallbackEnabled] = useState(false);
  const [visionRouteAllowed, setVisionRouteAllowed] = useState(false);
  const [personaStyles, setPersonaStyles] = useState('');
  const [personaTones, setPersonaTones] = useState('');
  const [personaVerbosityLevels, setPersonaVerbosityLevels] = useState('');
  const [providerTypeCatalog, setProviderTypeCatalog] = useState<ProviderCatalogEntry[]>([]);
  const [toolNativePromotionThreshold, setToolNativePromotionThreshold] = useState('0.8');
  const [toolMinObservations, setToolMinObservations] = useState('5');
  const [toolNativeDemotionThreshold, setToolNativeDemotionThreshold] = useState('0.4');
  const [structuredJsonStrictPromotionThreshold, setStructuredJsonStrictPromotionThreshold] =
    useState('0.9');
  const [structuredJsonMinObservations, setStructuredJsonMinObservations] = useState('5');
  const [visionEnableThreshold, setVisionEnableThreshold] = useState('0.8');
  const [observationDecayWindowDays, setObservationDecayWindowDays] = useState('30');
  const [providerPathFallbackModeEnabled, setProviderPathFallbackModeEnabled] = useState(true);
  const [embeddingsEnabled, setEmbeddingsEnabled] = useState(false);
  const [factPersistThreshold, setFactPersistThreshold] = useState('0.7');
  const [factInjectThreshold, setFactInjectThreshold] = useState('0.6');
  const [maxInjectedFacts, setMaxInjectedFacts] = useState('10');
  const [injectionTokenBudget, setInjectionTokenBudget] = useState('1024');
  const [sessionCompactionModelId, setSessionCompactionModelId] = useState('');
  const [baselineDraft, setBaselineDraft] = useState<AdvancedSettingsDraft | null>(null);

  const applyDraft = (draft: AdvancedSettingsDraft) => {
    setCaptureMessageContent(draft.captureMessageContent);
    setLogLevel(draft.logLevel);
    setReleaseChannel(draft.releaseChannel);
    setLocale(draft.locale);
    setTimeZone(draft.timeZone);
    setModelTimeoutSeconds(draft.modelTimeoutSeconds);
    setMaxRunDurationSeconds(draft.maxRunDurationSeconds);
    setHitlExpirySeconds(draft.hitlExpirySeconds);
    setHitlExpiryAction(draft.hitlExpiryAction);
    setAllowMultipleToolCalls(draft.allowMultipleToolCalls);
    setAllowConcurrentInvocation(draft.allowConcurrentInvocation);
    setMaxParallelTools(draft.maxParallelTools);
    setSandboxImage(draft.sandboxImage);
    setSandboxCpus(draft.sandboxCpus);
    setSandboxMemory(draft.sandboxMemory);
    setSandboxTimeout(draft.sandboxTimeout);
    setDocumentIngestionEnabled(draft.documentIngestionEnabled);
    setInternalParserConnectorId(draft.internalParserConnectorId);
    setInternalParserToolName(draft.internalParserToolName);
    setParserTimeoutSeconds(draft.parserTimeoutSeconds);
    setParseRequiredExtensions(draft.parseRequiredExtensions);
    setPdfImageFallbackEnabled(draft.pdfImageFallbackEnabled);
    setVisionRouteAllowed(draft.visionRouteAllowed);
    setPersonaStyles(draft.personaStyles);
    setPersonaTones(draft.personaTones);
    setPersonaVerbosityLevels(draft.personaVerbosityLevels);
    setToolNativePromotionThreshold(draft.toolNativePromotionThreshold);
    setToolMinObservations(draft.toolMinObservations);
    setToolNativeDemotionThreshold(draft.toolNativeDemotionThreshold);
    setStructuredJsonStrictPromotionThreshold(draft.structuredJsonStrictPromotionThreshold);
    setStructuredJsonMinObservations(draft.structuredJsonMinObservations);
    setVisionEnableThreshold(draft.visionEnableThreshold);
    setObservationDecayWindowDays(draft.observationDecayWindowDays);
    setProviderPathFallbackModeEnabled(draft.providerPathFallbackModeEnabled);
    setEmbeddingsEnabled(draft.embeddingsEnabled);
    setFactPersistThreshold(draft.factPersistThreshold);
    setFactInjectThreshold(draft.factInjectThreshold);
    setMaxInjectedFacts(draft.maxInjectedFacts);
    setInjectionTokenBudget(draft.injectionTokenBudget);
    setSessionCompactionModelId(draft.sessionCompactionModelId);
  };

  const applyAdvancedSettings = (settings: AdvancedSettingsEntry) => {
    setSystemSettings(settings.system);
    setKautukaSettings(settings.kautuka);
    setProviderTypeCatalog(normalizeProviderCatalog(settings.providerTypeCatalog));
    const draft = buildDraft(settings);
    applyDraft(draft);
    setBaselineDraft(draft);
  };

  const currentDraft = useMemo<AdvancedSettingsDraft>(
    () => ({
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
      providerCatalogSignature: providerCatalogSignature(providerTypeCatalog),
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
      injectionTokenBudget,
      sessionCompactionModelId,
    }),
    [
      captureMessageContent,
      documentIngestionEnabled,
      embeddingsEnabled,
      factInjectThreshold,
      factPersistThreshold,
      injectionTokenBudget,
      internalParserConnectorId,
      internalParserToolName,
      locale,
      logLevel,
      maxInjectedFacts,
      maxRunDurationSeconds,
      modelTimeoutSeconds,
      hitlExpiryAction,
      hitlExpirySeconds,
      allowMultipleToolCalls,
      allowConcurrentInvocation,
      maxParallelTools,
      observationDecayWindowDays,
      parseRequiredExtensions,
      parserTimeoutSeconds,
      pdfImageFallbackEnabled,
      personaStyles,
      personaTones,
      personaVerbosityLevels,
      providerTypeCatalog,
      providerPathFallbackModeEnabled,
      releaseChannel,
      sandboxCpus,
      sandboxImage,
      sandboxMemory,
      sandboxTimeout,
      sessionCompactionModelId,
      structuredJsonMinObservations,
      structuredJsonStrictPromotionThreshold,
      timeZone,
      toolMinObservations,
      toolNativeDemotionThreshold,
      toolNativePromotionThreshold,
      visionEnableThreshold,
      visionRouteAllowed,
    ]
  );

  const dirtyTabs = useMemo<Record<AdvancedSettingsTabKey, boolean>>(
    () => ({
      workspace: haveFieldsChanged(baselineDraft, currentDraft, [
        'sandboxImage',
        'sandboxCpus',
        'sandboxMemory',
        'sandboxTimeout',
      ]),
      'document-ingestion': haveFieldsChanged(baselineDraft, currentDraft, [
        'documentIngestionEnabled',
        'internalParserConnectorId',
        'internalParserToolName',
        'parserTimeoutSeconds',
        'parseRequiredExtensions',
        'pdfImageFallbackEnabled',
        'visionRouteAllowed',
      ]),
      updates: haveFieldsChanged(baselineDraft, currentDraft, ['releaseChannel']),
      runtime: haveFieldsChanged(baselineDraft, currentDraft, [
        'locale',
        'timeZone',
        'modelTimeoutSeconds',
        'maxRunDurationSeconds',
        'hitlExpirySeconds',
        'hitlExpiryAction',
        'allowMultipleToolCalls',
        'allowConcurrentInvocation',
        'maxParallelTools',
      ]),
      observability: haveFieldsChanged(baselineDraft, currentDraft, [
        'captureMessageContent',
        'logLevel',
      ]),
      persona: haveFieldsChanged(baselineDraft, currentDraft, [
        'personaStyles',
        'personaTones',
        'personaVerbosityLevels',
      ]),
      'provider-catalog': haveFieldsChanged(baselineDraft, currentDraft, [
        'providerCatalogSignature',
      ]),
      'provider-path': haveFieldsChanged(baselineDraft, currentDraft, [
        'toolNativePromotionThreshold',
        'toolMinObservations',
        'toolNativeDemotionThreshold',
        'structuredJsonStrictPromotionThreshold',
        'structuredJsonMinObservations',
        'visionEnableThreshold',
        'observationDecayWindowDays',
        'providerPathFallbackModeEnabled',
      ]),
      memory: haveFieldsChanged(baselineDraft, currentDraft, [
        'embeddingsEnabled',
        'factPersistThreshold',
        'factInjectThreshold',
        'maxInjectedFacts',
        'injectionTokenBudget',
        'sessionCompactionModelId',
      ]),
    }),
    [baselineDraft, currentDraft]
  );

  const dirtyTabCount = useMemo(() => Object.values(dirtyTabs).filter(Boolean).length, [dirtyTabs]);

  const hasUnsavedChanges = dirtyTabCount > 0;

  const buildPayload = (): AdvancedSettingsEntry => ({
    system: {
      logLevel,
      observability: {
        includeBinaryContent: Boolean(systemSettings?.observability.includeBinaryContent ?? false),
        captureMessageContent,
      },
      updates: {
        releaseChannel,
      },
      runtimeGrounding: {
        locale,
        timeZone,
      },
      runtimeLimits: normalizeSystemRuntimeLimits({
        modelTimeoutSeconds: Number.parseInt(modelTimeoutSeconds, 10),
        maxRunDurationSeconds: Number.parseInt(maxRunDurationSeconds, 10),
        hitlExpirySeconds: Number.parseInt(hitlExpirySeconds, 10),
        hitlExpiryAction,
        allowMultipleToolCalls,
        allowConcurrentInvocation,
        maxParallelTools: Number.parseInt(maxParallelTools, 10),
      }),
    },
    kautuka: {
      workspace: {
        baseRoot: kautukaSettings?.workspace.baseRoot ?? '',
      },
      sandbox: {
        image: sandboxImage,
        defaultCpus: Number.parseInt(sandboxCpus, 10) || 2,
        defaultMemory: sandboxMemory,
        defaultTimeout: Number.parseInt(sandboxTimeout, 10) || 120,
      },
      documentIngestion: {
        enabled: documentIngestionEnabled,
        internalParserConnectorId,
        internalParserToolName,
        parserTimeoutSeconds: Number.parseInt(parserTimeoutSeconds, 10) || 30,
        parseRequiredExtensions,
        pdfImageFallbackEnabled,
        visionRouteAllowed,
      },
    },
    personaDefinitions: {
      styles: parseCsvList(personaStyles),
      tones: parseCsvList(personaTones),
      verbosity_levels: parseCsvList(personaVerbosityLevels),
    },
    providerTypeCatalog: normalizeProviderCatalog(providerTypeCatalog),
    providerPathPolicy: {
      toolNativePromotionThreshold: Number.parseFloat(toolNativePromotionThreshold) || 0.8,
      toolMinObservations: Number.parseInt(toolMinObservations, 10) || 5,
      toolNativeDemotionThreshold: Number.parseFloat(toolNativeDemotionThreshold) || 0.4,
      structuredJsonStrictPromotionThreshold:
        Number.parseFloat(structuredJsonStrictPromotionThreshold) || 0.9,
      structuredJsonMinObservations: Number.parseInt(structuredJsonMinObservations, 10) || 5,
      visionEnableThreshold: Number.parseFloat(visionEnableThreshold) || 0.8,
      observationDecayWindowDays: Number.parseInt(observationDecayWindowDays, 10) || 30,
      providerPathFallbackModeEnabled,
    },
    memory: {
      embeddingsEnabled,
      factPersistThreshold: Number.parseFloat(factPersistThreshold) || 0.7,
      factInjectThreshold: Number.parseFloat(factInjectThreshold) || 0.6,
      maxInjectedFacts: Number.parseInt(maxInjectedFacts, 10) || 10,
      injectionTokenBudget: Number.parseInt(injectionTokenBudget, 10) || 1024,
      sessionCompactionModelId: sessionCompactionModelId.trim() || null,
    },
  });

  const load = async () => {
    setLoading(true);
    try {
      const response = await settingsAdmin.getAdvancedSettings();
      applyAdvancedSettings({
        ...response.data,
        personaDefinitions: normalizePersonaDefinitions(response.data.personaDefinitions),
        providerTypeCatalog: normalizeProviderCatalog(response.data.providerTypeCatalog ?? []),
      });
    } catch (err) {
      setResult({ success: false, message: apiError(err) });
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    void load();
  }, []);

  const save = async () => {
    if (!hasUnsavedChanges) {
      return;
    }

    setSaving(true);
    setResult(null);
    try {
      const response = await settingsAdmin.saveAdvancedSettings(buildPayload());
      applyAdvancedSettings({
        ...response.data,
        personaDefinitions: normalizePersonaDefinitions(response.data.personaDefinitions),
        providerTypeCatalog: normalizeProviderCatalog(response.data.providerTypeCatalog ?? []),
      });
      setResult({ success: true, message: 'Advanced settings saved.' });
    } catch (err) {
      setResult({ success: false, message: apiError(err) });
    } finally {
      setSaving(false);
    }
  };

  const resetChanges = () => {
    if (!baselineDraft) {
      return;
    }

    applyDraft(baselineDraft);
    setResult(null);
  };

  const statusItems = useMemo(
    () => [
      {
        title: 'Global Status',
        value: hasUnsavedChanges ? 'Unsaved changes pending' : 'All changes saved',
      },
      { title: 'Recent Activity', value: result?.message ?? 'No changes yet' },
      {
        title: 'Raw Message Capture',
        value: captureMessageContent ? 'Enabled' : 'Disabled',
      },
      {
        title: 'Workspace Location',
        value: kautukaSettings?.workspace?.baseRoot ?? 'Using runtime default',
      },
      {
        title: 'Provider Path Fallback',
        value: providerPathFallbackModeEnabled ? 'Enabled' : 'Disabled',
      },
      {
        title: 'Provider Catalog',
        value: `${providerTypeCatalog.length} built-in definitions`,
      },
      {
        title: 'Memory Injection Budget',
        value: `${injectionTokenBudget} tokens`,
      },
      {
        title: 'Compaction Model',
        value: sessionCompactionModelId.trim() || 'Not configured',
      },
    ],
    [
      captureMessageContent,
      kautukaSettings?.workspace?.baseRoot,
      hasUnsavedChanges,
      injectionTokenBudget,
      providerTypeCatalog.length,
      providerPathFallbackModeEnabled,
      result?.message,
      sessionCompactionModelId,
    ]
  );

  return {
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
    injectionTokenBudget,
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
    setInjectionTokenBudget,
    setSessionCompactionModelId,
    save,
    resetChanges,
  };
}
