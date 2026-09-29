# Architecture decisions and failure analysis

## Scope

Demonstrate reliable claim intake and integration orchestration using synthetic identifiers and a deterministic gateway simulator. The platform does not decide coverage, clinical suitability, reimbursement, or eligibility. `Integrated` means only that the simulated integration acknowledged the message.

## ADR 001 Atomic claim intake with an internal outbox

**Decision:** Persist the claim, idempotency key, pending event, and initial audit entry in a single immediate SQLite transaction.

**Why:** A claim must not be accepted without durable work to process it. A process crash before commit leaves none of these records committed; after commit, the polling worker can discover the event. SQLite foreign keys and unique indexes enforce relationships and business-key uniqueness.

**Tradeoff:** SQLite is easy to run and inspect, but serializes writers. The demonstration uses immediate transactions and a ten-second lock timeout. Production throughput requirements may justify PostgreSQL or SQL Server plus a separate outbox dispatcher and message broker.

## ADR 002 Separate transport and business idempotency

**Decision:** Check a global transport key first, then `(source, externalClaimId)`. Identical payloads return the existing claim. Changed payloads return a conflict rather than silently overwriting data. A new transport key for an existing identical business claim is linked to the original claim.

**Canonical representation:** Whitespace is trimmed from source, external ID, and member ID. Amounts use integer cents in the hash. Field order is fixed by the canonical object. The scenario is included because it changes demonstration processing behavior.

**Tradeoff:** IDs are case-sensitive and keys have no expiration. There is no tenant model. Production scope must include an authenticated tenant/client and a deliberate key-retention policy. A SHA-256 payload fingerprint is an equality check, not encryption.

## ADR 003 Consume simulated events transactionally

**Decision:** Select a due event and commit its simulated outcome, claim status, attempt count, and audit entries within one immediate transaction. Another worker waits and then observes the updated event state.

**Why:** The simulated gateway is pure local logic and fast. No network calls occur while holding the write lock. A crash rolls back an incomplete attempt; a later worker can process the event.

**Limit:** This is not a general exactly-once messaging solution. A real external gateway cannot be rolled back with SQLite. Replace the simulator with a leased event worker, send the event ID as a downstream idempotency key, and handle the crash between external acceptance and local acknowledgement. Keep network calls outside DB transactions.

## ADR 004 Bounded, durable retries

**Decision:** Retry transient failures after 2 and 4 seconds; stop at three attempts. Persist the next due time and error code. Permanent failures go directly to dead letter.

**Tradeoff:** The schedule is short and deterministic for a local presentation. Production retry classification should use actual failure semantics, include jitter, honor provider retry hints, and establish operational limits. Replays reset attempt counts for a new cycle, but prior attempts remain in audit history.

## ADR 005 Preserve history on replay

**Decision:** Permit replay only in `DeadLetter`, require a human-readable reason, retain the event ID and all existing audit entries, and queue the same immutable payload.

**Limit:** Replay is not a repair mechanism. Permanent and exhausted failure simulations remain failures after replay. Real operations require an authenticated actor, permissions, replay quotas, investigation tooling, and safe remediation procedures. The demo does not attribute actions to a verified identity.

## State transitions

| Current state | Trigger | Next state |
|---|---|---|
| Queued | Simulated success; amount <= 5000 USD | Integrated |
| Queued / RetryScheduled | Simulated success; amount > 5000 USD | ManualReview |
| Queued / RetryScheduled | Transient failure below attempt limit | RetryScheduled |
| Queued / RetryScheduled | Permanent failure or exhausted attempts | DeadLetter |
| DeadLetter | Replay with reason | Queued |

`ManualReview` is terminal for this demonstration; there is no approval or rejection workflow. `Integrated` is terminal. Duplicate requests return the current state without reprocessing.

## Failure scenarios

| Scenario | Expected behavior | Evidence |
|---|---|---|
| Client loses response after successful submission | Retry identical key/payload; receive existing claim | API and duplicate tests |
| Client uses a different key for the same business claim | Existing claim returned, no additional event | Store duplicate test |
| Same key or business identifier with altered amount | HTTP 409, existing state preserved | Conflict tests |
| Several concurrent identical submissions | One claim and one nonduplicate response | Concurrent submission test |
| Two workers compete for an event | One committed processing attempt | Competing consumers test |
| Process restarts before retry deadline | Persisted schedule retained | Restart/clock test |
| Third transient failure | Dead letter; no automatic fourth attempt | Exhaustion test |
| Replay | History grows; event ID retained | Replay test |

## Production evolution

1. Define a real integration contract and map approved source/payer formats.
2. Add authenticated intake, tenant-scoped idempotency, and authorization for reads/replay.
3. Move to an operational database, add schema migrations, and implement broker dispatch with leases.
4. Add a gateway adapter with downstream idempotency and tests for uncertain acknowledgements.
5. Add OpenTelemetry traces, queue-age metrics, retry/dead-letter alerts, readiness probes, and load tests.
6. Establish data classification, encryption, secrets management, immutable audit export, backup/recovery, and retention before processing sensitive data.

## Interview walkthrough

Explain the transaction boundary first. Demonstrate a duplicate submission and then a restart-safe retry. Show the difference between a business claim identifier and a delivery key. Finish by explaining why the local transactional simulator does not establish exactly-once external delivery, and how a production adapter would change the design.
