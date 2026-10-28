import { useMutation, useQuery } from '@tanstack/react-query';
import { queryClient } from '@/lib/queryClient';
import {
  deleteSkill,
  fetchSkill,
  fetchSkills,
  importSkill,
  listUserEnvironmentKeys,
  saveSkill,
  saveUserEnvironmentValues,
  validateSkillImport,
} from './skills';
import { skillsQueryKeys } from './queryKeys';
import type { SkillImportOptions } from '../components/SkillImportPanel';

export function useSkills() {
  return useQuery({
    queryKey: skillsQueryKeys.list(),
    queryFn: fetchSkills,
  });
}

export function useSkill(skillId: string | null) {
  return useQuery({
    queryKey: skillId ? skillsQueryKeys.detail(skillId) : ['skills', 'no-id'],
    queryFn: () => (skillId ? fetchSkill(skillId) : Promise.reject(new Error('No skill ID'))),
    enabled: !!skillId,
  });
}

export function useValidateSkillImport() {
  return useMutation({
    mutationFn: ({
      file,
      options,
    }: {
      file: File;
      options: SkillImportOptions;
    }) => validateSkillImport(file, options),
  });
}

export function useImportSkill() {
  return useMutation({
    mutationFn: ({
      file,
      options,
    }: {
      file: File;
      options: SkillImportOptions;
    }) => importSkill(file, options),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: skillsQueryKeys.list() });
    },
  });
}

export function useDeleteSkill() {
  return useMutation({
    mutationFn: (skillId: string) => deleteSkill(skillId),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: skillsQueryKeys.list() });
    },
  });
}

export function useSaveSkill() {
  return useMutation({
    mutationFn: ({
      skillId,
      data,
    }: {
      skillId: string;
      data: { enabled: boolean; scope: 'system' | 'user' };
    }) => saveSkill(skillId, data),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: skillsQueryKeys.list() });
    },
  });
}

export function useUserEnvironmentKeys() {
  return useQuery({
    queryKey: skillsQueryKeys.userEnvironmentKeys(),
    queryFn: listUserEnvironmentKeys,
  });
}

export function useSaveUserEnvironmentValues() {
  return useMutation({
    mutationFn: (entries: Record<string, string>) => saveUserEnvironmentValues(entries),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: skillsQueryKeys.userEnvironmentKeys() });
    },
  });
}
