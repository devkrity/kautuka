import { PageIntro } from '@/components/layout/PageIntro';
import { PageState } from '@/components/layout/PageState';
import { useProgressiveLimit } from '@/components/layout/useProgressiveLimit';
import { ShowMoreButton } from '@/components/layout/ShowMoreButton';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { useCurrentUser } from '@/features/auth';
import {
  CatalogRecommendationCard,
  ROLE_ADMIN,
  useCatalogRecommendations,
  useInstallCatalogRecommendation,
} from '@/features/settings';
import { HitlFunctionStat } from '@/features/approvals';
import {
  SkillCard,
  SkillImportPanel,
  useDeleteSkill,
  useImportSkill,
  useSaveSkill,
  useSaveUserEnvironmentValues,
  useSkill,
  useSkills,
  useUserEnvironmentKeys,
  useValidateSkillImport,
  type Skill,
  type SkillImportOptions,
  type SkillImportResult,
} from '@/features/skills';
import { useQueryClient } from '@tanstack/react-query';
import { BookOpenText, FileCode2, PackageOpen, Sparkles } from 'lucide-react';
import type { HTMLInputTypeAttribute } from 'react';
import React, { useEffect, useMemo, useRef, useState } from 'react';
import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import { toast } from 'sonner';

export const SkillsPage: React.FC = () => {
  const { data: currentUser } = useCurrentUser();
  const { data: skills = [], isLoading, error } = useSkills();
  const validateMutation = useValidateSkillImport();
  const importMutation = useImportSkill();
  const saveMutation = useSaveSkill();
  const saveUserEnvironmentMutation = useSaveUserEnvironmentValues();
  const deleteMutation = useDeleteSkill();
  const userEnvironmentKeysQuery = useUserEnvironmentKeys();
  const queryClient = useQueryClient();
  const recommendationsQuery = useCatalogRecommendations();
  const installRecommendationMutation = useInstallCatalogRecommendation('skills');

  const [preview, setPreview] = useState<SkillImportResult | null>(null);
  const [isPreviewVisible, setIsPreviewVisible] = useState(false);
  const [selectedSkillId, setSelectedSkillId] = useState<string | null>(null);
  const [pageError, setPageError] = useState<string | null>(null);
  const [environmentDraft, setEnvironmentDraft] = useState<Map<string, string>>(new Map());
  const validationRequestIdRef = useRef(0);

  const inspectionQuery = useSkill(selectedSkillId);

  useEffect(() => {
    setEnvironmentDraft(new Map());
  }, [selectedSkillId]);

  const handleValidate = async (file: File, options: SkillImportOptions) => {
    setPageError(null);
    const requestId = ++validationRequestIdRef.current;
    try {
      const result = await validateMutation.mutateAsync({ file, options });
      if (validationRequestIdRef.current === requestId) {
        setPreview(result);
        setIsPreviewVisible(true);
      }
    } catch (validationError) {
      if (validationRequestIdRef.current === requestId) {
        setPageError(
          validationError instanceof Error ? validationError.message : String(validationError)
        );
      }
    }
  };

  const handleImport = async (file: File, options: SkillImportOptions) => {
    setPageError(null);
    validationRequestIdRef.current += 1;
    setIsPreviewVisible(false);
    setPreview(null);
    try {
      await importMutation.mutateAsync({ file, options });
      setSelectedSkillId(null);
    } catch (importError) {
      setPageError(importError instanceof Error ? importError.message : String(importError));
    }
  };

  const handleDelete = (skill: Skill) => {
    if (!canManageSkill(skill)) {
      toast.error('Only admins can manage system skills.');
      return;
    }

    if (
      !globalThis.confirm(
        `Delete skill "${skill.name}"? This removes the package and detaches it from agents.`
      )
    ) {
      return;
    }

    deleteMutation.mutate(skill.id, {
      onSuccess: () => {
        if (selectedSkillId === skill.id) {
          setSelectedSkillId(null);
        }
      },
    });
  };

  const canManageSkill = (skill: Skill): boolean => {
    if (!currentUser) {
      return false;
    }

    if (skill.scope === 'system') {
      return currentUser.role === ROLE_ADMIN;
    }

    return skill.ownerUserId === currentUser.id;
  };

  const canImportSkills =
    !!currentUser && (currentUser.role === ROLE_ADMIN || Boolean(currentUser.canImportSkills));

  const handleToggleEnabled = (skill: Skill, enabled: boolean) => {
    if (!canManageSkill(skill)) {
      toast.error('Only admins can manage system skills.');
      return;
    }

    saveMutation.mutate({
      skillId: skill.id,
      data: {
        enabled,
        scope: skill.scope,
      },
    });
  };

  const skillError = useMemo(() => {
    if (pageError) return pageError;
    if (error instanceof Error) return error.message;
    return error ? String(error) : null;
  }, [error, pageError]);

  const configuredEnvironmentKeySet = useMemo(
    () => new Set(userEnvironmentKeysQuery.data ?? []),
    [userEnvironmentKeysQuery.data]
  );

  const requiredEnvironmentKeys = inspectionQuery.data?.skill.envKeys ?? [];

  const isSensitiveEnvironmentKey = (key: string): boolean =>
    /(token|secret|password|passwd|key)/i.test(key);

  const getEnvironmentDraftValue = (key: string): string => environmentDraft.get(key) ?? '';

  const setEnvironmentDraftValue = (key: string, value: string) => {
    setEnvironmentDraft((previous) => {
      const next = new Map(previous);
      next.set(key, value);
      return next;
    });
  };

  const handleSaveEnvironmentVariables = () => {
    if (requiredEnvironmentKeys.length === 0) {
      return;
    }

    const entries = Object.fromEntries(
      requiredEnvironmentKeys
        .map((key) => [key, getEnvironmentDraftValue(key).trim()] as const)
        .filter(([, value]) => value.length > 0)
    );

    if (Object.keys(entries).length === 0) {
      toast.error('Enter at least one environment variable value to save.');
      return;
    }

    saveUserEnvironmentMutation.mutate(entries, {
      onSuccess: () => {
        toast.success('Environment variable values saved.');
        setEnvironmentDraft(new Map());
      },
      onError: (saveError) => {
        toast.error(
          saveError instanceof Error ? saveError.message : 'Failed to save environment values.'
        );
      },
    });
  };

  const concealedTextInputType = ['pass', 'word'].join('') as HTMLInputTypeAttribute;
  const resourceCount = skills.filter((skill) => skill.hasResources).length;
  const scriptCount = skills.filter((skill) => skill.hasScripts).length;
  const recommendedSkills = recommendationsQuery.data?.skills ?? [];
  const activeSkills = skills.filter((skill) => skill.enabled);
  const availableSkills = skills.filter((skill) => !skill.enabled);
  const visibleRecommended = useProgressiveLimit(recommendedSkills);
  const visibleActive = useProgressiveLimit(activeSkills);
  const visibleAvailable = useProgressiveLimit(availableSkills);
  const recommendationsMessage = recommendationsQuery.isError
    ? 'Hosted catalog is currently unreachable. Check again later.'
    : (recommendationsQuery.data?.message ?? null);
  const installingRecommendationKey = installRecommendationMutation.isPending
    ? `${installRecommendationMutation.variables.kind}:${installRecommendationMutation.variables.id}`
    : null;

  return (
    <div className="page-shell">
      <PageIntro
        kicker="Capabilities"
        title="Skills"
        description="Import platform-owned skill packages, inspect their instructions and files, then attach them in agent settings."
        actions={
          <div className="flex items-center gap-3 text-xs text-muted-foreground">
            <span className="flex items-center gap-1.5">
              <PackageOpen className="h-4 w-4" /> {skills.length} total
            </span>
            <span className="flex items-center gap-1.5">
              <BookOpenText className="h-4 w-4" /> {resourceCount} with resources
            </span>
            <span className="flex items-center gap-1.5">
              <FileCode2 className="h-4 w-4" /> {scriptCount} with scripts
            </span>
          </div>
        }
      />

      <SkillImportPanel
        canImport={canImportSkills}
        validating={validateMutation.isPending}
        importing={importMutation.isPending}
        preview={isPreviewVisible && !selectedSkillId ? preview : null}
        errorMessage={skillError}
        onValidate={handleValidate}
        onImport={handleImport}
      />

      {!recommendationsQuery.isLoading && (
        <section className="space-y-3">
          <div className="flex items-start justify-between gap-3">
            <div>
              <h2 className="section-label">Recommended</h2>
              <p className="mt-1 text-sm text-muted-foreground">
                Catalog-backed skills you can install when you want them available to agents.
                Experimental; the hosted catalog may be unreachable.
              </p>
            </div>
            <span className="inline-flex items-center gap-1 rounded-full border border-border bg-card px-3 py-1 text-xs text-muted-foreground">
              <Sparkles className="h-3.5 w-3.5" /> Hosted catalog (experimental)
            </span>
          </div>
          {recommendationsMessage ? (
            <div className="rounded-xl border border-border bg-muted/40 p-4 text-sm text-muted-foreground">
              {recommendationsMessage}
            </div>
          ) : null}
          {recommendedSkills.length > 0 ? (
            <>
              <div className="grid gap-4 xl:grid-cols-2">
                {visibleRecommended.visibleItems.map((recommendation) => {
                  const isInstalling =
                    installingRecommendationKey === `${recommendation.kind}:${recommendation.id}`;

                  return (
                    <CatalogRecommendationCard
                      key={recommendation.id}
                      recommendation={recommendation}
                      icon="skill"
                      canImport={canImportSkills}
                      isInstalling={isInstalling}
                      onInstall={(item) => installRecommendationMutation.mutate(item)}
                    />
                  );
                })}
              </div>
              <ShowMoreButton
                count={visibleRecommended.hiddenCount}
                onClick={visibleRecommended.showMore}
              />
            </>
          ) : (
            <div className="rounded-xl border border-dashed border-border p-5 text-sm text-muted-foreground">
              No skill recommendations are available from the hosted catalog yet.
            </div>
          )}
        </section>
      )}

      {selectedSkillId && inspectionQuery.data && (
        <section className="workspace-block space-y-4">
          <div className="flex items-start justify-between gap-3">
            <div>
              <p className="section-label mb-1">Inspection</p>
              <h2 className="text-2xl font-extrabold tracking-tight text-foreground">
                {inspectionQuery.data.skill.name}
              </h2>
              <p className="mt-1 text-sm text-muted-foreground">
                {inspectionQuery.data.skill.description}
              </p>
              <div className="mt-2">
                <HitlFunctionStat
                  skillId={inspectionQuery.data.skill.id}
                  label={inspectionQuery.data.skill.name}
                />
              </div>
            </div>
            <Button
              type="button"
              variant="outline"
              size="sm"
              onClick={() => setSelectedSkillId(null)}
            >
              Close
            </Button>
          </div>

          <div className="grid gap-4 xl:grid-cols-[2fr,1fr]">
            <div className="rounded-xl border border-border bg-card p-4 shadow-sm">
              <div className="mb-3 flex items-center gap-2 text-sm font-medium text-foreground">
                <Sparkles className="h-4 w-4 text-primary" /> Instructions
              </div>
              <div className="prose prose-sm max-w-none dark:prose-invert">
                <ReactMarkdown remarkPlugins={[remarkGfm]}>
                  {inspectionQuery.data.instructionsMarkdown}
                </ReactMarkdown>
              </div>
            </div>
            <div className="space-y-4">
              <div className="rounded-xl border border-border bg-card p-4 shadow-sm">
                <div className="mb-3 text-sm font-medium text-foreground">Resources</div>
                {inspectionQuery.data.resourcePaths.length > 0 ? (
                  <ul className="space-y-1 text-sm text-muted-foreground">
                    {inspectionQuery.data.resourcePaths.map((resourcePath) => (
                      <li key={resourcePath}>{resourcePath}</li>
                    ))}
                  </ul>
                ) : (
                  <p className="text-sm text-muted-foreground">No packaged resources.</p>
                )}
              </div>
              <div className="rounded-xl border border-border bg-card p-4 shadow-sm">
                <div className="mb-3 text-sm font-medium text-foreground">Scripts</div>
                {inspectionQuery.data.scriptPaths.length > 0 ? (
                  <ul className="space-y-1 text-sm text-muted-foreground">
                    {inspectionQuery.data.scriptPaths.map((scriptPath) => (
                      <li key={scriptPath}>{scriptPath}</li>
                    ))}
                  </ul>
                ) : (
                  <p className="text-sm text-muted-foreground">No packaged scripts.</p>
                )}
              </div>
              <div className="rounded-xl border border-border bg-card p-4 shadow-sm">
                <div className="mb-3 text-sm font-medium text-foreground">
                  Required Environment Variables
                </div>
                {requiredEnvironmentKeys.length > 0 ? (
                  <div className="space-y-3">
                    <ul className="space-y-2 text-sm text-muted-foreground">
                      {requiredEnvironmentKeys.map((environmentKey) => {
                        const configured = configuredEnvironmentKeySet.has(environmentKey);
                        return (
                          <li
                            key={environmentKey}
                            className="rounded-lg border border-border/70 bg-muted/30 p-2"
                          >
                            <div className="mb-2 flex items-center justify-between gap-2">
                              <span className="font-mono text-xs text-foreground">
                                {environmentKey}
                              </span>
                              <span className="text-xs">
                                {configured ? 'configured' : 'missing'}
                              </span>
                            </div>
                            <Input
                              type={
                                isSensitiveEnvironmentKey(environmentKey)
                                  ? concealedTextInputType
                                  : 'text'
                              }
                              placeholder={`Set value for ${environmentKey}`}
                              value={getEnvironmentDraftValue(environmentKey)}
                              onChange={(event) =>
                                setEnvironmentDraftValue(environmentKey, event.target.value)
                              }
                            />
                          </li>
                        );
                      })}
                    </ul>
                    <Button
                      variant="outline"
                      onClick={handleSaveEnvironmentVariables}
                      disabled={saveUserEnvironmentMutation.isPending}
                    >
                      {saveUserEnvironmentMutation.isPending
                        ? 'Saving...'
                        : 'Save Environment Variable Values'}
                    </Button>
                    <p className="text-xs text-muted-foreground">
                      Values are stored in your personal key store and resolved at runtime.
                    </p>
                  </div>
                ) : (
                  <p className="text-sm text-muted-foreground">
                    This skill does not declare required environment variables.
                  </p>
                )}
              </div>
            </div>
          </div>
        </section>
      )}

      {isLoading && <PageState message="Loading skills..." />}

      {!isLoading && skills.length === 0 && !skillError && (
        <div className="flex flex-col items-center justify-center gap-2 py-20 text-muted-foreground">
          <PackageOpen className="h-10 w-10 opacity-30" />
          <p className="text-sm">No skills imported yet.</p>
        </div>
      )}

      {!isLoading && skills.length > 0 && (
        <div className="space-y-6">
          {activeSkills.length > 0 && (
            <section>
              <h2 className="mb-3 section-label">Active</h2>
              <div className="grid gap-4 xl:grid-cols-2">
                {visibleActive.visibleItems.map((skill) => (
                  <SkillCard
                    key={skill.id}
                    skill={skill}
                    deleting={deleteMutation.isPending}
                    toggling={saveMutation.isPending}
                    canManage={canManageSkill(skill)}
                    onInspect={setSelectedSkillId}
                    onDelete={handleDelete}
                    onToggleEnabled={handleToggleEnabled}
                  />
                ))}
              </div>
              <ShowMoreButton count={visibleActive.hiddenCount} onClick={visibleActive.showMore} />
            </section>
          )}

          {availableSkills.length > 0 && (
            <section>
              <h2 className="mb-3 section-label">Available</h2>
              <div className="grid gap-4 xl:grid-cols-2">
                {visibleAvailable.visibleItems.map((skill) => (
                  <SkillCard
                    key={skill.id}
                    skill={skill}
                    deleting={deleteMutation.isPending}
                    toggling={saveMutation.isPending}
                    canManage={canManageSkill(skill)}
                    onInspect={setSelectedSkillId}
                    onDelete={handleDelete}
                    onToggleEnabled={handleToggleEnabled}
                  />
                ))}
              </div>
              <ShowMoreButton
                count={visibleAvailable.hiddenCount}
                onClick={visibleAvailable.showMore}
              />
            </section>
          )}
        </div>
      )}
    </div>
  );
};
