# Sapphire Avenue Assistant

Sapphire Avenue Assistant is the Discord-side coordinator for a one-channel FFXIV cross-world linkshell relay.

V1 accepts a signed `/cwls message` Discord interaction, persists it for a game relay, elects exactly one transmitting relay with a short epoch-fenced lease, and publishes idempotent CWLS observations back to the configured Discord channel. The game plugin is a separate consumer of the versioned HTTP contract; this repository does not automate Square Enix account creation, login, or client operation.

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

## Configuration

Non-secret defaults live in `appsettings.json`. Supply the Discord public key, bot token, IDs, and each relay-node token through environment variables or a deployment secret store. Never put Square Enix credentials in this service.
