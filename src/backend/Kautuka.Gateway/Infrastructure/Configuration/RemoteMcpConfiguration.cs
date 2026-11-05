using System.Text.Json.Serialization;

namespace Kautuka.Gateway.Infrastructure.Configuration;

internal sealed record RemoteMcpCredential(string Key, string Label, [property: JsonRequired] bool Required);
internal sealed record RemoteMcpHeader(string Name, string? Value = null, string? CredentialKey = null, string? Prefix = null);
internal sealed record RemoteMcpConfiguration(RemoteMcpCredential[] Credentials, RemoteMcpHeader[] HttpHeaders, string? CredentialNamespace = null, string? CredentialAssociationId = null, string? CredentialOperationId = null, string? CredentialSourceId = null);
