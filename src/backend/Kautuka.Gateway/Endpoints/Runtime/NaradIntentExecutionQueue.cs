using System.Text.Json;

using Kautuka.Gateway.Domain.Execution;


namespace Kautuka.Gateway.Endpoints.Runtime;

internal sealed record NaradIntentExecutionWork(JsonElement RequestBody);

internal interface INaradIntentExecutionQueue
{
    bool TryEnqueue(NaradIntentExecutionWork work);
}

internal sealed class NaradIntentExecutionQueue(SessionTurnQueueStore turns) : INaradIntentExecutionQueue
{
    public bool TryEnqueue(NaradIntentExecutionWork work)
    {
        try
        {
            string id = work.RequestBody.GetProperty("run_id").GetString()
                ?? throw new InvalidOperationException("Turn identity is required.");
            _ = turns.Admit(id, work.RequestBody);
            return true;
        }
        catch (SessionQueueRejectedException) { return false; }
    }
}
