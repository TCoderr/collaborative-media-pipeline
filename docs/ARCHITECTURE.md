# Architecture

The API and worker share the domain and EF Core model. PostgreSQL is authoritative. Object storage holds private source bytes and derivatives; RabbitMQ carries references, never media payloads. The API and worker are separate processes so decoding has bounded memory, CPU and concurrency.

## Domain

A project has an owner and collaborators with explicit Owner, Editor, Reviewer or Viewer capabilities. Assets allocate strictly increasing version numbers using PostgreSQL concurrency checks and unique indexes. Stored source identity, checksum, size, type, creator and version number are immutable in both EF and PostgreSQL. Corrections create another version. State and generated derivatives are separate from immutable source data.

The source lifecycle is Reserved → Stored → Ready. Failed and Abandoned uploads retain tombstones. Invalid media becomes Quarantined. Ready requires successful decoding and all requested derivatives; failed work never becomes ready. Review follows Draft → InReview → Approved or ChangesRequested. Resolved threads are required for approval, and the uploader cannot approve their own version. Reopening review clears current approval but preserves its history. Only an owner may record delivery; delivered versions cannot re-enter review. Delivery records a confirmed handoff, not an automated transfer to an external destination.

Each meaningful state transition writes append-only history and a notification outbox entry in the same transaction. Approval and delivery history includes actor references. Version changes and comment activity share optimistic concurrency so an approval cannot silently win against a newly added review thread.

## Upload and processing

The upload endpoint reserves the asset/version first, streams bytes into a server-generated quarantine key, validates a content signature and declared media type, calculates SHA-256, and commits stored metadata, the processing job and its outbox event together. File names are display data only. A crash between storage and database commits leaves an inspectable reservation or orphan; reconciliation reports it without inventing a successful upload.

JPEG, PNG and still WebP are supported. Default ceilings are 25 MiB compressed bytes, 16 million decoded pixels and 12,000 pixels on either axis. The worker verifies stored length and checksum, probes dimensions before decoding, rejects animated/unsupported/incomplete media, then re-encodes 512px thumbnails and 1600px previews to WebP. Source files and incomplete outputs cannot be downloaded before Ready. Derivatives strip source metadata.

One worker processes one job at a time by default; validated configuration permits at most two. The processing cancellation budget is at most 60 seconds. A separate 90-second watchdog terminates a process whose native decoder does not return; broker delivery remains unacknowledged, and the durable lease expires before another worker can claim it. Container CPU, memory and process limits are additional boundaries. This is bounded image processing, not a general antivirus service.

## Consistency

Outbox publishers claim rows with `FOR UPDATE SKIP LOCKED`, publish durable messages with broker confirms, and mark publication only afterwards. Lease fields are concurrency tokens, so an expired publisher cannot overwrite a newer lease. Confirm ambiguity may cause duplicate delivery; the inbox's unique message/consumer key and durable job claim make that safe. There is no exactly-once transport claim.

Success commits derivative rows, version/job states, inbox, history and notification outbox together. Transient failures retain history and retry through 5, 30 and 120 second TTL queues. Permanent failures and exhausted jobs are terminal and go to the dead-letter queue. Manual retry accepts only terminal transient failures and creates a new message identity with an explicit additional attempt budget; it does not create another version. Permanent invalid uploads require a new valid version.

An upload `Idempotency-Key` reserves its operation before work. Reusing it for the same resource returns that operation's original resource; another scope conflicts. A request with a changed body must use a new key. A reservation interrupted before a response reference is durable returns a conflict for operator inspection, never a second version. Idempotency records are retained, not silently expired into duplicate work.

## Archive and realtime

`GET /api/v1/archive` returns a membership-filtered, paginated projection of real versions, including processing, review, approval and delivery state. Only validated thumbnail URLs reach the spiral. Pending and failed versions remain readable in the list. The existing visual and animation parameters are unchanged.

API replicas receive RabbitMQ notification fan-out and send SignalR project events only to current authorised subscribers. Permission is rechecked on dispatch. Events trigger HTTP refresh; polling and reconnect refresh recover missed or reordered events. Socket delivery is ephemeral and never the source of truth.

OpenTelemetry connects HTTP, database, outbox, broker, processing and realtime operations. Counters and histograms record real attempts, duplicates, failures, latency and approvals; periodic observations read queue depth, pending outbox, quarantine and active reviews from live systems. No production values are seeded.
