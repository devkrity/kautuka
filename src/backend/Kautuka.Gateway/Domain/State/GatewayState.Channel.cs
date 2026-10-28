using Kautuka.Gateway.Domain.Sandbox;

namespace Kautuka.Gateway.Domain.State;

internal sealed partial class GatewayState
{
    private static void RebuildWorkspacesIfTopologyChanged(GatewayState state, IWorkspaceRuntimeTool workspaceTool)
    {
        _ = workspaceTool;
        string currentHash = state.ComputeTopologyHashFromState();
        if (string.Equals(state.TopologyHash, currentHash, StringComparison.Ordinal))
        {
            return;
        }

        state.TopologyHash = currentHash;
    }
}
