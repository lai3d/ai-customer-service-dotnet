# The operations surface


Where the people who answer for this service look at what it did: the conversations, the
tickets the assistant raised, the answers someone flagged, and a record of who looked. It
exists because the Java and Go implementations built one each; the .NET one was asked for
with one difference the other two declined: **the frontend is deployed separately from the
service.**

## Two deployables, one contract

| | |
| --- | --- |
| `src/CustomerService` | The service. Gains `/api/admin/v1/**`: JSON only, bearer sessions, permissions enforced on every request. Registered only when `ADMIN_ENABLED=true`; otherwise the paths are 404s, not 401s from a guard. |
| `admin-ui/` | A static bundle: Vite, React 19, TypeScript, three runtime dependencies. Its own image, its own container, its own port (8083). It talks to the service only through nginx's `/api` proxy, so the browser sees one origin and the service needs no CORS in the Compose stack. |

The Java implementation kept its page inside the Spring application and called the interface
"already separated, the deployment is not". Both readings are correct. What separating the
deployment buys is that the UI can be built, tested, versioned and put behind a different
edge without touching the service that holds the model key — and what it costs is a second
image, a `package-lock.json`, and CORS the moment the two are not behind one proxy. The cost
is visible in the repository; the benefit is a deployment property. That trade was the
owner's to make and was made.

The UI carries the session as a bearer token in `sessionStorage`, so there is no ambient
credential for a cross-site request to ride on and no CSRF token to rotate. The cost of that
choice is that the token is readable by any script on the page, which is why the page has no
script that is not its own: React renders text as text, the markdown subset builds elements,
and the CSP nginx sends allows scripts from `'self'` only.

## Sign-in, roles, sessions

Accounts live in `staff_account`, PBKDF2-SHA256 with a per-account salt. Two roles, `admin`
and `support`, because this release has two kinds of action and a permission model with more
entries than the actions it governs is a design document, not a control. A support account
works tickets, reads conversations and flags answers; an admin also manages accounts and
reads the audit.

Sessions are rows in `staff_session` holding the SHA-256 of a random token, with a sliding idle
expiry (`ADMIN_SESSION_TIMEOUT`, 30 minutes). Disabling an account or resetting its password
deletes its sessions, so a revoked person's open page fails on its next request — the
proposal's requirement that revoked permissions stop an already-open page.
`LogoutAndDisablingEndASession` and `AnIdleSessionExpires` pin both.

The first admin is seeded by `ADMIN_SEED_USERNAME` / `ADMIN_SEED_PASSWORD`, into an empty table
only, under a table lock so two replicas starting together seed once. The variables never
overwrite or reset an account and are safe to leave set. A wrong password, an unknown user and
a disabled account are refused identically, and the password check runs against a real hash
even when the user does not exist, so the time a rejection takes does not say which usernames
do.

## Reading is an action

Opening a conversation writes an `admin_audit` row with the operator's name. Who looked is most
of what an audit trail is for here, because looking is the sensitive operation on this
surface: the writes are about tickets and the reads are about people. Refusals are recorded
too — by role (`403`, with the method and path the role could not use) and by rule (`422`,
with the rule) — because an audit trail of what succeeded is missing exactly the rows an
investigation opens it for. A lost race (`409`) is not a refusal and is not recorded. Nothing
edits or deletes the table. Both sibling implementations found the refusal gap in a live walk;
here it was built in from their write-ups.

## Tickets became real before the page existed

Tickets used to live in a bounded map in each process, so the three-per-conversation cap was
`replicas × 3` and deduplication held only within whichever replica served the request. Both
were written down as known limits, which was honest while nobody could see them; an operations
page is exactly the thing that shows two operators two different sets of tickets.

[`TicketStore`](../src/CustomerService/Tickets/TicketStore.cs) holds them in Postgres. The
deduplication is a unique index on the normalised summary; the cap is a guard row per
conversation, created if absent and locked `FOR UPDATE` in the creating transaction, because a
unique index cannot enforce a count. `TheCapHoldsUnderConcurrentCalls` runs twenty differently
worded requests through the database at once and gets three tickets. The tool the model calls
is the same tool with the same outcomes; it just stopped lying about where its boundary was.

The state machine, as built:

```
open ──claim / assign──▶ claimed ──resolve──▶ resolved ──close──▶ closed
  ▲                        │  │                   │                  │
  └────────release─────────┘  └───────close───────┘                  │
  ▲                                                                  │
  └──────────────────reopen (reason required)────────────────────────┘  (also from resolved)
```

Claiming is first come, first served on an unowned open ticket; `ClaimingIsFirstComeFirstServed`
races eight claims and gets one. Release, resolve, close and reassign are the owner's or an
admin's. Resolving requires a conclusion, stored on the resolving `ticket_event` and never on
the ticket row, so a reopen has nothing to carry forward and every conclusion a ticket ever had
stays in its history. Reopening requires a reason and clears the owner: a reopened ticket is
nobody's until claimed again. Every mutation carries the version the page read, required and
never defaulted; a stale one is a `409` and writes nothing, which is also what makes a
double-submitted form land once.

Where the three implementations chose differently, on purpose:

| | .NET | Go | Java |
| --- | --- | --- | --- |
| States | `open → claimed → resolved → closed`, release back to `open` | `OPEN → IN_PROGRESS → RESOLVED → CLOSED` | as .NET |
| Reopen | reason required, owner cleared | reason required | reason optional as a note, owner cleared |
| Conclusion | on the resolving event | a column on the ticket | on the resolving event |
| Frontend | separate deployable | one static file in the binary | one static file in the jar |
| Session | bearer token, hashed in Postgres | static per-operator tokens in configuration | form login, server session, CSRF cookie |

