# DeviceSync File Transfer V1

## Scope

File Transfer V1 supports Android-to-Windows transfers only. Messages use the
existing Protocol V1 JSON envelope and `[4-byte big-endian length][UTF-8 JSON]`
framing. Transfer messages are allowed only after authentication succeeds and
both peers advertise `file-transfer-v1`.

V1 supports one non-terminal transfer per connection, a maximum file size of
100 MiB (`104857600` bytes), fixed 64 KiB (`65536` byte) chunks, and Base64
chunk data inside JSON. Resume, retries, queues, and concurrent transfers are
out of scope.

## Message flow

```text
Android                Windows
   | file.offer           |
   |--------------------->|
   | file.accept/reject   |
   |<---------------------|
   | file.chunk (0..n)    |
   |--------------------->|
   | file.complete        |
   |--------------------->|
   | file.received/error  |
   |<---------------------|
```

`file.accept.correlationId` references the offer message ID.
`file.received.correlationId` references the complete message ID. All file
messages set `requiresAcknowledgement` to `false` because their application
responses are explicit.

## Encoding

- `transferId` is a canonical UUID string and is identical in every message.
- `sizeBytes` and `offset` are signed 64-bit JSON integers.
- `index`, `totalChunks`, and `chunkSize` are signed 32-bit JSON integers.
- `data` is standard padded Base64 of the raw chunk bytes.
- `sha256` is unpadded Base64url of the 32-byte whole-file SHA-256 digest.
- `mimeType` is required; unknown content uses `application/octet-stream`.

## Payloads

### `file.offer`

```json
{
  "transferId": "550e8400-e29b-41d4-a716-446655440000",
  "fileName": "hello.txt",
  "sizeBytes": 11,
  "mimeType": "text/plain",
  "sha256": "ZOyIygCyaOW6GjVnihtTFtIS9PNmskdyMlNKiuyjfzw",
  "chunkSize": 65536
}
```

### `file.accept`

```json
{ "transferId": "550e8400-e29b-41d4-a716-446655440000" }
```

### `file.reject`

```json
{
  "transferId": "550e8400-e29b-41d4-a716-446655440000",
  "code": "user_rejected",
  "message": "Declined by the user."
}
```

### `file.chunk`

```json
{
  "transferId": "550e8400-e29b-41d4-a716-446655440000",
  "index": 0,
  "offset": 0,
  "data": "SGVsbG8gd29ybGQ="
}
```

### `file.complete`

```json
{
  "transferId": "550e8400-e29b-41d4-a716-446655440000",
  "totalChunks": 1,
  "sizeBytes": 11
}
```

### `file.received`

```json
{
  "transferId": "550e8400-e29b-41d4-a716-446655440000",
  "sizeBytes": 11,
  "sha256": "ZOyIygCyaOW6GjVnihtTFtIS9PNmskdyMlNKiuyjfzw",
  "savedFileName": "hello.txt"
}
```

### `file.cancel`

```json
{
  "transferId": "550e8400-e29b-41d4-a716-446655440000",
  "reason": "user_cancelled"
}
```

### `file.error`

```json
{
  "transferId": "550e8400-e29b-41d4-a716-446655440000",
  "code": "checksum_mismatch",
  "message": "Hash mismatch."
}
```

Reject codes include `user_rejected`, `offer_timeout`, `busy`,
`file_too_large`, `invalid_file_name`, `unsupported_chunk_size`,
`insufficient_space`, and `unsupported_direction`.

Error codes include `invalid_state`, `duplicate_transfer_id`,
`transfer_id_mismatch`, `invalid_chunk_index`, `invalid_chunk_offset`,
`invalid_chunk_data`, `size_exceeded`, `size_mismatch`,
`total_chunks_mismatch`, `complete_before_all_bytes`, `checksum_mismatch`,
`chunk_timeout`, `completion_timeout`, and `io_error`.

## Invariants

- The next chunk index starts at zero and increments by one.
- The next offset equals the number of raw bytes already accepted.
- Every non-final chunk decodes to 65536 bytes; the final chunk may be shorter.
- Received bytes never exceed the offered size.
- `file.complete` is valid only after exactly `sizeBytes` bytes arrive.
- `totalChunks` equals `ceil(sizeBytes / 65536)`; an empty file has zero chunks.
- The computed whole-file SHA-256 must match the offer.
- Unknown, rejected, cancelled, failed, or completed transfers reject chunks.
- A repeated transfer ID must never overwrite a file.

## Timeouts and disconnect

- Offer decision timeout: 60 seconds after `file.offer` is received.
- Chunk inactivity timeout: 30 seconds after accept or the preceding chunk.
- Complete timeout: 10 seconds after exactly `sizeBytes` bytes arrive.
- Receipt timeout: 30 seconds after Android sends `file.complete`.

Disconnect terminates the transfer. V1 never resumes it. Windows closes and
best-effort deletes the `.part` file; Android must use a new transfer ID to
retry.

## Safe file handling

`fileName` is untrusted metadata, never a path. Windows rejects or replaces
path separators, control characters, invalid Windows filename characters,
`.`/`..`, trailing spaces/dots, and reserved device names. It resolves the
final path under the configured receive directory and verifies that the path
does not escape it. Existing files are never overwritten; collisions receive
a suffix such as ` (1)`.

Windows writes to `.devicesync-{transferId}.part` in the destination directory,
updates SHA-256 while streaming, flushes and closes the file, verifies size and
hash, then performs a same-volume atomic move without overwrite. Only then may
it send `file.received`.

## Security

SHA-256 protects integrity, not confidentiality. Until the authenticated
connection is protected by TLS, names and file contents are visible on the
local network. MIME type and extension are untrusted and must not cause Windows
to execute or open the file automatically.

## Test vector

The complete `Hello world` happy path and terminal failure messages live in
`protocol/test-vectors/file-transfer/`. `Hello world` is 11 bytes, encodes as
`SGVsbG8gd29ybGQ=`, and has SHA-256 Base64url
`ZOyIygCyaOW6GjVnihtTFtIS9PNmskdyMlNKiuyjfzw`.
