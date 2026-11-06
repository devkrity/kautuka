using Kautuka.NetworkGateway.Dns;

using Microsoft.Data.Sqlite;

namespace Kautuka.NetworkGateway.Storage;

/// <summary>Bounded resolver-owned fixture audit; separate from root network-flow admissions.</summary>
internal sealed class SqliteDnsDecisionLedger : IDnsDecisionLedger, IDisposable
{
    private const long applicationId = 0x4B444731; // KDG1
    private readonly object sync = new();
    private readonly SqliteConnection connection;
    private readonly int capacity;

    public SqliteDnsDecisionLedger(string path, int capacity = 8192)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        this.capacity = capacity;
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
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA application_id;";
            long marker = (long)command.ExecuteScalar()!;
            command.CommandText = "PRAGMA user_version;";
            long version = (long)command.ExecuteScalar()!;
            if ((existed && (marker != applicationId || version != 1)) || (!existed && (marker != 0 || version != 0)))
            {
                throw new InvalidDataException("Refusing unrelated or unsupported DNS audit storage.");
            }
            command.CommandText = "PRAGMA journal_mode=WAL;";
            if (!string.Equals((string?)command.ExecuteScalar(), "wal", StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("DNS decision audit requires WAL support.");
            }
            command.CommandText = """
                PRAGMA synchronous=FULL;
                PRAGMA application_id=0x4B444731;
                PRAGMA user_version=1;
                CREATE TABLE IF NOT EXISTS dns_decisions (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    operation TEXT NOT NULL, client_address TEXT NOT NULL, client_port INTEGER NOT NULL,
                    protocol INTEGER NOT NULL, ingress_interface INTEGER NOT NULL, namespace_generation INTEGER NOT NULL,
                    subject TEXT, identity_generation INTEGER, question_id INTEGER NOT NULL,
                    name TEXT NOT NULL, qtype INTEGER NOT NULL, qclass INTEGER NOT NULL, policy_generation INTEGER NOT NULL,
                    kind TEXT NOT NULL, reason TEXT NOT NULL, upstream_id INTEGER, answer_address TEXT, answer_ttl INTEGER,
                    timestamp INTEGER NOT NULL
                );
                """;
            command.ExecuteNonQuery();
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public void Append(DnsDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        lock (sync)
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT COUNT(*) FROM dns_decisions;";
            if ((long)command.ExecuteScalar()! >= capacity)
            {
                throw new IOException("DNS fixture decision capacity exhausted; retention requires operator action.");
            }
            command.CommandText = """
                INSERT INTO dns_decisions
                    (operation, client_address, client_port, protocol, ingress_interface, namespace_generation,
                     subject, identity_generation, question_id, name, qtype, qclass, policy_generation,
                     kind, reason, upstream_id, answer_address, answer_ttl, timestamp)
                VALUES ($operation, $address, $port, $protocol, $ingress, $namespace, $subject, $identity,
                    $question, $name, $type, $class, $policy, $kind, $reason, $upstream, $answer, $ttl, $at);
                """;
            command.Parameters.AddWithValue("$operation", decision.Operation.ToString("N"));
            command.Parameters.AddWithValue("$address", decision.Client.Address.ToString());
            command.Parameters.AddWithValue("$port", decision.Client.Port);
            command.Parameters.AddWithValue("$protocol", decision.Client.Protocol);
            command.Parameters.AddWithValue("$ingress", decision.Client.IngressInterfaceIndex);
            command.Parameters.AddWithValue("$namespace", decision.Client.NamespaceGeneration);
            command.Parameters.AddWithValue("$subject", (object?)decision.Subject ?? DBNull.Value);
            command.Parameters.AddWithValue("$identity", decision.IdentityGeneration is long identity ? (object)identity : DBNull.Value);
            command.Parameters.AddWithValue("$question", decision.Question.Id);
            command.Parameters.AddWithValue("$name", decision.Question.Name);
            command.Parameters.AddWithValue("$type", decision.Question.Type);
            command.Parameters.AddWithValue("$class", decision.Question.Class);
            command.Parameters.AddWithValue("$policy", decision.PolicyGeneration);
            command.Parameters.AddWithValue("$kind", decision.Kind.ToString());
            command.Parameters.AddWithValue("$reason", decision.Reason);
            command.Parameters.AddWithValue("$upstream", decision.UpstreamId is ushort upstream ? (object)upstream : DBNull.Value);
            command.Parameters.AddWithValue("$answer", (object?)decision.AnswerAddress ?? DBNull.Value);
            command.Parameters.AddWithValue("$ttl", decision.AnswerTtl is uint ttl ? (object)ttl : DBNull.Value);
            command.Parameters.AddWithValue("$at", decision.Timestamp);
            command.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            connection.Dispose();
        }
    }
}