## The turn record is not the chat memory

`chat_memory` is the model's context, windowed at 40 messages. A history that disappears when
the window slides is not a history, and it cannot answer what an operator is actually asked:
did this fail or did the customer close the tab, what did retrieval return, what did it cost.

So there is one `conversation_turn` row per turn, with its retrieval evidence and tool calls as
JSON and one `turn_model_call` row per model call. It is written where the turn executes rather
than from the event stream that feeds the browser, because that stream feeds a page which may
already be gone. Two boundaries, deliberately asymmetric: the opening record is written before
the model is called and its failure fails the turn — a model call this service cannot account
for is worse than a turn that did not happen; the closing record runs in the `finally` that
persists the partial reply, on a token detached from the request, and its failure is logged.
At startup, turns still `running` from a process that died more than thirty minutes ago are
marked `interrupted`, never invented.

The Go implementation's test for the disconnect case found that a cancelled history read was
being recorded as the database breaking. The same shape was possible here and
`ACancelledTurnIsRecordedAsCancelledNotAsADatabaseFailure` closes it: cancellation is
classified before any step that noticed it.

Cost is labelled an estimate and says when it is incomplete. A turn on a model with no price
contributes its tokens and no cost, and the overview counts those turns rather than quietly
omitting them; the UI shows "no price for this model" where a zero would be a lie.

## Answer feedback

An operator reading a conversation can flag a recorded answer as incorrect, incomplete or
other, with a note. Feedback is bound to the turn, not the conversation, so it survives the
window sliding. Closing it requires a conclusion and means the report was handled — an FAQ
revised, a decision written — not that the customer's issue is resolved. There is no knowledge
editing behind it yet, for the reason below.

## The bug that arrived through a third door

The service's first live turn sent the model `{"status":1}` because `System.Text.Json` writes
enums as integers. The Java implementation then found its dates going out as `[2026,9,3]`.
Building this surface, the admin API sent the UI `{"state":1}` for a ticket, and
`TheTicketLoopRunsThroughTheApiWithVersionsAndRefusals` — which reads the JSON as text, the
way the UI does — failed on it before a browser was opened. One converter on the shared
serializer options, and the finding is now three for three: **a number where a reader expects
a word is the bug that keeps arriving, and only a reader that has never seen the type catches
it.** The UI is such a reader, which is an argument for the separated frontend that nobody
made in advance.

## Verified in a browser, and what it found

The pages were driven in a real Chrome on 2026-09-06 through Playwright: a refused sign-in,
sign-in as a support account, the overview, the ticket queue with its state filter, a ticket's
detail, a full cycle on `TKT-4701` through the real buttons and forms — claim, note, resolve
with a conclusion, close, reopen with a reason, claim again — the conversation behind it with
the assistant's markdown rendered as bold rather than asterisks, a flag raised on its answer,
that flag closed on the feedback page, the support account bounced from `/audit` to the
overview, then sign-out, sign-in as the admin, the audit table with the refusals in it, and the
staff table. Every mutation moved the version the page showed; no console error and no failed
request except the deliberate wrong password.

It found one defect no typed test had: **claim and release did nothing.** The handler set the
pending action into React state and then read it back in the same tick, saw the previous
value, and returned. The other five actions open a form first, so the state had settled by the
time they submitted; the two that act on a bare click were exactly the two that failed. The
walk's first run stopped there with a timeout. The action is a parameter now, not state read
back. Both siblings recorded the same lesson in their own live walks: the data was correct at
every seam a test can reach, and the defect lived in what a person would see.

The walk also confirmed, by accident, a property that was designed rather than tested: a
`docker compose up` that omitted `ADMIN_ENABLED` recreated the service with the admin off, and
the UI's sign-in got a `404` from `/api/admin/v1/session` — not a `401`, not a guard, the route
absent. The variables now live in the local `.env`.

**Still not verified:** one operator at a time, one conversation, one ticket. No second
operator has had the same ticket open in another window — the `409` path is covered by tests
and by `curl`, not by two people — and nothing has been driven at a width where a table needs
to scroll.

## Not built

- **Knowledge editing and publication.** The largest part of the proposal, deferred rather than
  forgotten: it changes the corpus, and the corpus is the one fixture that makes every
  retrieval number across the three implementations comparable. Doing it properly needs a
  versioned index, an atomic switch that live retrieval filters on, and a rollback that
  accounts for in-flight requests. A Publish button wired to the startup importer is exactly
  the shape that looks finished and is not.
- **A browser test in CI.** The walk above is [`admin-ui/scripts/drive.mjs`](../admin-ui/scripts/drive.mjs),
  run by hand against the Compose stack with Playwright and an installed Chrome; the UI's CI
  job typechecks, unit-tests and builds the bundle and drives nothing. The script is the shape
  a Playwright suite would take.
- Filtering the ticket queue by time on the page; the API takes `from` and `to`.
- An absolute session lifetime and a concurrent-session limit.
- Operational overview beyond the last seven days' totals.

## Operating it

```bash
ADMIN_ENABLED=true ADMIN_SEED_USERNAME=root ADMIN_SEED_PASSWORD='at-least-twelve-characters' docker compose up -d
open http://localhost:8083
```

After the seeded admin signs in, accounts are created on the Staff page. Only `ADMIN_ENABLED`
needs to stay set; the seed variables may be removed once the first admin exists. If the first
admin's password is lost, an admin resets it on the Staff page; if there is no other admin,
delete the row and restart with the seed set. For a UI served from another origin — the Vite
dev server on 5173, a static host — set `ADMIN_CORS_ORIGINS` on the service.

---

[← Back to the README](../README.md)
