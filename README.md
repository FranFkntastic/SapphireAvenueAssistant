# Sapphire Avenue Discord Bridge

Sapphire Avenue Discord Bridge is a fail-closed bridge between a community-owned Discord bot/channel and one FFXIV cross-world linkshell. Sapphire Avenue is the maker's mark, not a server restriction. The repository contains the durable Discord coordinator and `SapphireAvenueRelay`, the Dalamud relay node.

The plugin starts with both directions disabled. A Discord server manager creates a short-lived node code with `/bridge add-node`; the player enters that code and the HTTPS coordinator address in `/sadbridge`, then selects a CWLS by name from the character's current memberships. Every observation and send rechecks the saved slot/name pair, so a reordered or missing CWLS pauses the relay instead of writing into another chat.

Game-bound messages use one epoch-fenced coordinator leader. A send is reported as successful only after the plugin observes the matching CWLS echo from the local character; missing echoes and uncertain failures are sealed as ambiguous rather than retried.

The plugin exposes an authenticated local Agent Bridge manifest and allowlisted semantic commands. `tools/Invoke-RelayBridge.ps1` can read its manifest/snapshot, configure or clear it, enable directions, and perform an explicit verified test send without desktop automation. Node and bridge tokens are protected at rest with Windows DPAPI and are never returned in snapshots.

The first release boundary is deliberately one coordinator deployment per community-owned Discord application and guild. Shared multi-community hosting is not implied: the relay message, observation, and lease tables are not tenant-partitioned yet.

V1 accepts signed `/cwls message` and `/bridge` Discord interactions, persists game-bound work and management revisions, elects exactly one transmitting relay with a short epoch-fenced lease, and publishes idempotent CWLS observations back to the configured Discord channel. A preferred relay node wins an available lease, but never preempts a live sender. The included game plugin consumes that versioned HTTP contract; this repository does not automate Square Enix account creation, login, or client operation.

Run one coordinator process against its local SQLite database. The relay *node* pool is highly available, but coordinator replication is not part of v1; multiple service processes would need a shared transactional authority before they could safely elect one game sender.

## Run locally

The service targets .NET 10. Configure secrets outside source, then run:

```powershell
$env:SapphireAvenue__Discord__PublicKey = '<discord-application-public-key>'
$env:SapphireAvenue__Discord__BotToken = '<discord-bot-token>'
$env:SapphireAvenue__Discord__ApplicationId = '<discord-application-id>'
$env:SapphireAvenue__Discord__GuildId = '<discord-server-id>'
$env:SapphireAvenue__Discord__ChannelId = '<initial-relay-channel-id>'
$env:SapphireAvenue__Discord__AllowedRoleIds__0 = '<initial-message-role-id>'
dotnet run --project .\src\SapphireAvenueAssistant
```

Discord's Interactions Endpoint URL is `https://<host>/discord/interactions`. The coordinator reconciles the guild commands it owns (`cwls` and `bridge`) without deleting unrelated commands. `/bridge` is discoverable to members with Manage Server permission, and the endpoint independently rechecks the current signed permission on every management interaction.

## Configure a relay

1. A server manager runs `/bridge configure` and selects the relay channel and role allowed to use `/cwls message`.
2. The manager runs `/bridge add-node` with a friendly installation name. Discord returns a 13-character, ten-minute pairing code ephemerally.
3. The player opens `/sadbridge`, enters the HTTPS coordinator URL and pairing code, and selects a discovered CWLS by name.
4. The manager may use `/bridge prefer-node` to nominate the relay account. Standbys take over only after the active lease expires.

`/bridge status`, `/bridge list-nodes`, `/bridge pause`, `/bridge clear-preference`, and `/bridge revoke-node` remain available while every FFXIV client is offline. The durable node bearer is returned only to the pairing plugin, stored with Windows DPAPI, and never placed in Discord.

Relay nodes use bearer authentication and the `/relay/v1/nodes/{nodeId}` routes. A node must heartbeat with `canSendToGame`, retain the returned instance/epoch lease, claim one outbound line, and complete the claim as `sent`, `not-sent`, or `ambiguous`. During rolling upgrades, an omitted capability remains temporarily eligible only while `Relay.AllowLegacyHeartbeatWithoutCapability` is enabled; disable that compatibility switch after every node reports the field. An expired claim is deliberately sealed as ambiguous instead of being transmitted twice. Revocation clears the credential, preference, and any current lease for that node.

Expose Discord and relay routes only through HTTPS. A relay bearer token grants permission to publish observations and, while that node owns the current epoch, claim Discord-to-game work.

## Linux service deployment

Publish the coordinator self-contained for the target Linux architecture and keep each immutable release under `/srv/sapphire-avenue-assistant/releases`. Point `/srv/sapphire-avenue-assistant/current` at the active release, retain the SQLite authority under `/srv/sapphire-avenue-assistant/shared`, and install `deploy/sapphire-avenue-assistant.service` as the systemd unit.

The unit reads `/etc/sapphire-avenue-assistant/sapphire-avenue.env`, which must be owned by root and mode `0600`. Set the database path to `/srv/sapphire-avenue-assistant/shared/sapphire-avenue.db`; keep the Discord bot token and per-node tokens only in that file. The included Caddy fragment exposes the service below `/sapphire-avenue/` while Kestrel remains bound to loopback port 5130.

## Configuration

Non-secret defaults live in `appsettings.json`. Supply the community's Discord public key, bot token, application ID, and guild ID through environment variables or a deployment secret store. The bot token never enters Discord or the plugin. `Relay:NodeTokens` remains a legacy deployment bootstrap for existing installations; newly distributed nodes use one-time pairing and hash-only server persistence. Never put Square Enix credentials in this service.
