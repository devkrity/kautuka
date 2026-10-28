namespace Kautuka.UpdateService.Models;

public sealed record UpdateOperationRecord(
    string OperationType,
    DateTimeOffset CompletedAtUtc,
    string? FromVersion,
    string ToVersion,
    bool Succeeded,
    string? ErrorMessage);
