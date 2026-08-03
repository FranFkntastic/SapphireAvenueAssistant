using Microsoft.Data.Sqlite;
using SapphireAvenueAssistant.Configuration;
using System.Security.Cryptography;
using System.Text;

namespace SapphireAvenueAssistant.Relay;

public sealed class RelayStore
{
    private readonly string connectionString;
    private readonly SapphireAvenueOptions sapphireOptions;
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
        sapphireOptions = options;
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
                    publish_channel_id TEXT NULL,
                    publish_config_revision INTEGER NULL,
                    discord_message_id TEXT NULL,
                    publication_error TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_inbound_publish
                ON inbound_observations(publication_state, retry_at_ms, received_at_ms);

                CREATE TABLE IF NOT EXISTS relay_nodes (
                    node_id TEXT PRIMARY KEY,
                    label TEXT NOT NULL,
                    token_hash BLOB NULL,
                    last_seen_at_ms INTEGER NULL,
                    last_instance_id TEXT NULL,
                    can_send_to_game INTEGER NOT NULL DEFAULT 0,
                    capability_reported INTEGER NOT NULL DEFAULT 0,
                    revoked_at_ms INTEGER NULL,
                    revoked_by_discord_user_id TEXT NULL
                );

                CREATE TABLE IF NOT EXISTS relay_pairing_codes (
                    code_hash BLOB PRIMARY KEY,
                    node_id TEXT NOT NULL UNIQUE REFERENCES relay_nodes(node_id),
                    expires_at_ms INTEGER NOT NULL,
                    consumed_at_ms INTEGER NULL
                );

                CREATE TABLE IF NOT EXISTS community_relay_configuration (
                    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
                    guild_id TEXT NOT NULL UNIQUE,
                    channel_id TEXT NOT NULL,
                    allowed_role_id TEXT NULL,
                    is_paused INTEGER NOT NULL DEFAULT 0,
                    preferred_node_id TEXT NULL,
                    revision INTEGER NOT NULL,
                    updated_by_discord_user_id TEXT NOT NULL,
                    updated_at_ms INTEGER NOT NULL
                );

