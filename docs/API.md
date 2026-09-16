# API contracts

All resource endpoints use `/api/v1`. Session cookies are HttpOnly; production cookies are Secure. Obtain a token from `GET /api/v1/auth/csrf` and return its `headerName`/`token` on authenticated mutations. Register/login take email and password (registration also accepts displayName); session inspection and logout are available under `/auth`.

| Area | Routes |
| --- | --- |
| Projects | GET/POST `/projects`; GET/PATCH `/projects/{ref}`; POST `/{ref}/status`, `/{ref}/owner`; POST/PATCH/DELETE members |
| Assets | GET `/projects/{ref}/assets`; multipart POST to the same route; GET `/assets/{ref}` |
| Versions | GET/POST `/assets/{ref}/versions`; GET `/versions/{ref}`; GET `/{ref}/content?derivative=Thumbnail` or `Preview` (omit derivative for source) |
| Processing | GET `/processing-jobs/{ref}` and `/{ref}/history`; POST `/versions/{ref}/reprocess` |
| Reviews | POST `/versions/{ref}/review`; GET/POST `/{ref}/comments`; POST `/comments/{ref}/resolution` |
| Approvals | GET/POST `/versions/{ref}/approvals` |
| Deliveries | GET/POST `/versions/{ref}/deliveries` |
| History | GET `/versions/{ref}/history` |
| Archive | GET `/archive?limit=100&cursor=...` |
| Health | GET `/health/live`, `/health/ready` (also exposed without `/api/v1` for container probes) |

Multipart uploads use `file` and an asset `title`. Send `Idempotency-Key` on retryable upload creation. Version detail returns an ETag. Review changes, approval and delivery require that ETag in `If-Match`; reload after 409 and explicitly reconsider the operation. A missing version precondition is rejected. Responses include public references, never source keys or local paths.

Reviews accept `{ "reviewState": "InReview" }` (also Draft, ChangesRequested and Approved). Approval POST accepts an empty JSON object. A comment takes `{ "body": "...", "parentRef": null }`; a reply may reference an open root thread on the same version. Resolution takes `{ "resolved": true }` and is restricted to the thread author or project owner. Approvals require every root thread resolved and a reviewer other than the uploader. Delivery POST records the owner's confirmation with its actor and timestamp.

Project events arrive through `/hubs/collaboration`. After cookie authentication, invoke `SubscribeToProject(projectRef)` and listen for `ProjectEvent`. Refresh the relevant HTTP resource after an event or reconnect. History and collection responses are bounded; follow `nextCursor` where returned. The archive viewer caps itself at 2,000 loaded records and shows an error instead of silently truncating a larger archive.

Unauthenticated requests return 401, inaccessible references return 404, conflicting edits return 409, invalid lifecycle operations return 422, and rate limits return 429. Error bodies contain a stable code, user-safe message and correlation information. Private media responses use `Cache-Control: no-store`; source downloads use attachment disposition.
