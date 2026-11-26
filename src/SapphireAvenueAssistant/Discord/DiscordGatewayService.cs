using System.Net.WebSockets;
using System.Text.Json;
using SapphireAvenueAssistant.Configuration;

namespace SapphireAvenueAssistant.Discord;

// Minimal hand-rolled Discord Gateway (v10, JSON) client. It only receives MESSAGE_CREATE for
// ordinary relay-channel ingestion; the gateway never sends anything to Discord channels.
public sealed class DiscordGatewayService(
    IDiscordApiClient discord,
    ChannelMessageIngestor ingestor,
    SapphireAvenueOptions options,
    TimeProvider timeProvider,
    ILogger<DiscordGatewayService> logger) : BackgroundService
{
    // GUILD_MESSAGES | MESSAGE_CONTENT (privileged). Nothing else is requested.
    public const int Intents = (1 << 9) | (1 << 15);

    private const int MaximumPayloadBytes = 16 * 1024 * 1024;
    private const WebSocketCloseStatus ResumableCloseStatus = (WebSocketCloseStatus)4900;
    private static readonly TimeSpan MaximumBackoff = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim sendLock = new(1, 1);
    private volatile string status = "disconnected";

    // Resume state survives reconnects within this process only; a restart identifies afresh and
    // relies on the "msg:<id>" idempotency key for anything Discord redelivers.
    private string? sessionId;
    private string? resumeGatewayUrl;
    private long sequence = -1;

    public string Status => status;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Discord.CanPublish)
        {
            status = "disabled";
            logger.LogWarning("Discord gateway is disabled: channel-message ingestion needs the bot token, application ID, public key, and guild ID.");
            return;
        }

        var backoff = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            status = "connecting";
            ConnectionResult result;
            try
            {
                result = await RunConnectionAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Discord gateway connection failed.");
                result = ConnectionResult.Retry();
            }

            if (result.FatalReason is { } fatal)
            {
                status = $"fatal:{fatal}";
                logger.LogError(
                    "Discord gateway stopped permanently ({Reason}): {Detail} Ordinary channel messages will not be relayed until this is fixed and the coordinator is restarted; /cwls message is unaffected.",
                    fatal,
                    result.Detail);
                return;
            }

            status = "disconnected";
            if (result.WasReady)
            {
                backoff = TimeSpan.FromSeconds(1);
            }

            var delay = result.Delay ?? backoff + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000));
            if (result.Delay is null)
            {
                backoff = TimeSpan.FromTicks(Math.Min(MaximumBackoff.Ticks, backoff.Ticks * 2));
            }

            try
            {
                await Task.Delay(delay, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        status = "disconnected";
    }

    private async Task<ConnectionResult> RunConnectionAsync(CancellationToken stoppingToken)
    {
        var resuming = sessionId is not null && resumeGatewayUrl is not null && Interlocked.Read(ref sequence) >= 0;
        string baseUrl;
        if (resuming)
        {
            baseUrl = resumeGatewayUrl!;
        }
        else
        {
            var info = await discord.GetGatewayBotAsync(stoppingToken);
            if (info.Unauthorized)
            {
                return ConnectionResult.Fatal(
                    "authentication-failed",
                    "Discord rejected the bot token (HTTP 401 from /gateway/bot). Check SapphireAvenue__Discord__BotToken.");
            }

            if (info.Url is null)
            {
                logger.LogWarning("Discord gateway discovery failed: {Error}", info.Error);
                return ConnectionResult.Retry();
            }

            if (info.SessionStartRemaining == 0 && info.SessionStartResetAfter is { } resetAfter)
            {
                logger.LogWarning(
                    "Discord session-start limit is exhausted; waiting {Delay} before identifying again.",
                    resetAfter);
                return ConnectionResult.Retry(resetAfter);
            }

            baseUrl = info.Url;
        }

        var uri = new Uri($"{baseUrl.TrimEnd('/')}/?v=10&encoding=json");
        using var socket = new ClientWebSocket();
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
        {
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            await socket.ConnectAsync(uri, connectTimeout.Token);
        }

        var heartbeat = new HeartbeatState();
        Task? heartbeatTask = null;
        var wasReady = false;
        try
        {
            while (true)
            {
                var frame = await ReceiveAsync(socket, connection.Token);
                if (frame is null)
                {
                    return HandleClose(socket.CloseStatus, socket.CloseStatusDescription, wasReady);
                }

                using var document = frame;
                var root = document.RootElement;
                var op = root.TryGetProperty("op", out var opElement) && opElement.TryGetInt32(out var opValue)
                    ? opValue
                    : -1;
                switch (op)
                {
                    case 10: // HELLO
                        var interval = TimeSpan.FromMilliseconds(
                            root.GetProperty("d").GetProperty("heartbeat_interval").GetDouble());
                        heartbeatTask = RunHeartbeatAsync(socket, interval, heartbeat, connection);
                        if (resuming)
                        {
                            await SendAsync(socket, new
                            {
                                op = 6,
                                d = new
                                {
                                    token = options.Discord.BotToken,
                                    session_id = sessionId,
                                    seq = Interlocked.Read(ref sequence)
                                }
                            }, connection.Token);
                        }
                        else
                        {
                            await SendAsync(socket, new
                            {
                                op = 2,
                                d = new
                                {
                                    token = options.Discord.BotToken,
                                    intents = Intents,
                                    properties = new
                                    {
                                        os = OperatingSystem.IsWindows() ? "windows" : "linux",
                                        browser = "sapphire-avenue-discord-bridge",
                                        device = "sapphire-avenue-discord-bridge"
                                    }
                                }
                            }, connection.Token);
                        }

                        break;
                    case 11: // HEARTBEAT_ACK
                        heartbeat.Acknowledged = true;
                        break;
                    case 1: // server-requested heartbeat
                        await SendHeartbeatAsync(socket, connection.Token);
                        break;
                    case 7: // RECONNECT
                        logger.LogInformation("Discord requested a gateway reconnect; resuming.");
                        await CloseQuietlyAsync(socket, ResumableCloseStatus);
                        return ConnectionResult.Retry(TimeSpan.Zero, wasReady);
                    case 9: // INVALID_SESSION
                        var resumable = root.TryGetProperty("d", out var resumableElement) &&
                            resumableElement.ValueKind == JsonValueKind.True;
                        if (!resumable)
                        {
                            ClearSession();
                        }

                        logger.LogInformation(
                            "Discord invalidated the gateway session (resumable: {Resumable}).",
                            resumable);
                        await CloseQuietlyAsync(socket, ResumableCloseStatus);
                        return ConnectionResult.Retry(
                            TimeSpan.FromMilliseconds(Random.Shared.Next(1000, 5001)),
                            wasReady);
                    case 0: // DISPATCH
                        if (root.TryGetProperty("s", out var seqElement) && seqElement.TryGetInt64(out var seq))
                        {
                            Interlocked.Exchange(ref sequence, seq);
                        }

                        var eventName = root.TryGetProperty("t", out var typeElement) &&
                            typeElement.ValueKind == JsonValueKind.String
                            ? typeElement.GetString()
                            : null;
                        if (!root.TryGetProperty("d", out var data))
                        {
                            break;
                        }

                        if (eventName == "READY")
                        {
                            sessionId = data.GetProperty("session_id").GetString();
                            resumeGatewayUrl = data.GetProperty("resume_gateway_url").GetString();
                            wasReady = true;
                            status = "ready";
                            logger.LogInformation("Discord gateway ready (channel-message ingestion active).");
                        }
                        else if (eventName == "RESUMED")
                        {
                            wasReady = true;
                            status = "ready";
                            logger.LogInformation("Discord gateway session resumed.");
                        }
                        else if (eventName == "MESSAGE_CREATE")
                        {
                            try
                            {
                                await ingestor.HandleMessageCreateAsync(data, connection.Token);
                            }
                            catch (Exception exception) when (exception is not OperationCanceledException)
                            {
                                logger.LogError(exception, "Failed to ingest a Discord channel message.");
                            }
                        }

                        break;
                }
            }
        }
        catch (Exception exception) when (
            heartbeat.Zombie &&
            !stoppingToken.IsCancellationRequested &&
            exception is WebSocketException or OperationCanceledException)
        {
            logger.LogWarning("Discord gateway heartbeat was not acknowledged; reconnecting to resume.");
            return ConnectionResult.Retry(TimeSpan.Zero, wasReady);
        }
        catch (WebSocketException exception) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Discord gateway socket failed; reconnecting.");
            return ConnectionResult.Retry(wasReady: wasReady);
        }
        finally
        {
            await connection.CancelAsync();
            if (heartbeatTask is not null)
            {
                try
                {
                    await heartbeatTask;
                }
                catch (Exception exception)
                {
                    // The heartbeat loop ends with its connection; never let its teardown
                    // replace the connection result being returned.
                    if (exception is not (OperationCanceledException or WebSocketException))
                    {
                        logger.LogDebug(exception, "Discord gateway heartbeat ended with an error.");
                    }
                }
            }
        }
    }

    private ConnectionResult HandleClose(WebSocketCloseStatus? closeStatus, string? description, bool wasReady)
    {
        var code = (int?)closeStatus ?? 0;
        switch (code)
        {
            case 4004:
                return ConnectionResult.Fatal(
                    "authentication-failed",
                    "Discord closed the gateway with 4004: the bot token is invalid. Check SapphireAvenue__Discord__BotToken.");
            case 4010:
            case 4011:
                return ConnectionResult.Fatal(
                    "sharding",
                    $"Discord closed the gateway with {code} ({description}): this coordinator does not shard.");
            case 4012:
                return ConnectionResult.Fatal(
                    "invalid-api-version",
                    $"Discord closed the gateway with 4012 ({description}).");
            case 4013:
                return ConnectionResult.Fatal(
                    "invalid-intents",
                    $"Discord closed the gateway with 4013 ({description}): the requested intents value is invalid.");
            case 4014:
                return ConnectionResult.Fatal(
                    "disallowed-intents",
                    "Discord closed the gateway with 4014: enable the Message Content privileged intent in the Developer Portal (Bot -> Privileged Gateway Intents).");
            case 4007: // invalid seq
            case 4009: // session timed out
                ClearSession();
                break;
        }

        logger.LogInformation(
            "Discord gateway closed ({Code}: {Description}); reconnecting.",
            code,
            description);
        return ConnectionResult.Retry(wasReady: wasReady);
    }

    private async Task RunHeartbeatAsync(
        ClientWebSocket socket,
        TimeSpan interval,
        HeartbeatState heartbeat,
        CancellationTokenSource connection)
    {
        var token = connection.Token;
        await Task.Delay(interval * Random.Shared.NextDouble(), timeProvider, token);
        while (!token.IsCancellationRequested)
        {
            if (!heartbeat.Acknowledged)
            {
                // Zombied connection: abort without a normal close so the session stays resumable.
                heartbeat.Zombie = true;
                socket.Abort();
                await connection.CancelAsync();
                return;
            }

            heartbeat.Acknowledged = false;
            await SendHeartbeatAsync(socket, token);
            await Task.Delay(interval, timeProvider, token);
        }
    }

    private Task SendHeartbeatAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var current = Interlocked.Read(ref sequence);
        return SendAsync(socket, new { op = 1, d = current >= 0 ? current : (long?)null }, cancellationToken);
    }

    private async Task SendAsync<T>(ClientWebSocket socket, T payload, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await sendLock.WaitAsync(cancellationToken);
        try
        {
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        }
        finally
        {
            sendLock.Release();
        }
    }

    private static async Task<JsonDocument?> ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            message.Write(buffer, 0, result.Count);
            if (message.Length > MaximumPayloadBytes)
            {
                throw new WebSocketException("Discord gateway payload exceeded the size limit.");
            }

            if (result.EndOfMessage)
            {
                break;
            }
        }

        message.Position = 0;
        return await JsonDocument.ParseAsync(message, cancellationToken: cancellationToken);
    }

    private static async Task CloseQuietlyAsync(ClientWebSocket socket, WebSocketCloseStatus closeStatus)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await socket.CloseOutputAsync(closeStatus, "reconnecting", timeout.Token);
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException)
        {
            // A reconnect follows regardless.
        }
    }

    private void ClearSession()
    {
        sessionId = null;
        resumeGatewayUrl = null;
        Interlocked.Exchange(ref sequence, -1);
    }

    private sealed class HeartbeatState
    {
        public volatile bool Acknowledged = true;
        public volatile bool Zombie;
    }

    private sealed record ConnectionResult(
        TimeSpan? Delay,
        bool WasReady,
        string? FatalReason = null,
        string? Detail = null)
    {
        public static ConnectionResult Retry(TimeSpan? delay = null, bool wasReady = false) => new(delay, wasReady);

        public static ConnectionResult Fatal(string reason, string detail) => new(null, false, reason, detail);
    }
}
