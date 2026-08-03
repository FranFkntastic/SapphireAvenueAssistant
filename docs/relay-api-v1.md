# Relay API v1

Every authenticated route is under `/relay/v1/nodes/{nodeId}` and requires `Authorization: Bearer <node-token>`. A one-time pairing exchange issues the node identity and token; the plugin stores only its own values. Use this API only over HTTPS because the bearer credential grants relay authority.

## One-time node pairing

An authorized Discord server manager creates a node with `/bridge add-node`. The returned 13-character Base32 code expires after ten minutes and is displayed only in the ephemeral interaction response. Exchange it once over HTTPS:

```http
POST /relay/v1/pair
Content-Type: application/json

{"pairingCode":"ABCDEFG234567"}
```

A successful response returns `nodeId`, `nodeLabel`, and a 256-bit base64url `accessToken`. The coordinator stores only hashes of the pairing code and bearer token. Unknown, expired, consumed, or revoked codes return `401` without credential material. Plain HTTP returns `400`; a TLS-terminating reverse proxy is accepted only from loopback with `X-Forwarded-Proto: https` in the current deployment topology.

## Heartbeat and leadership

`POST /heartbeat`

```json
{
  "instanceId": "random-per-client-start",
  "canSendToGame": true
}
```

The response contains `role`, `isPreferred`, `epoch`, and `expiresAtUtc`. `canSendToGame` must reflect the current node's Discord-to-game direction, login state, and exact CWLS eligibility; an ineligible leader releases and fences its lease. During a rolling upgrade, a legacy client that omits this field is temporarily eligible only while `Relay.AllowLegacyHeartbeatWithoutCapability` is enabled. Disable that compatibility switch after every node has upgraded. Heartbeat well before expiry. Only a `leader` may claim Discord-to-game lines; a new leader receives a larger epoch after the old lease expires, and the previous epoch is permanently fenced. A configured preferred node gets the next available lease but never preempts a live leader.

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
