using Microsoft.Data.Sqlite;
using SapphireAvenueAssistant.Configuration;

namespace SapphireAvenueAssistant.Relay;

public sealed class RelayStore
{
    private readonly string connectionString;
    private readonly RelayOptions options;
    private readonly SemaphoreSlim gate = new(1, 1);

    public RelayStore(SapphireAvenueOptions options)
        : this(options, Directory.GetCurrentDirectory())
    {
    }

    public RelayStore(SapphireAvenueOptions options, IHostEnvironment environment)
        : this(options, environment.ContentRootPath)
    {
    }

    private RelayStore(SapphireAvenueOptions options, string contentRoot)
    {
        this.options = options.Relay;
        var databasePath = Path.GetFullPath(this.options.DatabasePath, contentRoot);
        var parent = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            ForeignKeys = true
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode = WAL;
                CREATE TABLE IF NOT EXISTS leader_state (
                    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
                    epoch INTEGER NOT NULL,
                    node_id TEXT NULL,
                    instance_id TEXT NULL,
                    expires_at_ms INTEGER NOT NULL
                );
                INSERT OR IGNORE INTO leader_state(singleton, epoch, expires_at_ms)
                VALUES (1, 0, 0);

                CREATE TABLE IF NOT EXISTS outbound_messages (
                    message_id TEXT PRIMARY KEY,
                    discord_interaction_id TEXT NOT NULL UNIQUE,
                    discord_user_id TEXT NOT NULL,
                    discord_display_name TEXT NOT NULL,
                    content TEXT NOT NULL,
                    created_at_ms INTEGER NOT NULL,
                    state INTEGER NOT NULL,
                    claim_id TEXT NULL,
                    claim_node_id TEXT NULL,
                    claim_instance_id TEXT NULL,
                    claim_epoch INTEGER NULL,
                    claim_expires_at_ms INTEGER NULL,
                    completed_at_ms INTEGER NULL
                );
                CREATE INDEX IF NOT EXISTS ix_outbound_pending
                ON outbound_messages(state, created_at_ms);

                CREATE TABLE IF NOT EXISTS inbound_observations (
                    observation_id TEXT PRIMARY KEY,
                    reporting_node_id TEXT NOT NULL,
                    cwls_slot INTEGER NOT NULL,
                    sender_name TEXT NOT NULL,
                    sender_world TEXT NULL,
                    content TEXT NOT NULL,
                    observed_at_ms INTEGER NOT NULL,
                    received_at_ms INTEGER NOT NULL,
                    publication_state INTEGER NOT NULL,
                    publish_claim_id TEXT NULL,
                    publish_claim_expires_at_ms INTEGER NULL,
                    publish_attempt_count INTEGER NOT NULL DEFAULT 0,
                    retry_at_ms INTEGER NULL,
                    discord_message_id TEXT NULL,
                    publication_error TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_inbound_publish
                ON inbound_observations(publication_state, retry_at_ms, received_at_ms);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<LeaderLease> HeartbeatAsync(
        string nodeId,
        string instanceId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            using var transaction = connection.BeginTransaction();
            var state = await ReadLeaderAsync(connection, transaction, cancellationToken);
            var nowMs = now.ToUnixTimeMilliseconds();
            var sameLiveInstance = state.ExpiresAtMs > nowMs &&
                string.Equals(state.NodeId, nodeId, StringComparison.Ordinal) &&
                string.Equals(state.InstanceId, instanceId, StringComparison.Ordinal);
            var canTakeLeadership = sameLiveInstance || state.ExpiresAtMs <= nowMs;

            if (canTakeLeadership)
            {
                var epoch = sameLiveInstance ? state.Epoch : checked(state.Epoch + 1);
                var expiresAt = now + options.LeaderLeaseDuration;
                await using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE leader_state
                    SET epoch = $epoch,
                        node_id = $nodeId,
                        instance_id = $instanceId,
                        expires_at_ms = $expiresAt
                    WHERE singleton = 1;
                    """;
                update.Parameters.AddWithValue("$epoch", epoch);
                update.Parameters.AddWithValue("$nodeId", nodeId);
                update.Parameters.AddWithValue("$instanceId", instanceId);
                update.Parameters.AddWithValue("$expiresAt", expiresAt.ToUnixTimeMilliseconds());
                await update.ExecuteNonQueryAsync(cancellationToken);
                transaction.Commit();
                return new LeaderLease(true, epoch, expiresAt);
            }

            transaction.Commit();
            return new LeaderLease(
                false,
                state.Epoch,
                DateTimeOffset.FromUnixTimeMilliseconds(state.ExpiresAtMs));
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<EnqueueResult> EnqueueOutboundAsync(
        string interactionId,
        string discordUserId,
        string discordDisplayName,
        string content,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            var messageId = Guid.NewGuid().ToString("N");
            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT OR IGNORE INTO outbound_messages(
                    message_id,
                    discord_interaction_id,
                    discord_user_id,
                    discord_display_name,
                    content,
                    created_at_ms,
                    state)
                VALUES ($messageId, $interactionId, $userId, $displayName, $content, $createdAt, $state);
                """;
            insert.Parameters.AddWithValue("$messageId", messageId);
            insert.Parameters.AddWithValue("$interactionId", interactionId);
            insert.Parameters.AddWithValue("$userId", discordUserId);
            insert.Parameters.AddWithValue("$displayName", discordDisplayName);
            insert.Parameters.AddWithValue("$content", content);
            insert.Parameters.AddWithValue("$createdAt", now.ToUnixTimeMilliseconds());
            insert.Parameters.AddWithValue("$state", (int)OutboundState.Pending);
            var inserted = await insert.ExecuteNonQueryAsync(cancellationToken) == 1;
            if (inserted)
            {
                return new EnqueueResult(messageId, true);
            }

            await using var select = connection.CreateCommand();
            select.CommandText = """
                SELECT message_id
                FROM outbound_messages
                WHERE discord_interaction_id = $interactionId;
                """;
            select.Parameters.AddWithValue("$interactionId", interactionId);
            var existing = (string?)await select.ExecuteScalarAsync(cancellationToken);
            return new EnqueueResult(existing ?? throw new InvalidOperationException("Duplicate interaction disappeared."), false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<OutboundClaimResult> ClaimOutboundAsync(
        string nodeId,
        string instanceId,
        long epoch,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            using var transaction = connection.BeginTransaction();
            var nowMs = now.ToUnixTimeMilliseconds();
            await SealExpiredOutboundClaimsAsync(connection, transaction, nowMs, cancellationToken);
            var leader = await ReadLeaderAsync(connection, transaction, cancellationToken);
            if (leader.Epoch != epoch || leader.ExpiresAtMs <= nowMs ||
                !string.Equals(leader.NodeId, nodeId, StringComparison.Ordinal) ||
                !string.Equals(leader.InstanceId, instanceId, StringComparison.Ordinal))
            {
                transaction.Commit();
                return new OutboundClaimResult(false, null);
            }

            await using var select = connection.CreateCommand();
            select.Transaction = transaction;
            select.CommandText = """
                SELECT message_id, discord_user_id, discord_display_name, content, created_at_ms
                FROM outbound_messages
                WHERE state = $pending
                ORDER BY created_at_ms, message_id
                LIMIT 1;
                """;
            select.Parameters.AddWithValue("$pending", (int)OutboundState.Pending);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                transaction.Commit();
                return new OutboundClaimResult(true, null);
            }

            var messageId = reader.GetString(0);
            var userId = reader.GetString(1);
            var displayName = reader.GetString(2);
            var content = reader.GetString(3);
            var createdAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4));
            await reader.DisposeAsync();

            var claimId = Guid.NewGuid().ToString("N");
            var expiresAt = now + options.ClaimLeaseDuration;
            if (expiresAt.ToUnixTimeMilliseconds() > leader.ExpiresAtMs)
            {
                expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(leader.ExpiresAtMs);
            }

            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE outbound_messages
                SET state = $claimed,
                    claim_id = $claimId,
                    claim_node_id = $nodeId,
                    claim_instance_id = $instanceId,
                    claim_epoch = $epoch,
                    claim_expires_at_ms = $expiresAt
                WHERE message_id = $messageId AND state = $pending;
                """;
            update.Parameters.AddWithValue("$claimed", (int)OutboundState.Claimed);
            update.Parameters.AddWithValue("$claimId", claimId);
            update.Parameters.AddWithValue("$nodeId", nodeId);
            update.Parameters.AddWithValue("$instanceId", instanceId);
            update.Parameters.AddWithValue("$epoch", epoch);
            update.Parameters.AddWithValue("$expiresAt", expiresAt.ToUnixTimeMilliseconds());
            update.Parameters.AddWithValue("$messageId", messageId);
            update.Parameters.AddWithValue("$pending", (int)OutboundState.Pending);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException("Outbound claim lost atomic ownership.");
            }

            transaction.Commit();
            return new OutboundClaimResult(
                true,
                new OutboundRelayMessage(messageId, claimId, userId, displayName, content, createdAt));
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> CompleteOutboundAsync(
        string nodeId,
        string instanceId,
        long epoch,
        string messageId,
        string claimId,
        DeliveryOutcome outcome,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            var nextState = outcome switch
            {
                DeliveryOutcome.Sent => OutboundState.Sent,
                DeliveryOutcome.NotSent => OutboundState.Pending,
                DeliveryOutcome.Ambiguous => OutboundState.Ambiguous,
                _ => throw new ArgumentOutOfRangeException(nameof(outcome))
            };
            await using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE outbound_messages
                SET state = $nextState,
                    completed_at_ms = CASE WHEN $nextState = $pending THEN NULL ELSE $completedAt END,
                    claim_id = CASE WHEN $nextState = $pending THEN NULL ELSE claim_id END,
                    claim_node_id = CASE WHEN $nextState = $pending THEN NULL ELSE claim_node_id END,
                    claim_instance_id = CASE WHEN $nextState = $pending THEN NULL ELSE claim_instance_id END,
                    claim_epoch = CASE WHEN $nextState = $pending THEN NULL ELSE claim_epoch END,
                    claim_expires_at_ms = CASE WHEN $nextState = $pending THEN NULL ELSE claim_expires_at_ms END
                WHERE message_id = $messageId
                  AND state = $claimed
                  AND claim_id = $claimId
                  AND claim_node_id = $nodeId
                  AND claim_instance_id = $instanceId
                  AND claim_epoch = $epoch;
                """;
            update.Parameters.AddWithValue("$nextState", (int)nextState);
            update.Parameters.AddWithValue("$pending", (int)OutboundState.Pending);
            update.Parameters.AddWithValue("$completedAt", now.ToUnixTimeMilliseconds());
            update.Parameters.AddWithValue("$messageId", messageId);
            update.Parameters.AddWithValue("$claimed", (int)OutboundState.Claimed);
            update.Parameters.AddWithValue("$claimId", claimId);
            update.Parameters.AddWithValue("$nodeId", nodeId);
            update.Parameters.AddWithValue("$instanceId", instanceId);
            update.Parameters.AddWithValue("$epoch", epoch);
            return await update.ExecuteNonQueryAsync(cancellationToken) == 1;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ObservationResult> EnqueueObservationAsync(
        string nodeId,
        InboundObservation observation,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT OR IGNORE INTO inbound_observations(
                    observation_id,
                    reporting_node_id,
                    cwls_slot,
                    sender_name,
                    sender_world,
                    content,
                    observed_at_ms,
                    received_at_ms,
                    publication_state)
                VALUES (
                    $observationId,
                    $nodeId,
                    $cwlsSlot,
                    $senderName,
                    $senderWorld,
                    $content,
                    $observedAt,
                    $receivedAt,
                    $state);
                """;
            insert.Parameters.AddWithValue("$observationId", observation.ObservationId);
            insert.Parameters.AddWithValue("$nodeId", nodeId);
            insert.Parameters.AddWithValue("$cwlsSlot", observation.CwlsSlot);
            insert.Parameters.AddWithValue("$senderName", observation.SenderName);
            insert.Parameters.AddWithValue("$senderWorld", (object?)observation.SenderWorld ?? DBNull.Value);
            insert.Parameters.AddWithValue("$content", observation.Content);
            insert.Parameters.AddWithValue("$observedAt", observation.ObservedAtUtc.ToUnixTimeMilliseconds());
            insert.Parameters.AddWithValue("$receivedAt", receivedAt.ToUnixTimeMilliseconds());
            insert.Parameters.AddWithValue("$state", (int)PublicationState.Pending);
            return new ObservationResult(await insert.ExecuteNonQueryAsync(cancellationToken) == 1);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<DiscordPublishWorkItem?> ClaimDiscordPublishAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            using var transaction = connection.BeginTransaction();
            var nowMs = now.ToUnixTimeMilliseconds();
            await using (var expire = connection.CreateCommand())
            {
                expire.Transaction = transaction;
                expire.CommandText = """
                    UPDATE inbound_observations
                    SET publication_state = $ambiguous,
                        publication_error = 'Publisher lease expired after Discord delivery may have occurred.'
                    WHERE publication_state = $inFlight
                      AND publish_claim_expires_at_ms <= $now;
                    """;
                expire.Parameters.AddWithValue("$ambiguous", (int)PublicationState.Ambiguous);
                expire.Parameters.AddWithValue("$inFlight", (int)PublicationState.InFlight);
                expire.Parameters.AddWithValue("$now", nowMs);
                await expire.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var select = connection.CreateCommand();
            select.Transaction = transaction;
            select.CommandText = """
                SELECT observation_id, publish_attempt_count, cwls_slot, sender_name,
                       sender_world, content, observed_at_ms
                FROM inbound_observations
                WHERE publication_state IN ($pending, $retry)
                  AND (retry_at_ms IS NULL OR retry_at_ms <= $now)
                ORDER BY received_at_ms, observation_id
                LIMIT 1;
                """;
            select.Parameters.AddWithValue("$pending", (int)PublicationState.Pending);
            select.Parameters.AddWithValue("$retry", (int)PublicationState.Retry);
            select.Parameters.AddWithValue("$now", nowMs);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                transaction.Commit();
                return null;
            }

            var observationId = reader.GetString(0);
            var attemptCount = reader.GetInt32(1) + 1;
            var cwlsSlot = reader.GetInt32(2);
            var senderName = reader.GetString(3);
            var senderWorld = reader.IsDBNull(4) ? null : reader.GetString(4);
            var content = reader.GetString(5);
            var observedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6));
            await reader.DisposeAsync();

            var claimId = Guid.NewGuid().ToString("N");
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE inbound_observations
                SET publication_state = $inFlight,
                    publish_claim_id = $claimId,
                    publish_claim_expires_at_ms = $expiresAt,
                    publish_attempt_count = $attemptCount,
                    retry_at_ms = NULL
                WHERE observation_id = $observationId
                  AND publication_state IN ($pending, $retry);
                """;
            update.Parameters.AddWithValue("$inFlight", (int)PublicationState.InFlight);
            update.Parameters.AddWithValue("$claimId", claimId);
            update.Parameters.AddWithValue("$expiresAt", (now + options.PublishLeaseDuration).ToUnixTimeMilliseconds());
            update.Parameters.AddWithValue("$attemptCount", attemptCount);
            update.Parameters.AddWithValue("$observationId", observationId);
            update.Parameters.AddWithValue("$pending", (int)PublicationState.Pending);
            update.Parameters.AddWithValue("$retry", (int)PublicationState.Retry);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException("Discord publication claim lost atomic ownership.");
            }

            transaction.Commit();
            return new DiscordPublishWorkItem(
                observationId,
                claimId,
                attemptCount,
                cwlsSlot,
                senderName,
                senderWorld,
                content,
                observedAt);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> CompleteDiscordPublishAsync(
        string observationId,
        string publishClaimId,
        PublicationState state,
        DateTimeOffset now,
        string? discordMessageId,
        string? error,
        TimeSpan? retryAfter = null,
        CancellationToken cancellationToken = default)
    {
        if (state is not (PublicationState.Published or PublicationState.Retry or PublicationState.Ambiguous or PublicationState.Failed))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE inbound_observations
                SET publication_state = $state,
                    discord_message_id = $discordMessageId,
                    publication_error = $error,
                    retry_at_ms = $retryAt
                WHERE observation_id = $observationId
                  AND publication_state = $inFlight
                  AND publish_claim_id = $claimId;
                """;
            update.Parameters.AddWithValue("$state", (int)state);
            update.Parameters.AddWithValue("$discordMessageId", (object?)discordMessageId ?? DBNull.Value);
            update.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
            update.Parameters.AddWithValue(
                "$retryAt",
                retryAfter.HasValue ? (now + retryAfter.Value).ToUnixTimeMilliseconds() : DBNull.Value);
            update.Parameters.AddWithValue("$observationId", observationId);
            update.Parameters.AddWithValue("$inFlight", (int)PublicationState.InFlight);
            update.Parameters.AddWithValue("$claimId", publishClaimId);
            return await update.ExecuteNonQueryAsync(cancellationToken) == 1;
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<OutboundState?> GetOutboundStateAsync(
        string messageId,
        CancellationToken cancellationToken = default) =>
        ReadEnumAsync<OutboundState>(
            "SELECT state FROM outbound_messages WHERE message_id = $id;",
            messageId,
            cancellationToken);

    public Task<PublicationState?> GetPublicationStateAsync(
        string observationId,
        CancellationToken cancellationToken = default) =>
        ReadEnumAsync<PublicationState>(
            "SELECT publication_state FROM inbound_observations WHERE observation_id = $id;",
            observationId,
            cancellationToken);

    public async Task<int> CountOutboundByInteractionAsync(
        string interactionId,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM outbound_messages WHERE discord_interaction_id = $id;";
            command.Parameters.AddWithValue("$id", interactionId);
            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<TEnum?> ReadEnumAsync<TEnum>(
        string sql,
        string id,
        CancellationToken cancellationToken)
        where TEnum : struct, Enum
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$id", id);
            var value = await command.ExecuteScalarAsync(cancellationToken);
            return value is null or DBNull ? null : (TEnum)Enum.ToObject(typeof(TEnum), Convert.ToInt32(value));
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task<(long Epoch, string? NodeId, string? InstanceId, long ExpiresAtMs)> ReadLeaderAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT epoch, node_id, instance_id, expires_at_ms FROM leader_state WHERE singleton = 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Leader state is not initialized.");
        }

        return (
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetInt64(3));
    }

    private static async Task SealExpiredOutboundClaimsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long nowMs,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE outbound_messages
            SET state = $ambiguous,
                completed_at_ms = $now
            WHERE state = $claimed
              AND claim_expires_at_ms <= $now;
            """;
        command.Parameters.AddWithValue("$ambiguous", (int)OutboundState.Ambiguous);
        command.Parameters.AddWithValue("$claimed", (int)OutboundState.Claimed);
        command.Parameters.AddWithValue("$now", nowMs);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