                CREATE TABLE IF NOT EXISTS discord_management_interactions (
                    interaction_id TEXT PRIMARY KEY,
                    guild_id TEXT NOT NULL,
                    actor_discord_user_id TEXT NOT NULL,
                    action INTEGER NOT NULL,
                    request_hash BLOB NOT NULL,
                    response TEXT NOT NULL,
                    succeeded INTEGER NOT NULL,
                    created_at_ms INTEGER NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            await EnsureColumnAsync(
                connection, "inbound_observations", "publish_channel_id", "TEXT NULL", cancellationToken);
            await EnsureColumnAsync(
                connection, "inbound_observations", "publish_config_revision", "INTEGER NULL", cancellationToken);
            await EnsureColumnAsync(
                connection, "relay_nodes", "can_send_to_game", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
            await EnsureColumnAsync(
                connection, "relay_nodes", "capability_reported", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
            await SeedConfigurationAsync(connection, cancellationToken);
            await SeedRelayNodesAsync(connection, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<CommunityRelayConfiguration?> GetCommunityConfigurationAsync(
        string guildId,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            return await ReadCommunityConfigurationAsync(connection, null, guildId, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> AuthorizeNodeAsync(
        string nodeId,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        var actualHash = HashSecret(accessToken);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT token_hash
                FROM relay_nodes
                WHERE node_id = $nodeId
                  AND revoked_at_ms IS NULL
                  AND token_hash IS NOT NULL;
                """;
            command.Parameters.AddWithValue("$nodeId", nodeId);
            var expected = await command.ExecuteScalarAsync(cancellationToken) as byte[];
            return expected is not null &&
                actualHash.Length == expected.Length &&
                CryptographicOperations.FixedTimeEquals(actualHash, expected);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<BridgeManagementResult> ApplyBridgeManagementAsync(
        BridgeManagementRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var requestHash = HashSecret(string.Join(
            '\n',
            (int)request.Action,
            request.ChannelId ?? string.Empty,
            request.RoleId ?? string.Empty,
            request.NodeId ?? string.Empty,
            request.NodeLabel ?? string.Empty));
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            using var transaction = connection.BeginTransaction();
            var replay = await ReadManagementReplayAsync(
                connection,
                transaction,
                request.InteractionId,
                requestHash,
                cancellationToken);
            if (replay is not null)
            {
                transaction.Commit();
                return replay;
            }

            var result = await ApplyBridgeMutationAsync(connection, transaction, request, now, cancellationToken);
            var persistedResponse = request.Action == BridgeManagementAction.AddNode && result.Succeeded
                ? "This interaction already created the node. Run `/bridge add-node` again if you no longer have its one-time pairing code."
                : result.Response;
            await using var audit = connection.CreateCommand();
            audit.Transaction = transaction;
            audit.CommandText = """
                INSERT INTO discord_management_interactions(
                    interaction_id, guild_id, actor_discord_user_id, action,
                    request_hash, response, succeeded, created_at_ms)
                VALUES ($interactionId, $guildId, $actorId, $action,
                        $requestHash, $response, $succeeded, $createdAt);
                """;
            audit.Parameters.AddWithValue("$interactionId", request.InteractionId);
            audit.Parameters.AddWithValue("$guildId", request.GuildId);
            audit.Parameters.AddWithValue("$actorId", request.ActorDiscordUserId);
            audit.Parameters.AddWithValue("$action", (int)request.Action);
            audit.Parameters.AddWithValue("$requestHash", requestHash);
            audit.Parameters.AddWithValue("$response", persistedResponse);
            audit.Parameters.AddWithValue("$succeeded", result.Succeeded ? 1 : 0);
            audit.Parameters.AddWithValue("$createdAt", now.ToUnixTimeMilliseconds());
            await audit.ExecuteNonQueryAsync(cancellationToken);
            transaction.Commit();
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<PairingExchangeResult?> ExchangePairingCodeAsync(
        string pairingCode,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var normalizedCode = NormalizePairingCode(pairingCode);
        if (normalizedCode is null)
        {
            return null;
        }

        var codeHash = HashSecret(normalizedCode);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            using var transaction = connection.BeginTransaction();
            await using var select = connection.CreateCommand();
            select.Transaction = transaction;
            select.CommandText = """
                SELECT p.node_id, n.label
                FROM relay_pairing_codes p
                JOIN relay_nodes n ON n.node_id = p.node_id
                WHERE p.code_hash = $codeHash
                  AND p.consumed_at_ms IS NULL
                  AND p.expires_at_ms > $now
                  AND n.revoked_at_ms IS NULL;
                """;
            select.Parameters.AddWithValue("$codeHash", codeHash);
            select.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                transaction.Commit();
                return null;
            }

            var nodeId = reader.GetString(0);
            var nodeLabel = reader.GetString(1);
            await reader.DisposeAsync();
            var accessToken = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            await using var consume = connection.CreateCommand();
            consume.Transaction = transaction;
            consume.CommandText = """
                UPDATE relay_pairing_codes
                SET consumed_at_ms = $now
                WHERE code_hash = $codeHash
                  AND consumed_at_ms IS NULL
                  AND expires_at_ms > $now;

                UPDATE relay_nodes
                SET token_hash = $tokenHash
                WHERE node_id = $nodeId
                  AND revoked_at_ms IS NULL;
                """;
            consume.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            consume.Parameters.AddWithValue("$codeHash", codeHash);
            consume.Parameters.AddWithValue("$tokenHash", HashSecret(accessToken));
            consume.Parameters.AddWithValue("$nodeId", nodeId);
            await consume.ExecuteNonQueryAsync(cancellationToken);
            transaction.Commit();
            return new PairingExchangeResult(nodeId, nodeLabel, accessToken);
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
        bool? canSendToGame = null,
        bool allowLegacyHeartbeatWithoutCapability = true,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            using var transaction = connection.BeginTransaction();
            var nowMs = now.ToUnixTimeMilliseconds();
            var effectiveCanSendToGame = canSendToGame ?? allowLegacyHeartbeatWithoutCapability;
            await using (var seen = connection.CreateCommand())
            {
                seen.Transaction = transaction;
                seen.CommandText = """
                    UPDATE relay_nodes
                    SET last_seen_at_ms = $now,
                        last_instance_id = $instanceId,
                        can_send_to_game = $canSendToGame,
                        capability_reported = $capabilityReported
                    WHERE node_id = $nodeId
                      AND revoked_at_ms IS NULL;
                    """;
                seen.Parameters.AddWithValue("$now", nowMs);
                seen.Parameters.AddWithValue("$instanceId", instanceId);
                seen.Parameters.AddWithValue("$canSendToGame", effectiveCanSendToGame ? 1 : 0);
                seen.Parameters.AddWithValue("$capabilityReported", canSendToGame.HasValue ? 1 : 0);
                seen.Parameters.AddWithValue("$nodeId", nodeId);
                if (await seen.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    transaction.Commit();
                    return new LeaderLease(false, false, false, 0, DateTimeOffset.UnixEpoch);
                }
            }

            var state = await ReadLeaderAsync(connection, transaction, cancellationToken);
            var preferred = await ReadPreferredNodeAsync(connection, transaction, cancellationToken);
            var isPreferred = preferred is not null &&
                string.Equals(preferred.Value.NodeId, nodeId, StringComparison.Ordinal);
            var sameLiveInstance = state.ExpiresAtMs > nowMs &&
                string.Equals(state.NodeId, nodeId, StringComparison.Ordinal) &&
                string.Equals(state.InstanceId, instanceId, StringComparison.Ordinal);
            if (sameLiveInstance && !effectiveCanSendToGame)
            {
                var releasedEpoch = checked(state.Epoch + 1);
                await using var release = connection.CreateCommand();
                release.Transaction = transaction;
                release.CommandText = """
                    UPDATE leader_state
                    SET epoch = $epoch,
                        node_id = NULL,
                        instance_id = NULL,
                        expires_at_ms = 0
                    WHERE singleton = 1;
                    """;
                release.Parameters.AddWithValue("$epoch", releasedEpoch);
                await release.ExecuteNonQueryAsync(cancellationToken);
                transaction.Commit();
                return new LeaderLease(true, false, isPreferred, releasedEpoch, now);
            }

            var canTakeLeadership = effectiveCanSendToGame && (sameLiveInstance || state.ExpiresAtMs <= nowMs);
            if (!sameLiveInstance && state.ExpiresAtMs <= nowMs)
            {
                if (preferred is not null &&
                    !string.Equals(preferred.Value.NodeId, nodeId, StringComparison.Ordinal))
                {
                    var preferredReferenceMs = preferred.Value.LastSeenAtMs ?? preferred.Value.ConfiguredAtMs;
                    canTakeLeadership = preferredReferenceMs + (long)options.LeaderLeaseDuration.TotalMilliseconds <= nowMs;
                }
            }

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
                return new LeaderLease(true, true, isPreferred, epoch, expiresAt);
            }

            transaction.Commit();
            return new LeaderLease(
                true,
                false,
                isPreferred,
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

    public async Task<CwlsEnqueueResult> EnqueueAuthorizedOutboundAsync(
        string guildId,
        string channelId,
        IReadOnlyCollection<string> memberRoleIds,
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
            using var transaction = connection.BeginTransaction();
            var configuration = await ReadCommunityConfigurationAsync(
                connection, transaction, guildId, cancellationToken);
            if (configuration is null)
            {
                transaction.Commit();
                return new CwlsEnqueueResult(CwlsEnqueueRefusal.NotConfigured);
            }

            if (configuration.IsPaused)
            {
                transaction.Commit();
                return new CwlsEnqueueResult(CwlsEnqueueRefusal.Paused);
            }

            if (!string.Equals(configuration.ChannelId, channelId, StringComparison.Ordinal))
            {
                transaction.Commit();
                return new CwlsEnqueueResult(CwlsEnqueueRefusal.WrongChannel);
            }

            if (configuration.AllowedRoleId is not null &&
                !memberRoleIds.Contains(configuration.AllowedRoleId, StringComparer.Ordinal))
            {
                transaction.Commit();
                return new CwlsEnqueueResult(CwlsEnqueueRefusal.RoleRequired);
            }

            var messageId = Guid.NewGuid().ToString("N");
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO outbound_messages(
                    message_id, discord_interaction_id, discord_user_id,
                    discord_display_name, content, created_at_ms, state)
                VALUES ($messageId, $interactionId, $userId,
                        $displayName, $content, $createdAt, $state);
                """;
            insert.Parameters.AddWithValue("$messageId", messageId);
            insert.Parameters.AddWithValue("$interactionId", interactionId);
            insert.Parameters.AddWithValue("$userId", discordUserId);
            insert.Parameters.AddWithValue("$displayName", discordDisplayName);
            insert.Parameters.AddWithValue("$content", content);
            insert.Parameters.AddWithValue("$createdAt", now.ToUnixTimeMilliseconds());
            insert.Parameters.AddWithValue("$state", (int)OutboundState.Pending);
            var inserted = await insert.ExecuteNonQueryAsync(cancellationToken) == 1;
            if (!inserted)
            {
                await using var select = connection.CreateCommand();
                select.Transaction = transaction;
                select.CommandText = "SELECT message_id FROM outbound_messages WHERE discord_interaction_id = $interactionId;";
                select.Parameters.AddWithValue("$interactionId", interactionId);
                messageId = (string?)await select.ExecuteScalarAsync(cancellationToken) ??
                    throw new InvalidOperationException("Duplicate interaction disappeared.");
            }

            transaction.Commit();
            return new CwlsEnqueueResult(CwlsEnqueueRefusal.None, messageId, inserted);
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
            if (!await IsActiveNodeAsync(connection, transaction, nodeId, cancellationToken))
            {
                transaction.Commit();
                return new OutboundClaimResult(false, false, null);
            }

            var nowMs = now.ToUnixTimeMilliseconds();
            await SealExpiredOutboundClaimsAsync(connection, transaction, nowMs, cancellationToken);
            var leader = await ReadLeaderAsync(connection, transaction, cancellationToken);
            if (leader.Epoch != epoch || leader.ExpiresAtMs <= nowMs ||
                !string.Equals(leader.NodeId, nodeId, StringComparison.Ordinal) ||
                !string.Equals(leader.InstanceId, instanceId, StringComparison.Ordinal))
            {
                transaction.Commit();
                return new OutboundClaimResult(true, false, null);
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
                return new OutboundClaimResult(true, true, null);
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
                true,
                new OutboundRelayMessage(messageId, claimId, userId, displayName, content, createdAt));
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<NodeMutationResult> CompleteOutboundAsync(
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
            using var transaction = connection.BeginTransaction();
            if (!await IsActiveNodeAsync(connection, transaction, nodeId, cancellationToken))
            {
                transaction.Commit();
                return NodeMutationResult.Unauthorized;
            }

            var nextState = outcome switch
            {
                DeliveryOutcome.Sent => OutboundState.Sent,
                DeliveryOutcome.NotSent => OutboundState.Pending,
                DeliveryOutcome.Ambiguous => OutboundState.Ambiguous,
                _ => throw new ArgumentOutOfRangeException(nameof(outcome))
            };
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
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
            var completed = await update.ExecuteNonQueryAsync(cancellationToken) == 1;
            transaction.Commit();
            return completed ? NodeMutationResult.Completed : NodeMutationResult.Conflict;
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
            using var transaction = connection.BeginTransaction();
            if (!await IsActiveNodeAsync(connection, transaction, nodeId, cancellationToken))
            {
                transaction.Commit();
                return new ObservationResult(false, false);
            }

            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
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
            var inserted = await insert.ExecuteNonQueryAsync(cancellationToken) == 1;
            transaction.Commit();
            return new ObservationResult(true, inserted);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<DiscordPublishWorkItem?> ClaimDiscordPublishAsync(
        string guildId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            using var transaction = connection.BeginTransaction();
            var configuration = await ReadCommunityConfigurationAsync(
                connection, transaction, guildId, cancellationToken);
            if (configuration is null || configuration.IsPaused)
            {
                transaction.Commit();
                return null;
            }

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
                    retry_at_ms = NULL,
                    publish_channel_id = $channelId,
                    publish_config_revision = $configRevision
                WHERE observation_id = $observationId
                  AND publication_state IN ($pending, $retry);
                """;
            update.Parameters.AddWithValue("$inFlight", (int)PublicationState.InFlight);
            update.Parameters.AddWithValue("$claimId", claimId);
            update.Parameters.AddWithValue("$expiresAt", (now + options.PublishLeaseDuration).ToUnixTimeMilliseconds());
            update.Parameters.AddWithValue("$attemptCount", attemptCount);
            update.Parameters.AddWithValue("$channelId", configuration.ChannelId);
            update.Parameters.AddWithValue("$configRevision", configuration.Revision);
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
                configuration.ChannelId,
                configuration.Revision,
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

    public async Task<DiscordPublishRouteCheck> ConfirmDiscordPublishRouteAsync(
        string guildId,
        DiscordPublishWorkItem workItem,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            using var transaction = connection.BeginTransaction();
            await using (var ownership = connection.CreateCommand())
            {
                ownership.Transaction = transaction;
                ownership.CommandText = """
                    SELECT COUNT(*)
                    FROM inbound_observations
                    WHERE observation_id = $observationId
                      AND publication_state = $inFlight
                      AND publish_claim_id = $claimId
                      AND publish_claim_expires_at_ms > $now
                      AND publish_channel_id = $channelId
                      AND publish_config_revision = $configRevision;
                    """;
                ownership.Parameters.AddWithValue("$observationId", workItem.ObservationId);
                ownership.Parameters.AddWithValue("$inFlight", (int)PublicationState.InFlight);
                ownership.Parameters.AddWithValue("$claimId", workItem.PublishClaimId);
                ownership.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
                ownership.Parameters.AddWithValue("$channelId", workItem.ChannelId);
                ownership.Parameters.AddWithValue("$configRevision", workItem.ConfigurationRevision);
                if (Convert.ToInt32(await ownership.ExecuteScalarAsync(cancellationToken)) != 1)
                {
                    transaction.Commit();
                    return DiscordPublishRouteCheck.ClaimLost;
                }
            }

            var configuration = await ReadCommunityConfigurationAsync(
                connection, transaction, guildId, cancellationToken);
            if (configuration is not null &&
                !configuration.IsPaused &&
                configuration.Revision == workItem.ConfigurationRevision &&
                string.Equals(configuration.ChannelId, workItem.ChannelId, StringComparison.Ordinal))
            {
                transaction.Commit();
                return DiscordPublishRouteCheck.Current;
            }

            await using var requeue = connection.CreateCommand();
            requeue.Transaction = transaction;
            requeue.CommandText = """
                UPDATE inbound_observations
                SET publication_state = $pending,
                    publish_claim_id = NULL,
                    publish_claim_expires_at_ms = NULL,
                    publish_attempt_count = MAX(0, publish_attempt_count - 1),
                    publish_channel_id = NULL,
                    publish_config_revision = NULL,
                    publication_error = NULL
                WHERE observation_id = $observationId
                  AND publication_state = $inFlight
                  AND publish_claim_id = $claimId;
                """;
            requeue.Parameters.AddWithValue("$pending", (int)PublicationState.Pending);
            requeue.Parameters.AddWithValue("$inFlight", (int)PublicationState.InFlight);
            requeue.Parameters.AddWithValue("$observationId", workItem.ObservationId);
            requeue.Parameters.AddWithValue("$claimId", workItem.PublishClaimId);
            var safelyRequeued = await requeue.ExecuteNonQueryAsync(cancellationToken) == 1;
            transaction.Commit();
            return safelyRequeued ? DiscordPublishRouteCheck.Requeued : DiscordPublishRouteCheck.ClaimLost;
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

    public async Task<int> CountActiveNodesAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM relay_nodes WHERE revoked_at_ms IS NULL AND token_hash IS NOT NULL;";
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

    private async Task<BridgeManagementResult> ApplyBridgeMutationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        BridgeManagementRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var nowMs = now.ToUnixTimeMilliseconds();
        if (request.Action == BridgeManagementAction.Status)
        {
            var configuration = await ReadCommunityConfigurationAsync(
                connection, transaction, request.GuildId, cancellationToken);
            var response = configuration is null
                ? "The CWLS relay is not configured."
                : $"Relay {(configuration.IsPaused ? "paused" : "running")} in <#{configuration.ChannelId}> for " +
                    (configuration.AllowedRoleId is null ? "everyone in the server" : $"<@&{configuration.AllowedRoleId}>") +
                    $". Preferred node: `{configuration.PreferredNodeId ?? "automatic"}`.";
            return new BridgeManagementResult(true, false, false, response);
        }

        if (request.Action == BridgeManagementAction.ListNodes)
        {
            var nodes = await ReadNodeStatusesAsync(connection, transaction, now, cancellationToken);
            var response = nodes.Count == 0
                ? "No relay nodes are registered."
                : string.Join('\n', nodes.Select(node =>
                    $"`{node.NodeId}` — {RelayText.EscapeDiscordMarkdown(node.Label)}; " +
                    (node.IsRevoked ? "revoked" :
                        !node.IsPaired ? "awaiting pairing" :
                        node.IsLeader && !node.CapabilityReported ? "leader; legacy capability unknown" :
                        node.IsLeader ? "leader" :
                        !node.CapabilityReported ? "legacy; capability unknown" :
                        node.CanSendToGame ? "standby" :
                        "observer; not eligible to send") +
                    (node.IsPreferred ? "; preferred" : string.Empty)));
            if (response.Length > 1900)
            {
                response = $"{response[..1870]}\n…additional nodes omitted.";
            }
            return new BridgeManagementResult(true, false, false, response);
        }

        if (request.Action == BridgeManagementAction.Configure)
        {
            if (!DiscordOptions.IsSnowflake(request.ChannelId ?? string.Empty) ||
                !DiscordOptions.IsSnowflake(request.RoleId ?? string.Empty))
            {
                return Failure("Choose a valid Discord channel and role.");
            }

            await using var configure = connection.CreateCommand();
            configure.Transaction = transaction;
            configure.CommandText = """
                INSERT INTO community_relay_configuration(
                    singleton, guild_id, channel_id, allowed_role_id, is_paused,
                    preferred_node_id, revision, updated_by_discord_user_id, updated_at_ms)
                VALUES (1, $guildId, $channelId, $roleId, 0, NULL, 1, $actorId, $now)
                ON CONFLICT(singleton) DO UPDATE SET
                    guild_id = excluded.guild_id,
                    channel_id = excluded.channel_id,
                    allowed_role_id = excluded.allowed_role_id,
                    revision = community_relay_configuration.revision + 1,
                    updated_by_discord_user_id = excluded.updated_by_discord_user_id,
                    updated_at_ms = excluded.updated_at_ms;
                """;
            configure.Parameters.AddWithValue("$guildId", request.GuildId);
            configure.Parameters.AddWithValue("$channelId", request.ChannelId!);
            configure.Parameters.AddWithValue("$roleId", request.RoleId!);
            configure.Parameters.AddWithValue("$actorId", request.ActorDiscordUserId);
            configure.Parameters.AddWithValue("$now", nowMs);
            await configure.ExecuteNonQueryAsync(cancellationToken);
            return new BridgeManagementResult(
                true, false, false, $"CWLS relay configured for <#{request.ChannelId}> and <@&{request.RoleId}>.");
        }

        if (request.Action is BridgeManagementAction.SetChannel or BridgeManagementAction.SetRole)
        {
            var value = request.Action == BridgeManagementAction.SetChannel ? request.ChannelId : request.RoleId;
            if (!DiscordOptions.IsSnowflake(value ?? string.Empty))
            {
                return Failure(request.Action == BridgeManagementAction.SetChannel
                    ? "Choose a valid Discord channel."
                    : "Choose a valid Discord role.");
            }

            await using var change = connection.CreateCommand();
            change.Transaction = transaction;
            change.CommandText = request.Action == BridgeManagementAction.SetChannel
                ? """
                    UPDATE community_relay_configuration
                    SET channel_id = $value, revision = revision + 1,
                        updated_by_discord_user_id = $actorId, updated_at_ms = $now
                    WHERE singleton = 1 AND guild_id = $guildId;
                    """
                : """
                    UPDATE community_relay_configuration
                    SET allowed_role_id = $value, revision = revision + 1,
                        updated_by_discord_user_id = $actorId, updated_at_ms = $now
                    WHERE singleton = 1 AND guild_id = $guildId;
                    """;
            change.Parameters.AddWithValue("$value", value!);
            change.Parameters.AddWithValue("$actorId", request.ActorDiscordUserId);
            change.Parameters.AddWithValue("$now", nowMs);
            change.Parameters.AddWithValue("$guildId", request.GuildId);
            if (await change.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                return Failure("Configure the relay channel and role first.");
            }

            return new BridgeManagementResult(
                true,
                false,
                false,
                request.Action == BridgeManagementAction.SetChannel
                    ? $"CWLS relay channel changed to <#{value}>."
                    : $"CWLS message access changed to <@&{value}>.");
        }

        if (request.Action is BridgeManagementAction.Pause or BridgeManagementAction.Resume)
        {
            await using var pause = connection.CreateCommand();
            pause.Transaction = transaction;
            pause.CommandText = """
                UPDATE community_relay_configuration
                SET is_paused = $paused,
                    revision = revision + 1,
                    updated_by_discord_user_id = $actorId,
                    updated_at_ms = $now
                WHERE singleton = 1 AND guild_id = $guildId;
                """;
            pause.Parameters.AddWithValue("$paused", request.Action == BridgeManagementAction.Pause ? 1 : 0);
            pause.Parameters.AddWithValue("$actorId", request.ActorDiscordUserId);
            pause.Parameters.AddWithValue("$now", nowMs);
            pause.Parameters.AddWithValue("$guildId", request.GuildId);
            if (await pause.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                return Failure("Configure the relay channel and role first.");
            }

            return new BridgeManagementResult(
                true, false, false, request.Action == BridgeManagementAction.Pause ? "CWLS relay paused." : "CWLS relay resumed.");
        }

        if (request.Action == BridgeManagementAction.AddNode)
        {
            var label = RelayText.Normalize(request.NodeLabel, 80);
            if (label is null)
            {
                return Failure("Provide a node name within 80 characters.");
            }

            var nodeId = $"node-{Base64UrlEncode(RandomNumberGenerator.GetBytes(9))}";
            var pairingCode = GeneratePairingCode();
            await using var add = connection.CreateCommand();
            add.Transaction = transaction;
            add.CommandText = """
                INSERT INTO relay_nodes(node_id, label, token_hash)
                VALUES ($nodeId, $label, NULL);
                INSERT INTO relay_pairing_codes(code_hash, node_id, expires_at_ms)
                VALUES ($codeHash, $nodeId, $expiresAt);
                """;
            add.Parameters.AddWithValue("$nodeId", nodeId);
            add.Parameters.AddWithValue("$label", label);
            add.Parameters.AddWithValue("$codeHash", HashSecret(pairingCode));
            add.Parameters.AddWithValue("$expiresAt", (now + TimeSpan.FromMinutes(10)).ToUnixTimeMilliseconds());
            await add.ExecuteNonQueryAsync(cancellationToken);
            return new BridgeManagementResult(
                true,
                false,
                false,
                $"Node **{RelayText.EscapeDiscordMarkdown(label)}** created. Pair it within 10 minutes using code `{pairingCode}`. This code is shown only here; the durable access token is returned only to the relay node.");
        }

        if (request.Action == BridgeManagementAction.ClearPreference)
        {
            await using var clear = connection.CreateCommand();
            clear.Transaction = transaction;
            clear.CommandText = """
                UPDATE community_relay_configuration
                SET preferred_node_id = NULL,
                    revision = revision + 1,
                    updated_by_discord_user_id = $actorId,
                    updated_at_ms = $now
                WHERE singleton = 1 AND guild_id = $guildId;
                """;
            clear.Parameters.AddWithValue("$actorId", request.ActorDiscordUserId);
            clear.Parameters.AddWithValue("$now", nowMs);
            clear.Parameters.AddWithValue("$guildId", request.GuildId);
            if (await clear.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                return Failure("Configure the relay first.");
            }

            return new BridgeManagementResult(true, false, false, "Node selection returned to automatic failover.");
        }

        if (request.Action == BridgeManagementAction.PreferNode)
        {
            if (string.IsNullOrWhiteSpace(request.NodeId))
            {
                return Failure("Choose a relay node.");
            }

            await using var prefer = connection.CreateCommand();
            prefer.Transaction = transaction;
            prefer.CommandText = """
                UPDATE community_relay_configuration
                SET preferred_node_id = $nodeId,
                    revision = revision + 1,
                    updated_by_discord_user_id = $actorId,
                    updated_at_ms = $now
                WHERE singleton = 1
                  AND guild_id = $guildId
                  AND EXISTS (
                      SELECT 1 FROM relay_nodes
                      WHERE node_id = $nodeId AND revoked_at_ms IS NULL AND token_hash IS NOT NULL);
                """;
            prefer.Parameters.AddWithValue("$nodeId", request.NodeId);
            prefer.Parameters.AddWithValue("$actorId", request.ActorDiscordUserId);
            prefer.Parameters.AddWithValue("$now", nowMs);
            prefer.Parameters.AddWithValue("$guildId", request.GuildId);
            if (await prefer.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                return Failure("That active relay node does not exist, or the relay is not configured.");
            }

            return new BridgeManagementResult(
                true, false, false, $"`{request.NodeId}` will be preferred at the next safe lease turnover.");
        }

        if (request.Action == BridgeManagementAction.RevokeNode)
        {
            if (string.IsNullOrWhiteSpace(request.NodeId))
            {
                return Failure("Choose a relay node.");
            }

            await using var revoke = connection.CreateCommand();
            revoke.Transaction = transaction;
            revoke.CommandText = """
                UPDATE relay_nodes
                SET revoked_at_ms = $now,
                    revoked_by_discord_user_id = $actorId,
                    token_hash = NULL
                WHERE node_id = $nodeId
                  AND revoked_at_ms IS NULL;
                """;
            revoke.Parameters.AddWithValue("$now", nowMs);
            revoke.Parameters.AddWithValue("$actorId", request.ActorDiscordUserId);
            revoke.Parameters.AddWithValue("$nodeId", request.NodeId);
            if (await revoke.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                return Failure("That relay node is unknown or already revoked.");
            }

            await using var fence = connection.CreateCommand();
            fence.Transaction = transaction;
            fence.CommandText = """
                UPDATE community_relay_configuration
                SET preferred_node_id = CASE WHEN preferred_node_id = $nodeId THEN NULL ELSE preferred_node_id END,
                    revision = revision + 1,
                    updated_by_discord_user_id = $actorId,
                    updated_at_ms = $now
                WHERE singleton = 1;
                UPDATE leader_state
                SET epoch = epoch + 1,
                    node_id = NULL,
                    instance_id = NULL,
                    expires_at_ms = 0
                WHERE singleton = 1 AND node_id = $nodeId;
                """;
            fence.Parameters.AddWithValue("$nodeId", request.NodeId);
            fence.Parameters.AddWithValue("$actorId", request.ActorDiscordUserId);
            fence.Parameters.AddWithValue("$now", nowMs);
            await fence.ExecuteNonQueryAsync(cancellationToken);
            return new BridgeManagementResult(true, false, false, $"Relay node `{request.NodeId}` revoked.");
        }

        return Failure("Unsupported bridge management action.");
    }

    private static BridgeManagementResult Failure(string response) => new(false, false, false, response);

    private static async Task<BridgeManagementResult?> ReadManagementReplayAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string interactionId,
        byte[] requestHash,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT request_hash, response, succeeded
            FROM discord_management_interactions
            WHERE interaction_id = $interactionId;
            """;
        command.Parameters.AddWithValue("$interactionId", interactionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var existingHash = (byte[])reader[0];
        if (existingHash.Length != requestHash.Length ||
            !CryptographicOperations.FixedTimeEquals(existingHash, requestHash))
        {
            return new BridgeManagementResult(
                false, true, true, "This Discord interaction ID was already used for a different management request.");
        }

        return new BridgeManagementResult(reader.GetInt64(2) == 1, true, false, reader.GetString(1));
    }

    private static async Task<CommunityRelayConfiguration?> ReadCommunityConfigurationAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string guildId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT guild_id, channel_id, allowed_role_id, is_paused,
                   preferred_node_id, revision, updated_at_ms
            FROM community_relay_configuration
            WHERE singleton = 1 AND guild_id = $guildId;
            """;
        command.Parameters.AddWithValue("$guildId", guildId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new CommunityRelayConfiguration(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetInt64(3) == 1,
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetInt64(5),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)));
    }

    private static async Task<List<RelayNodeStatus>> ReadNodeStatusesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var nodes = new List<RelayNodeStatus>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT n.node_id, n.label, n.token_hash IS NOT NULL,
                   n.capability_reported, n.can_send_to_game, n.revoked_at_ms,
                   CASE WHEN c.preferred_node_id = n.node_id THEN 1 ELSE 0 END,
                   CASE WHEN l.node_id = n.node_id AND l.expires_at_ms > $now THEN 1 ELSE 0 END,
                   n.last_seen_at_ms
            FROM relay_nodes n
            LEFT JOIN community_relay_configuration c ON c.singleton = 1
            CROSS JOIN leader_state l
            ORDER BY n.label, n.node_id;
            """;
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            nodes.Add(new RelayNodeStatus(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2) == 1,
                reader.GetInt64(3) == 1,
                reader.GetInt64(4) == 1,
                !reader.IsDBNull(5),
                reader.GetInt64(6) == 1,
                reader.GetInt64(7) == 1,
                reader.IsDBNull(8) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(8))));
        }

        return nodes;
    }

    private static async Task<(string NodeId, long? LastSeenAtMs, long ConfiguredAtMs)?> ReadPreferredNodeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT c.preferred_node_id, n.last_seen_at_ms, c.updated_at_ms
            FROM community_relay_configuration c
            JOIN relay_nodes n ON n.node_id = c.preferred_node_id
            WHERE c.singleton = 1
              AND n.revoked_at_ms IS NULL
              AND n.can_send_to_game = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetInt64(1), reader.GetInt64(2));
    }

    private async Task SeedConfigurationAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var discord = sapphireOptions.Discord;
        if (!DiscordOptions.IsSnowflake(discord.GuildId) || !DiscordOptions.IsSnowflake(discord.ChannelId))
        {
            return;
        }

        var allowedRoleId = discord.AllowedRoleIds.FirstOrDefault(DiscordOptions.IsSnowflake);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO community_relay_configuration(
                singleton, guild_id, channel_id, allowed_role_id, is_paused,
                preferred_node_id, revision, updated_by_discord_user_id, updated_at_ms)
            VALUES (1, $guildId, $channelId, $roleId, 0, NULL, 1, 'configuration-bootstrap', $now);
            """;
        command.Parameters.AddWithValue("$guildId", discord.GuildId);
        command.Parameters.AddWithValue("$channelId", discord.ChannelId);
        command.Parameters.AddWithValue("$roleId", (object?)allowedRoleId ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task SeedRelayNodesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        foreach (var (nodeId, token) in options.NodeTokens)
        {
            if (string.IsNullOrWhiteSpace(nodeId) || string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO relay_nodes(node_id, label, token_hash)
                VALUES ($nodeId, $label, $tokenHash)
                ON CONFLICT(node_id) DO UPDATE SET token_hash = excluded.token_hash
                WHERE relay_nodes.revoked_at_ms IS NULL;
                """;
            command.Parameters.AddWithValue("$nodeId", nodeId);
            command.Parameters.AddWithValue("$label", nodeId);
            command.Parameters.AddWithValue("$tokenHash", HashSecret(token));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static byte[] HashSecret(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private static string? NormalizePairingCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = new string(value
            .Where(character => character is not '-' and not ' ')
            .Select(char.ToUpperInvariant)
            .ToArray());
        return normalized.Length == 13 && normalized.All(character => "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".Contains(character))
            ? normalized
            : null;
    }

    private static string GeneratePairingCode()
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = RandomNumberGenerator.GetBytes(8);
        var output = new StringBuilder(13);
        ulong buffer = 0;
        var bits = 0;
        foreach (var value in bytes)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                output.Append(alphabet[(int)((buffer >> bits) & 31)]);
            }
        }

        if (bits > 0)
        {
            output.Append(alphabet[(int)((buffer << (5 - bits)) & 31)]);
        }

        return output.ToString();
    }

    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string declaration,
        CancellationToken cancellationToken)
    {
        await using var inspect = connection.CreateCommand();
        inspect.CommandText = $"PRAGMA table_info({tableName});";
        await using var reader = await inspect.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.Ordinal))
            {
                return;
            }
        }

        await reader.DisposeAsync();
        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {declaration};";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> IsActiveNodeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string nodeId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)
            FROM relay_nodes
            WHERE node_id = $nodeId
              AND revoked_at_ms IS NULL
              AND token_hash IS NOT NULL;
            """;
        command.Parameters.AddWithValue("$nodeId", nodeId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
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
