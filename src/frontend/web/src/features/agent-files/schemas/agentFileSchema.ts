import { z } from 'zod';

export const agentFileEntrySchema = z.object({
  name: z.string(),
  path: z.string(),
  entryType: z.union([z.literal('directory'), z.literal('file')]),
  sizeBytes: z.number().nullable(),
  modifiedAt: z.string().datetime({ offset: true }),
  fileCount: z.number().nullable(),
});

export const agentFileListResponseSchema = z.object({
  agentId: z.string(),
  path: z.string(),
  entries: z.array(agentFileEntrySchema),
  totalFileCount: z.number(),
  totalSizeBytes: z.number(),
});

export const agentFileSearchResponseSchema = z.object({
  agentId: z.string(),
  query: z.string(),
  entries: z.array(agentFileEntrySchema),
});

export const agentFilesDisabledResponseSchema = z.object({
  agentId: z.string(),
  reasonCode: z.string(),
  message: z.string(),
});

export const agentFileUploadResponseSchema = z.object({
  agentId: z.string(),
  path: z.string(),
  fileName: z.string(),
  contentType: z.string(),
  sizeBytes: z.number(),
});

export const agentFileCreateFolderResponseSchema = z.object({
  agentId: z.string(),
  path: z.string(),
  name: z.string(),
});

export type AgentFileEntry = z.infer<typeof agentFileEntrySchema>;
export type AgentFileListResponse = z.infer<typeof agentFileListResponseSchema>;
export type AgentFileSearchResponse = z.infer<typeof agentFileSearchResponseSchema>;
export type AgentFilesDisabledResponse = z.infer<typeof agentFilesDisabledResponseSchema>;
export type AgentFileUploadResponse = z.infer<typeof agentFileUploadResponseSchema>;
export type AgentFileCreateFolderResponse = z.infer<typeof agentFileCreateFolderResponseSchema>;

export type AgentFilesQueryResult =
  | { status: 'ready'; response: AgentFileListResponse }
  | { status: 'disabled'; response: AgentFilesDisabledResponse };