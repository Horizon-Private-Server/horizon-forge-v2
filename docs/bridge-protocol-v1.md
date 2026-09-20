# Horizon Forge bridge protocol v1

This protocol carries binary messages between Electron main and the Forge .NET
host over the host's stdin/stdout streams. JSON, object memory layouts, and
platform-native integer layouts are not used on the wire.

## Byte order and limits

- All integers are unsigned little-endian values.
- The fixed header is 24 bytes.
- Protocol version is `1`.
- Payloads are limited to 64 MiB (`67,108,864` bytes).
- ISO images never cross the bridge. Operations pass validated paths instead.
- Data too large for this limit uses an app-owned temporary file with explicit
  lifetime rather than raising the frame limit implicitly.

## Header

| Offset | Size | Field | v1 rule |
| ---: | ---: | --- | --- |
| 0 | 4 | Magic | ASCII `HFG2` (`48 46 47 32`) |
| 4 | 2 | Version | `1` |
| 6 | 1 | Message kind | One value from the table below |
| 7 | 1 | Flags | `0`; all bits are reserved in v1 |
| 8 | 2 | Opcode | One value from the table below |
| 10 | 2 | Status | `0`, except error frames carry an error code |
| 12 | 4 | Request ID | `0` for handshake; nonzero otherwise |
| 16 | 4 | Payload length | `0..67,108,864` |
| 20 | 4 | Reserved | Must be `0` |

Readers validate the complete header before allocating its declared payload.
Unknown values, nonzero reserved fields, and incompatible versions are protocol
errors. A partial header or payload at end-of-stream is malformed input.

## Message kinds

| Value | Name | Direction and purpose |
| ---: | --- | --- |
| 1 | Handshake | Either direction during startup negotiation |
| 2 | Request | Electron to host operation request |
| 3 | Result | Successful terminal response |
| 4 | Progress | Nonterminal progress for a request |
| 5 | Cancel | Electron requests cancellation; payload must be empty |
| 6 | Error | Terminal failure with stable status and message |

The handshake uses request ID `0` and opcode `Control`. Every other v1 frame has
a nonzero request ID. Result, progress, cancellation, and error frames retain the
request ID of the request they describe.

## Opcodes

| Value | Name | Purpose |
| ---: | --- | --- |
| 0 | Control | Handshake and protocol-level errors |
| 1 | Echo | Walking-skeleton request used to qualify the bridge |

Handshake frames must use `Control`. Requests, results, progress, and cancellation
must use a non-control opcode. Error frames may use `Control` when dispatch never
reached a known operation, or the original operation's opcode otherwise. Unknown
opcodes are rejected in protocol v1.

## Status and error codes

Non-error frames have status `0`. Error frames use one of these stable codes:

| Value | Name |
| ---: | --- |
| 1 | InvalidMagic |
| 2 | UnsupportedVersion |
| 3 | InvalidMessageKind |
| 4 | InvalidFlags |
| 5 | UnknownOpcode |
| 6 | InvalidStatus |
| 7 | InvalidRequestId |
| 8 | PayloadTooLarge |
| 9 | InvalidHeader |
| 10 | MalformedPayload |
| 11 | Cancelled |
| 12 | InternalError |

Errors detected before a trustworthy request ID is available terminate the
connection. Otherwise the receiver may return an `Error` frame and then decide
whether the connection remains usable.

## Payloads

Payload schemas are defined per opcode and message kind. Variable-length values
use explicit little-endian byte lengths; C# or JavaScript object layouts must
never be copied directly.

An error payload is exactly:

```text
uint32 utf8ByteLength
byte[utf8ByteLength] message
```

The message must be nonempty, valid UTF-8, and consume the entire payload.

### Handshake payload

The host sends one handshake immediately after startup:

```text
string hostVersion
string sdkRevision
uint32 supportedGameCount
string[supportedGameCount] supportedGames
uint32 capabilityCount
string[capabilityCount] capabilities
```

Each `string` is a `uint32` UTF-8 byte length followed by those bytes. Counts are
limited to 64 and strings to 1 MiB. The initial host advertises game `UYA` and
capabilities `bridge.echo`, `bridge.progress`, and `bridge.cancellation`.

### Echo payloads

Echo is a diagnostic operation used to qualify lifecycle behavior:

```text
Request:  uint32 delayMilliseconds, string message
Progress: uint32 completed, uint32 total
Result:   string message
```

Delay is limited to 60 seconds. A zero delay completes immediately. Cancellation
has no payload and a cancelled operation terminates with error code `Cancelled`.

## Stream behavior

Readers retain partial headers and payloads across reads and may emit multiple
frames from one read. Writers must honor stream backpressure. Standard output is
reserved for frames; diagnostics go to standard error or a log file.

The shared language-neutral vectors in
[`tests/fixtures/bridge-v1.json`](../tests/fixtures/bridge-v1.json) are normative
examples. Both TypeScript and C# tests encode to and decode from those exact bytes.
