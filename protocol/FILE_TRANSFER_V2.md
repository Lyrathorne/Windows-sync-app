# File Transfer V2

File Transfer V2 extends V1 with resumable transfers, per-chunk integrity,
persistent FIFO queues and bounded retry. V1 message names and invariants remain
valid unless this document explicitly strengthens them.

## Capability and negotiation

The capability is `file-transfer-v2`. A peer advertises it only when it supports
every requirement in this document. If either peer lacks it, a new transfer uses
V1 and cannot be resumed after disconnect. There is no mid-transfer downgrade.

## Decisions

- Direction: Android ↔ Windows.
- Maximum file size remains 100 MiB until a later negotiated limit is added.
- Chunk size remains 64 KiB.
- Every `file.chunk` carries `chunkSha256`, encoded as unpadded Base64url.
- Receiver acknowledges every durably written chunk with `file.chunk.received`.
- The acknowledged `(nextChunkIndex, offset)` is the only valid resume point.
- Queue order is FIFO and maximum parallelism is one per authenticated connection.
- Retry delays are 1, 2, 4, 8 and 16 seconds with at most five attempts.
- Queue and active-transfer metadata survive application restart.
- Partial files older than seven days are deleted when the receiver starts.

## New messages

| Type | Direction | Purpose |
|---|---|---|
| `file.chunk.received` | receiver → sender | Durable chunk acknowledgement |
| `file.resume.request` | sender → receiver | Ask for the last durable resume point |
| `file.resume.accepted` | receiver → sender | Return the next chunk index and offset |

The receiver may answer `file.resume.request` with the existing `file.reject` or
`file.error` messages when metadata is missing, stale or inconsistent.

## Payloads

### `file.chunk` in V2

```json
{
  "transferId": "550e8400-e29b-41d4-a716-446655440000",
  "index": 3,
  "offset": 196608,
  "data": "SGVsbG8",
  "chunkSha256": "base64url-sha256"
}
```

### `file.chunk.received`

```json
{
  "transferId": "550e8400-e29b-41d4-a716-446655440000",
  "nextChunkIndex": 4,
  "offset": 262144
}
```

### `file.resume.request`

```json
{
  "transferId": "550e8400-e29b-41d4-a716-446655440000",
  "fileName": "photo.jpg",
  "sizeBytes": 1258291,
  "sha256": "base64url-file-sha256",
  "chunkSize": 65536
}
```

### `file.resume.accepted`

```json
{
  "transferId": "550e8400-e29b-41d4-a716-446655440000",
  "nextChunkIndex": 4,
  "offset": 262144
}
```

## Resume invariants

- `offset == min(nextChunkIndex * chunkSize, sizeBytes)`.
- Receiver acknowledges only after the bytes and transfer metadata are flushed.
- Sender never resumes beyond its last stored acknowledgement.
- Metadata in `file.resume.request` must exactly match the original offer.
- Receiver recomputes the whole-file SHA-256 from the partial file before commit.
- Duplicate chunks below the acknowledged offset are idempotently acknowledged.
- Conflicting duplicates fail the transfer with `resume_conflict`.
- A completed `transferId` is idempotent: repeated `file.complete` returns the same
  `file.received` and never creates another file.

## Persistent metadata

At minimum persist transfer ID, direction, peer ID, file identity, total size,
chunk size, next chunk index, acknowledged offset, state, retry count, timestamps
and the local source/destination reference. Metadata must be written atomically.
Secrets and file contents are never stored in transfer metadata.

## Retry and cleanup

Only disconnects, temporary I/O failures and timeouts are retryable. Protocol,
checksum, permission and user-rejection failures are terminal. After five failed
attempts the queue item becomes `Failed`. On startup, receiver deletes orphaned
partial files and metadata older than seven days; completed destination files are
never removed by cleanup.
