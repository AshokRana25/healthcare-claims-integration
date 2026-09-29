using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Claims.Core;

/// <summary>
/// Durable SQLite inbox/outbox demo. Simulated consumer effects stay in the same transaction.
/// A real network gateway requires leases plus downstream idempotency, not an open DB transaction.
/// </summary>
public sealed class ClaimStore
{
    private readonly string connectionString;
    private readonly TimeProvider clock;
    public const int MaxAttempts = 3;

    public ClaimStore(string databasePath, TimeProvider? timeProvider = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, DefaultTimeout = 10, Pooling = false
        }.ToString();
        clock = timeProvider ?? TimeProvider.System;
        using var db = Open();
        Execute(db, null, "PRAGMA journal_mode=WAL;");
        Execute(db, null, """
            CREATE TABLE IF NOT EXISTS claims (
              id TEXT PRIMARY KEY, source TEXT NOT NULL, external_id TEXT NOT NULL,
              member_id TEXT NOT NULL, service_date TEXT NOT NULL, amount_cents INTEGER NOT NULL,
              currency TEXT NOT NULL, scenario TEXT NOT NULL, payload_hash TEXT NOT NULL,
              status TEXT NOT NULL, attempts INTEGER NOT NULL DEFAULT 0,
              created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL,
              UNIQUE(source, external_id));
            CREATE TABLE IF NOT EXISTS inbox (
              key TEXT PRIMARY KEY, payload_hash TEXT NOT NULL,
              claim_id TEXT NOT NULL REFERENCES claims(id));
            CREATE TABLE IF NOT EXISTS events (
              id TEXT PRIMARY KEY, claim_id TEXT NOT NULL UNIQUE REFERENCES claims(id),
              state TEXT NOT NULL, attempts INTEGER NOT NULL DEFAULT 0,
              next_attempt_at INTEGER NOT NULL, last_error TEXT);
            CREATE INDEX IF NOT EXISTS ix_events_due ON events(state, next_attempt_at);
            CREATE TABLE IF NOT EXISTS audit (
              sequence INTEGER PRIMARY KEY AUTOINCREMENT,
              claim_id TEXT NOT NULL REFERENCES claims(id), action TEXT NOT NULL,
              detail TEXT NOT NULL, occurred_at INTEGER NOT NULL);
            """);
    }

    private long Now => clock.GetUtcNow().ToUnixTimeMilliseconds();
    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        Execute(db, null, "PRAGMA foreign_keys=ON;");
        return db;
    }

    public Submission Submit(ClaimRequest request, string key)
    {
        if (ClaimValidation.Errors(request).Count > 0)
            throw new ArgumentException("Invalid claim request.");
        if (string.IsNullOrWhiteSpace(key) || key.Length > 120)
            throw new ArgumentException("Idempotency key must contain 1 to 120 characters.");
        var r = request with { Source = request.Source.Trim(),
            ExternalClaimId = request.ExternalClaimId.Trim(), MemberId = request.MemberId.Trim() };
        var canonical = JsonSerializer.Serialize(new { r.Source, r.ExternalClaimId, r.MemberId,
            r.ServiceDate, AmountCents = (long)(r.Amount * 100), r.Currency, r.Scenario });
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        using (var lookup = Command(db, tx,
            "SELECT claim_id,payload_hash FROM inbox WHERE key=$key", ("$key", key)))
        using (var reader = lookup.ExecuteReader())
        {
            if (reader.Read())
            {
                var id = reader.GetString(0);
                if (reader.GetString(1) != hash)
                    throw new ClaimConflictException("Idempotency key already belongs to a different payload.");
                reader.Close();
                Audit(db, tx, id, "DuplicateIgnored", "Repeated idempotency key; no new processing event.");
                var existing = ReadClaim(db, tx, id)!;
                tx.Commit();
                return new(existing, true);
            }
        }

        using (var lookup = Command(db, tx,
            "SELECT id,payload_hash FROM claims WHERE source=$source AND external_id=$external",
            ("$source", r.Source), ("$external", r.ExternalClaimId)))
        using (var reader = lookup.ExecuteReader())
        {
            if (reader.Read())
            {
                var id = reader.GetString(0);
                if (reader.GetString(1) != hash)
                    throw new ClaimConflictException("Source claim identifier already belongs to a different payload.");
                reader.Close();
                Execute(db, tx, "INSERT INTO inbox VALUES($key,$hash,$id)",
                    ("$key", key), ("$hash", hash), ("$id", id));
                Audit(db, tx, id, "DuplicateIgnored", "Repeated source claim identifier; new key linked to existing claim.");
                var existing = ReadClaim(db, tx, id)!;
                tx.Commit();
                return new(existing, true);
            }
        }

        var claimId = Guid.NewGuid().ToString("N");
        var now = Now;
        Execute(db, tx, """
            INSERT INTO claims VALUES($id,$source,$external,$member,$date,$amount,
                $currency,$scenario,$hash,'Queued',0,$now,$now)
            """, ("$id", claimId), ("$source", r.Source), ("$external", r.ExternalClaimId),
            ("$member", r.MemberId), ("$date", r.ServiceDate), ("$amount", (long)(r.Amount * 100)),
            ("$currency", r.Currency), ("$scenario", r.Scenario), ("$hash", hash), ("$now", now));
        Execute(db, tx, "INSERT INTO inbox VALUES($key,$hash,$id)",
            ("$key", key), ("$hash", hash), ("$id", claimId));
        Execute(db, tx, "INSERT INTO events VALUES($event,$id,'Pending',0,$now,NULL)",
            ("$event", Guid.NewGuid().ToString("N")), ("$id", claimId), ("$now", now));
        Audit(db, tx, claimId, "ClaimReceived", "Validated synthetic claim and durable processing event committed together.");
        var claim = ReadClaim(db, tx, claimId)!;
        tx.Commit();
        return new(claim, false);
    }

    public bool ProcessNext()
    {
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        string id;
        int attempt;
        using (var command = Command(db, tx, """
            SELECT claim_id,attempts FROM events
            WHERE state IN ('Pending','RetryScheduled') AND next_attempt_at <= $now
            ORDER BY next_attempt_at,id LIMIT 1
            """, ("$now", Now)))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) { tx.Commit(); return false; }
            id = reader.GetString(0);
            attempt = reader.GetInt32(1) + 1;
        }
        var claim = ReadClaim(db, tx, id)!;
        Audit(db, tx, id, "ProcessingAttempt", $"Simulated gateway attempt {attempt} of {MaxAttempts}.");
        string status;
        string eventState;
        string? error = null;
        var next = Now;
        if (claim.Scenario == "Permanent")
        {
            status = "DeadLetter"; eventState = "DeadLetter"; error = "SIM_PERMANENT_REJECTION";
        }
        else if (claim.Scenario == "Exhaust" || (claim.Scenario == "Transient" && attempt < 3))
        {
            error = "SIM_GATEWAY_UNAVAILABLE";
            status = attempt >= MaxAttempts ? "DeadLetter" : "RetryScheduled";
            eventState = status;
            next += (long)Math.Pow(2, attempt) * 1000;
        }
        else
        {
            // Fictional routing threshold only; not coverage, medical, or payment adjudication.
            status = claim.Amount > 5000 ? "ManualReview" : "Integrated";
            eventState = "Completed";
        }
        Execute(db, tx, "UPDATE claims SET status=$status,attempts=$attempt,updated_at=$now WHERE id=$id",
            ("$status", status), ("$attempt", attempt), ("$now", Now), ("$id", id));
        Execute(db, tx, """
            UPDATE events SET state=$state,attempts=$attempt,next_attempt_at=$next,last_error=$error
            WHERE claim_id=$id
            """, ("$state", eventState), ("$attempt", attempt), ("$next", next),
            ("$error", error), ("$id", id));
        Audit(db, tx, id, status, error ?? (status == "ManualReview"
            ? "Fictional USD 5,000 routing threshold exceeded; human review required."
            : "Synthetic integration acknowledged; no payment or clinical decision made."));
        tx.Commit();
        return true;
    }

    public ClaimDetail? Replay(string id, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 5 || reason.Length > 300)
            throw new ArgumentException("Supply a replay reason of 5 to 300 characters.");
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        var claim = ReadClaim(db, tx, id);
        if (claim is null) return null;
        if (claim.Status != "DeadLetter")
            throw new ReplayConflictException("Only dead-letter claims can be replayed.");
        Execute(db, tx, "UPDATE claims SET status='Queued',attempts=0,updated_at=$now WHERE id=$id",
            ("$now", Now), ("$id", id));
        Execute(db, tx, """
            UPDATE events SET state='Pending',attempts=0,next_attempt_at=$now,last_error=NULL
            WHERE claim_id=$id
            """, ("$now", Now), ("$id", id));
        Audit(db, tx, id, "ReplayRequested", reason.Trim());
        tx.Commit();
        return Get(id);
    }

    public IReadOnlyList<Claim> List()
    {
        using var db = Open();
        using var command = Command(db, null, "SELECT * FROM claims ORDER BY created_at DESC,id LIMIT 200");
        using var reader = command.ExecuteReader();
        var results = new List<Claim>();
        while (reader.Read()) results.Add(MapClaim(reader));
        return results;
    }

    public ClaimDetail? Get(string id)
    {
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: true);
        var claim = ReadClaim(db, tx, id);
        if (claim is null) return null;
        ProcessingEvent evt;
        using (var command = Command(db, tx,
            "SELECT id,state,attempts,next_attempt_at,last_error FROM events WHERE claim_id=$id", ("$id", id)))
        using (var reader = command.ExecuteReader())
        {
            reader.Read();
            evt = new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4));
        }
        var audit = new List<AuditEntry>();
        using (var command = Command(db, tx,
            "SELECT sequence,action,detail,occurred_at FROM audit WHERE claim_id=$id ORDER BY sequence", ("$id", id)))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) audit.Add(new(reader.GetInt64(0), reader.GetString(1),
                reader.GetString(2), reader.GetInt64(3)));
        tx.Commit();
        return new(claim, evt, audit);
    }

    private void Audit(SqliteConnection db, SqliteTransaction tx, string id, string action, string detail) =>
        Execute(db, tx, "INSERT INTO audit(claim_id,action,detail,occurred_at) VALUES($id,$action,$detail,$now)",
            ("$id", id), ("$action", action), ("$detail", detail), ("$now", Now));

    private static Claim? ReadClaim(SqliteConnection db, SqliteTransaction? tx, string id)
    {
        using var command = Command(db, tx, "SELECT * FROM claims WHERE id=$id", ("$id", id));
        using var reader = command.ExecuteReader();
        return reader.Read() ? MapClaim(reader) : null;
    }

    private static Claim MapClaim(SqliteDataReader r) => new(r.GetString(0), r.GetString(1),
        r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt64(5) / 100m, r.GetString(6),
        r.GetString(7), r.GetString(9), r.GetInt32(10), r.GetInt64(11), r.GetInt64(12));

    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql,
        params (string Name, object? Value)[] parameters)
    {
        var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var p in parameters) command.Parameters.AddWithValue(p.Name, p.Value ?? DBNull.Value);
        return command;
    }

    private static void Execute(SqliteConnection db, SqliteTransaction? tx, string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = Command(db, tx, sql, parameters);
        command.ExecuteNonQuery();
    }
}
