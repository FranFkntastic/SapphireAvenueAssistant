# Sapphire Avenue Discord Bridge

Sapphire Avenue Discord Bridge is a fail-closed bridge between a community-owned Discord bot/channel and one FFXIV cross-world linkshell. Sapphire Avenue is the maker's mark, not a server restriction. The repository contains the durable Discord coordinator and `SapphireAvenueRelay`, the Dalamud relay node.

The plugin starts with both directions disabled. A Discord server manager creates a short-lived connection string with `/bridge add-node`; the player pastes that single value into `/sadbridge`, then selects a CWLS by name from the character's current memberships. Every observation and send rechecks the saved slot/name pair, so a reordered or missing CWLS pauses the relay instead of writing into another chat.

Game-bound messages use one epoch-fenced coordinator leader. A send is reported as successful only after the plugin observes the matching CWLS echo from the local character; missing echoes and uncertain failures are sealed as ambiguous rather than retried. Node tokens are protected at rest with Windows DPAPI.

The first release boundary is deliberately one coordinator deployment per community-owned Discord application and guild. Shared multi-community hosting is not implied: the relay message, observation, and lease tables are not tenant-partitioned yet.

V1 accepts ordinary messages typed in the relay channel, plus signed `/cwls message` and `/bridge` Discord interactions. It persists game-bound work and management revisions, and elects exactly one relay with a short epoch-fenced lease. Only that leader may send into the game or publish observed CWLS lines back to Discord; standby input is discarded. A preferred relay node wins an available lease, but never preempts a live leader. The included game plugin consumes that versioned HTTP contract; this repository does not automate Square Enix account creation, login, or client operation.

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
$env:SapphireAvenue__Relay__PublicBaseUrl = 'https://relay.example/community/'
dotnet run --project .\src\SapphireAvenueAssistant
```

Discord's Interactions Endpoint URL is `https://<host>/discord/interactions`. The coordinator reconciles the guild commands it owns (`cwls` and `bridge`) without deleting unrelated commands. `/bridge` is discoverable to members with Manage Server permission, and the endpoint independently rechecks the current signed permission on every management interaction.

## Discord application setup

In the Discord Developer Portal, open the application's **Bot** page and enable the **Message Content Intent** under *Privileged Gateway Intents*. The coordinator reads relay-channel messages over the Discord Gateway with only the Guild Messages and Message Content intents. If the privileged intent is off, the gateway stops with `fatal:disallowed-intents` on `/healthz`, and the log tells you to enable it. `/cwls message` keeps working either way. Restart the coordinator after you change the intent.

In the relay channel, the bot needs **View Channel**, **Send Messages**, **Read Message History**, and **Add Reactions**.

## Relaying from Discord

Anyone with the configured role can type an ordinary message in the relay channel and it is sent to the CWLS as `[Discord · Name] text`. Mentions, custom emoji, and timestamps become readable text first. Messages that only contain attachments, along with bot and webhook messages, are ignored. Edits and deletions are not relayed. `/cwls message` still works and posts the game confirmation line in the channel as before.

The bot reacts to each relay-channel message with what happened:

| Reaction | Meaning |
|---|---|
| ✅ | Sent. The relay saw the line appear in the CWLS. Only the latest delivered message keeps its ✅; the bot removes it from older ones. Other reactions stay. |
| ⚠️ | Uncertain. The game may or may not have shown it, so the line is not retried. |
| ❌ | Not sent. The relay could not send it. |
| ⌛ | Expired. No relay picked it up within `Relay:ChannelMessageMaxQueueSeconds` (default 120 seconds), so it will not arrive late. |
| ✂️ | Too long for one CWLS line, including the `[Discord · Name]` prefix. Nothing was cut or sent. Shorten it and send it again. |
| ⏸️ | The relay is paused. |
| 🚫 | You do not have the role that is allowed to relay. |

Reactions come from durable state, so a coordinator restart does not lose them. They never hold up delivery. Relay plugins older than 0.4.1.0 still report their own echo, which means channel messages show up twice until those nodes update.

### Pinging Discord members from the game

Type `@Name` in the CWLS exactly as the person appears in Discord: their server nickname, display name, or username. Case does not matter. If exactly one member has that name, the relayed line pings them. Unknown or ambiguous names stay plain text, and roles, `@everyone`, and `@here` never ping. Set `Discord:ResolveGameMentions` to `false` to turn this off.

## Configure a relay

1. A server manager runs `/bridge configure` and selects the relay channel and the role allowed to relay, both by typing in the channel and with `/cwls message`.
2. The manager runs `/bridge add-node`. Discord returns one ephemeral `SADB1 <HTTPS coordinator> <ten-minute code>` connection string; after pairing, the node names itself from the logged-in `Character @ Home World`.
3. The player opens `/sadbridge`, pastes the entire connection string, and selects a discovered CWLS by name. The plugin validates the visible HTTPS destination, refuses redirects, and saves it only after the one-time exchange succeeds.
4. The manager may use `/bridge prefer-node` to nominate the relay account from character/world autocomplete. Standbys take over only after the active lease expires.

`/bridge status`, `/bridge list-nodes`, `/bridge pause`, `/bridge clear-preference`, and `/bridge revoke-node` remain available while every FFXIV client is offline. The durable node bearer is returned only to the pairing plugin, stored with Windows DPAPI, and never placed in Discord.

Relay nodes use bearer authentication and the internal `/relay/v1/nodes/{nodeId}` routes. A node heartbeats with its current character name, canonical home-world ID/name, and `canSendToGame`, then retains the returned instance/epoch lease, claims one outbound line, and completes the claim as `sent`, `not-sent`, or `ambiguous`. Character/world is display context, never authority; duplicate claims revoke and fence the newer installation. During rolling upgrades, omitted modern heartbeat fields remain temporarily eligible only while `Relay.AllowLegacyHeartbeatWithoutCapability` is enabled; disable that compatibility switch after every node reports them. An expired claim is deliberately sealed as ambiguous instead of being transmitted twice.

Expose Discord and relay routes only through HTTPS. A relay bearer token authenticates the node, but only the current unexpired node/instance/epoch lease may publish observations or claim Discord-to-game work.

## Linux service deployment

Publish the coordinator self-contained for the target Linux architecture and keep each immutable release under `/srv/sapphire-avenue-assistant/releases`. Point `/srv/sapphire-avenue-assistant/current` at the active release, retain the SQLite authority under `/srv/sapphire-avenue-assistant/shared`, and install `deploy/sapphire-avenue-assistant.service` as the systemd unit.

The unit reads `/etc/sapphire-avenue-assistant/sapphire-avenue.env`, which must be owned by root and mode `0600`. Set the database path to `/srv/sapphire-avenue-assistant/shared/sapphire-avenue.db`; keep the Discord bot token and per-node tokens only in that file. The included Caddy fragment exposes the service below `/sapphire-avenue/` while Kestrel remains bound to loopback port 5130.

## Configuration

Non-secret defaults live in `appsettings.json`. Supply the community's Discord public key, bot token, application ID, and guild ID through environment variables or a deployment secret store. The bot token never enters Discord or the plugin. `Relay:NodeTokens` remains a legacy deployment bootstrap for existing installations; newly distributed nodes use one-time pairing and hash-only server persistence. Never put Square Enix credentials in this service.
