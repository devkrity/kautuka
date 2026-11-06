using Kautuka.NetworkGateway.Admission;

using Microsoft.Data.Sqlite;

namespace Kautuka.NetworkGateway.Storage;

/// <summary>Prototype-owned ledger; never opens an application database.</summary>
internal sealed class SqliteAdmissionLedger : IAdmissionLedger, IDisposable
{
    private const long applicationId = 0x4B4E4731; // KNG1; never mutate an unrelated SQLite store.
    private readonly SqliteConnection connection;

    public SqliteAdmissionLedger(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        bool existed = File.Exists(fullPath);
        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 1,
        }.ToString());
        try
        {
            connection.Open();
            Initialize(existed);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private void Initialize(bool existed)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA application_id;";
        long marker = (long)command.ExecuteScalar()!;
        command.CommandText = "PRAGMA user_version;";
        long version = (long)command.ExecuteScalar()!;
        if ((existed && (marker != applicationId || version != 2)) || (!existed && (marker != 0 || version != 0)))
        {
            throw new InvalidDataException("Refusing an unrelated or unsupported admission ledger.");
        }

        command.CommandText = "PRAGMA journal_mode=WAL;";
        if (!string.Equals((string?)command.ExecuteScalar(), "wal", StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("The admission ledger requires WAL support.");
        }
        command.CommandText = """
            PRAGMA synchronous=FULL;
            PRAGMA application_id=0x4B4E4731;
            PRAGMA user_version=2;
            CREATE TABLE IF NOT EXISTS gateway_admissions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                ct_id INTEGER NOT NULL, ct_zone INTEGER NOT NULL,
                ingress_interface INTEGER NOT NULL, egress_interface INTEGER NOT NULL,
                hook INTEGER NOT NULL, socket_uid INTEGER, namespace_generation INTEGER NOT NULL,
                source_address TEXT NOT NULL, source_port INTEGER NOT NULL,
                destination_address TEXT NOT NULL, destination_port INTEGER NOT NULL, protocol INTEGER NOT NULL,
                subject TEXT NOT NULL, identity_generation INTEGER NOT NULL, policy_generation INTEGER NOT NULL,
                expires_at INTEGER NOT NULL, release_status TEXT NOT NULL DEFAULT 'durable_before_release'
            );
            CREATE TABLE IF NOT EXISTS gateway_revocations (
                subject TEXT NOT NULL, identity_generation INTEGER NOT NULL,
                PRIMARY KEY (subject, identity_generation)
            );
            """;
        command.ExecuteNonQuery();
    }

    public long Append(FlowKey flow, ActorRegistration source, long policyGeneration, DateTimeOffset expiresAt)
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO gateway_admissions
                (ct_id, ct_zone, ingress_interface, egress_interface, hook, socket_uid, namespace_generation, source_address, source_port, destination_address, destination_port,
                 protocol, subject, identity_generation, policy_generation, expires_at)
            VALUES ($ct, $zone, $iface, $egress, $hook, $uid, $namespace, $src, $sport, $dst, $dport, $proto, $subject, $identity, $policy, $expires);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$ct", flow.ConntrackId);
        command.Parameters.AddWithValue("$zone", flow.Zone);
        command.Parameters.AddWithValue("$iface", flow.IngressInterfaceIndex);
        command.Parameters.AddWithValue("$egress", flow.EgressInterfaceIndex);
        command.Parameters.AddWithValue("$hook", (int)flow.Path);
        command.Parameters.AddWithValue("$uid", flow.SocketUid is uint uid ? (object)uid : DBNull.Value);
        command.Parameters.AddWithValue("$namespace", flow.NamespaceGeneration);
        command.Parameters.AddWithValue("$src", flow.Source.ToString());
        command.Parameters.AddWithValue("$sport", flow.SourcePort);
        command.Parameters.AddWithValue("$dst", flow.Destination.ToString());
        command.Parameters.AddWithValue("$dport", flow.DestinationPort);
        command.Parameters.AddWithValue("$proto", flow.Protocol);
        command.Parameters.AddWithValue("$subject", source.Subject);
        command.Parameters.AddWithValue("$identity", source.Generation);
        command.Parameters.AddWithValue("$policy", policyGeneration);
        command.Parameters.AddWithValue("$expires", expiresAt.ToUnixTimeMilliseconds());
        long sequence = (long)command.ExecuteScalar()!;
        transaction.Commit();
        return sequence;
    }

    public void RecordReleaseFailure(long admissionId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE gateway_admissions SET release_status = 'not_released' WHERE id = $id;";
        command.Parameters.AddWithValue("$id", admissionId);
        command.ExecuteNonQuery();
    }

    public void Dispose() => connection.Dispose();

    public bool IsRevoked(string subject, long identityGeneration)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM gateway_revocations WHERE subject = $subject AND identity_generation = $generation);";
        command.Parameters.AddWithValue("$subject", subject);
        command.Parameters.AddWithValue("$generation", identityGeneration);
        return (long)command.ExecuteScalar()! != 0;
    }

    public void Revoke(string subject, long identityGeneration)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO gateway_revocations (subject, identity_generation) VALUES ($subject, $generation);";
        command.Parameters.AddWithValue("$subject", subject);
        command.Parameters.AddWithValue("$generation", identityGeneration);
        command.ExecuteNonQuery();
    }
}
