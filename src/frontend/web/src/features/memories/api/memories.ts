/**
 * Memories feature API operations
 */
import { apiClient } from '@/lib/apiClient';
import {
  memoryFactSchema,
  memoriesBundleSchema,
  type MemoryFact,
  type MemoriesBundle,
} from '../schemas/memorySchema';

export async function fetchMemoriesForAgent(agentId: string): Promise<MemoriesBundle> {
  const response = await apiClient.get(`/memories/agents/${agentId}`);
  return memoriesBundleSchema.parse(response.data);
}

export async function deleteFact(factId: string): Promise<void> {
  await apiClient.delete(`/memories/${factId}`);
}

export async function updateFact(
  factId: string,
  patch: { title: string; content: string; category: string }
): Promise<MemoryFact> {
  const response = await apiClient.patch(`/memories/${factId}`, patch);
  return memoryFactSchema.parse(response.data);
}

export async function deleteMemorySession(sessionId: string): Promise<void> {
  await apiClient.delete(`/sessions/${sessionId}`);
}
