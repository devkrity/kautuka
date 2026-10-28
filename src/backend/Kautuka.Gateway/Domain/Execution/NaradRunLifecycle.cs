using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Execution;

internal static class NaradRunLifecycle
{
    public static bool IsTerminal(string eventType)
        => NaradEventTypes.IsRootRunTerminalEventType(eventType);

    public static bool IsLifecycleEvent(string eventType)
        => NaradEventTypes.IsRunLifecycleEventType(eventType);
}
