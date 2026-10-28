namespace Kautuka.Control.Core.Services;

public interface IServiceManager
{
    Result Install(ServiceId id, string installDir, Config.PortAssignments ports);
    Result Uninstall(ServiceId id, string installDir);
    Result Start(ServiceId id, string installDir, Config.PortAssignments ports);
    Result StopService(ServiceId id, string installDir);
    Result Restart(ServiceId id, string installDir, Config.PortAssignments ports);
}

/// <summary>Lightweight result type for service operations.</summary>
public readonly record struct Result
{
    public bool IsOk { get; }
    public string Error { get; }

    private Result(bool ok, string error) { IsOk = ok; Error = error; }

    public static Result Ok() => new(true, string.Empty);
    public static Result Fail(string error) => new(false, error);

    public override string ToString() => IsOk ? "OK" : Error;
}
