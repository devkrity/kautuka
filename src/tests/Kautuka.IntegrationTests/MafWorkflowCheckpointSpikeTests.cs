using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.InProc;

namespace Kautuka.IntegrationTests;

public sealed class MafWorkflowCheckpointSpikeTests
{
    [Fact]
    public async Task SequentialRequestPortYieldsThenResumesFromCheckpoint()
    {
        RequestPort port = RequestPort.Create<string, string>("step");
        var finish = new CollectExecutor("finish");
        var workflow = new WorkflowBuilder(port)
            .AddEdge(port, finish)
            .WithOutputFrom(finish)
            .Build();

        CheckpointManager manager = CheckpointManager.CreateInMemory();
        InProcessExecutionEnvironment environment = InProcessExecution.Default.WithCheckpointing(manager);

        CheckpointInfo? checkpoint = null;
        ExternalRequest? pending = null;
        var seen = new List<string>();
        await using (StreamingRun first = await environment.RunStreamingAsync(workflow, "brief", "session-1", TestContext.Current.CancellationToken))
        {
            await foreach (WorkflowEvent evt in first.WatchStreamAsync(blockOnPendingRequest: false, TestContext.Current.CancellationToken))
            {
                seen.Add(evt.GetType().Name);
                if (evt is SuperStepCompletedEvent completed)
                {
                    checkpoint ??= completed.CompletionInfo?.Checkpoint;
                }

                if (evt is RequestInfoEvent request)
                {
                    pending = request.Request;
                }
            }
        }

        Assert.True(pending is not null, $"Expected RequestInfoEvent. Saw: {string.Join(", ", seen)}");
        Assert.True(pending!.TryGetDataAs(out string? work));
        Assert.Equal("brief", work);
        Assert.True(checkpoint is not null, $"Expected checkpoint on superstep. Saw: {string.Join(", ", seen)}");

        string? output = null;
        var resumeSeen = new List<string>();
        await using StreamingRun resumed = await environment.ResumeStreamingAsync(workflow, checkpoint!, TestContext.Current.CancellationToken);
        ExternalRequest? resumeRequest = null;
        await foreach (WorkflowEvent evt in resumed.WatchStreamAsync(blockOnPendingRequest: false, TestContext.Current.CancellationToken))
        {
            resumeSeen.Add($"halt:{evt.GetType().Name}");
            if (evt is RequestInfoEvent request)
            {
                resumeRequest = request.Request;
            }
        }

        Assert.True(resumeRequest is not null, $"Expected republished request. Saw: {string.Join(", ", resumeSeen)}");
        await resumed.SendResponseAsync(resumeRequest.CreateResponse("accepted"));
        await foreach (WorkflowEvent evt in resumed.WatchStreamAsync(blockOnPendingRequest: false, TestContext.Current.CancellationToken))
        {
            resumeSeen.Add(evt.GetType().Name);
            if (evt is ExecutorFailedEvent failed && failed.Data is Exception failure)
            {
                resumeSeen.Add(failure.GetType().Name + ": " + failure.Message);
            }

            if (evt is WorkflowErrorEvent error && error.Exception is not null)
            {
                resumeSeen.Add(error.Exception.GetType().Name + ": " + error.Exception.Message);
            }

            if (evt is WorkflowOutputEvent yielded && yielded.Is(out string? text))
            {
                output = text;
            }
        }

        Assert.True(output is not null, $"Expected workflow output. Saw: {string.Join(", ", resumeSeen)}");
        Assert.Equal("accepted", output);
    }

    private sealed class CollectExecutor : Executor<string, string>
    {
        public CollectExecutor(string id)
            : base(id, options: null, declareCrossRunShareable: true)
        {
        }

        public override ValueTask<string> HandleAsync(string message, IWorkflowContext context, CancellationToken cancellationToken)
        {
            return new ValueTask<string>(message);
        }
    }
}
