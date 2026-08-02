# Sapphire Avenue Assistant

Sapphire Avenue Assistant is a fail-closed bridge between one Discord channel and one FFXIV cross-world linkshell. The repository contains the durable Discord/coordinator service and `SapphireAvenueRelay`, a Dalamud relay node.

The plugin starts with both directions disabled. Before it can relay, an operator must configure an HTTPS coordinator (loopback HTTP is accepted for development), a per-node token, a CWLS slot, and the exact expected linkshell name. Every observation and send rechecks that slot/name pair, so a reordered or missing CWLS pauses the relay instead of writing into another chat.

Game-bound messages use one epoch-fenced coordinator leader. A send is reported as successful only after the plugin observes the matching CWLS echo from the local character; missing echoes and uncertain failures are sealed as ambiguous rather than retried.

The plugin exposes an authenticated local Agent Bridge manifest and allowlisted semantic commands. `tools/Invoke-RelayBridge.ps1` can read its manifest/snapshot, configure or clear it, enable directions, and perform an explicit verified test send without desktop automation. Node and bridge tokens are protected at rest with Windows DPAPI and are never returned in snapshots.

Sapphire Avenue Assistant is the Discord-side coordinator for a one-channel FFXIV cross-world linkshell relay.

V1 accepts a signed `/cwls message` Discord interaction, persists it for a game relay, elects exactly one transmitting relay with a short epoch-fenced lease, and publishes idempotent CWLS observations back to the configured Discord channel. The included game plugin consumes that versioned HTTP contract; this repository does not automate Square Enix account creation, login, or client operation.

Run one coordinator process against its local SQLite database. The relay *node* pool is highly available, but coordinator replication is not part of v1; multiple service processes would need a shared transactional authority before they could safely elect one game sender.

## Run locally

The service targets .NET 10. Configure secrets outside source, then run:

```powershell
$env:SapphireAvenue__Discord__PublicKey = '<discord-application-public-key>'
$env:SapphireAvenue__Discord__BotToken = '<discord-bot-token>'
$env:SapphireAvenue__Discord__ApplicationId = '<discord-application-id>'
$env:SapphireAvenue__Discord__GuildId = '<discord-server-id>'
$env:SapphireAvenue__Discord__ChannelId = '<relay-channel-id>'
$env:SapphireAvenue__Relay__NodeTokens__relay_1 = '<random-node-secret>'
dotnet run --project .\src\SapphireAvenueAssistant
```

Discord's Interactions Endpoint URL is `https://<host>/discord/interactions`. Register one guild command named `cwls` with one required string option named `message`; restrict command visibility through Discord permissions as well as the service's optional `AllowedRoleIds` check.

Relay nodes use bearer authentication and the `/relay/v1/nodes/{nodeId}` routes. A node must heartbeat, retain the returned instance/epoch lease, claim one outbound line, and complete the claim as `sent`, `not-sent`, or `ambiguous`. An expired claim is deliberately sealed as ambiguous instead of being transmitted twice.

Expose Discord and relay routes only through HTTPS. A relay bearer token grants permission to publish observations and, while that node owns the current epoch, claim Discord-to-game work.

## Linux service deployment

Publish the coordinator self-contained for the target Linux architecture and keep each immutable release under `/srv/sapphire-avenue-assistant/releases`. Point `/srv/sapphire-avenue-assistant/current` at the active release, retain the SQLite authority under `/srv/sapphire-avenue-assistant/shared`, and install `deploy/sapphire-avenue-assistant.service` as the systemd unit.

The unit reads `/etc/sapphire-avenue-assistant/sapphire-avenue.env`, which must be owned by root and mode `0600`. Set the database path to `/srv/sapphire-avenue-assistant/shared/sapphire-avenue.db`; keep the Discord bot token and per-node tokens only in that file. The included Caddy fragment exposes the service below `/sapphire-avenue/` while Kestrel remains bound to loopback port 5130.

## Configuration

Non-secret defaults live in `appsettings.json`. Supply the Discord public key, bot token, IDs, and each relay-node token through environment variables or a deployment secret store. Never put Square Enix credentials in this service.
