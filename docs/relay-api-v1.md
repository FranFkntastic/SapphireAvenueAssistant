# Relay API v1

Every route is under `/relay/v1/nodes/{nodeId}` and requires `Authorization: Bearer <node-token>`. Node IDs and tokens are configured server-side; a plugin stores only its own node identity and token. Use this API only over HTTPS because the bearer credential grants relay authority.

## Heartbeat and leadership

`POST /heartbeat`

```json
{
  "instanceId": "random-per-client-start"
}
```

The response contains `role`, `epoch`, and `expiresAtUtc`. Heartbeat well before expiry. Only a `leader` may claim Discord-to-game lines; a new leader receives a larger epoch after the old lease expires, and the previous epoch is permanently fenced.

## Discord to game

`POST /outbound/claim`

```json
{
  "instanceId": "random-per-client-start",
  "epoch": 42
}
```

`204` means no work, while `409` means the node no longer owns the current lease. A successful response contains a `messageId`, unique `claimId`, Discord attribution, and normalized content. The plugin should transmit one line such as `[Discord · Display Name] content`, then immediately call `POST /outbound/{messageId}/complete`:

```json
{
  "instanceId": "random-per-client-start",
  "epoch": 42,
  "claimId": "claim-from-the-response",
  "outcome": "sent"
}
```

Valid outcomes are `sent`, `notSent`, and `ambiguous`. Use `notSent` only when the game send was definitely never attempted; it is the sole outcome that requeues a line. A timeout, disconnect, or uncertain game result is `ambiguous` and will not be retried automatically.

## Game to Discord

`POST /observations`

```json
{
  "observationId": "cwls1:stable-event-fingerprint",
  "cwlsSlot": 1,
  "senderName": "Example Person",
  "senderWorld": "Balmung",
  "content": "Hello from the CWLS.",
  "observedAtUtc": "2026-08-02T12:00:00Z"
}
```

All relay nodes may report observations. To collapse the same game line seen by several nodes, `observationId` must be a deterministic digest of the authoritative chat event fields available identically to every client—for example CWLS slot, server event timestamp, raw sender payload, and raw message payload. Do not include node identity or local receipt time. Repeating an observation ID is accepted as a duplicate and creates no second Discord publication.

Discord publication always suppresses mentions. A confirmed rate-limit rejection may retry, but a timeout, connection failure, server error, or success without a returned Discord message ID becomes reconciliation-required instead of risking a duplicate channel post.
