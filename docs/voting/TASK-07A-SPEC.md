# 7A — Voting module: architecture and specification

Rules live in [OWNER-DECISIONS.md](OWNER-DECISIONS.md); this is how they are built. Everything below was
re-verified against the repository on 2026-09-30, not carried over from an earlier reading.

## What the system already has

| Concern | Where it is today |
|---|---|
| Nomination | `Competition.Nominations` free text for the prose; the participant's choice is a required `Select` answer |
| Subdivision entity | `CompetitionCategory` — *"a village, a medium, a nomination…"*, `UNIQUE(CompetitionId, Slug)` |
| Submission ↔ category | `Submission.CompetitionCategoryId`, with `UNIQUE(Id, CompetitionCategoryId)` already in place |
| Winner | `CompetitionResult.OverallWinnerSubmissionId`, and `CompetitionCategoryWinner` for one winner per category |
| Publication gate | `CompetitionResult.PublishedAt` — *"nothing about it is public until this is set"* |
| Eligible work | `CompetitionResult.EligibleStatuses` = Submitted, UnderReview, Approved |
| Safe media | `/thumb` (640 px) and `/public` (1600 px, metadata-free); originals never public |
| Errors | `NotFoundException` 404, `ConflictException` 409, `ValidationFailedException` 422, RFC 7807 |
| Audit | `AuditTrail.Record(userId, action, entityType, entityId, detail)` |
| Rate limits | Named policies: `auth`, `submission-write`, `upload`, `qrlog-poll` |
| Identity | One `ApplicationUser` behind both PRIZMA accounts and QRLog SSO |
| Frontend | `useApi`, `ApiError`, `RequireAuth`, `AuthedImage`, `WorkVisual`, `GalleryCard`, `GalleryFilter`, `PageLoading`, `PageError`, `Empty`, the `.action-bar` sticky pattern, the `QrLoginDialog` focus trap |

Neither a `Vote` entity nor a voting window exists yet; both are new.

## Domain model

Additive only. No table is dropped, no column is repurposed, no historical answer is rewritten.

```
Competition  (+2 columns)
  VotingStartsAt   timestamptz NULL
  VotingEndsAt     timestamptz NULL

CompetitionCategory  (rows added — the three nominations, per competition)

Submission  (backfilled, not altered in shape)
  CompetitionCategoryId  ← resolved from the existing nomination answer

Vote  (new)
  Id                     bigint       PK
  UserId                 uuid         FK AspNetUsers
  SubmissionId           int
  CompetitionCategoryId  int
  CreatedAt              timestamptz
```

`Vote` carries `CompetitionCategoryId` because the rule is stated in terms of it and a unique index cannot
reach through a join. It cannot drift from the submission it names: a composite foreign key
`Vote(SubmissionId, CompetitionCategoryId) → Submissions(Id, CompetitionCategoryId)` makes a vote whose
nomination disagrees with its work unstorable. That key is already indexed — the same device
`CompetitionCategoryWinner` uses so that *"a Balaxanı entry can never be stored as Ramana's winner, not
even by hand"*.

No denormalised vote counter. One grouped query per page is exact by construction and fast enough at this
size; a counter is a second copy of the truth and is only worth it once measurement says so.

## Uniqueness and concurrency

```sql
UNIQUE (UserId, CompetitionCategoryId)
```

The database is the rule. A disabled button is a courtesy, not a constraint.

Casting a vote is one `INSERT`. A second one — a double click, a second tab, a replayed request, two
parallel calls — violates the index, Postgres raises `23505`, and the API answers **409** with
*"Bu nominasiyada artıq səs vermisiniz."* Exactly one row exists either way, and the caller gets the same
answer whichever request lost. The count is read after the insert, never before.

## Voting state

Computed on the server from the window and returned as a name, never as dates for the client to compare:

| State | When |
|---|---|
| `NotConfigured` | no window set |
| `Upcoming` | now < `VotingStartsAt` |
| `Open` | between the two |
| `Closed` | now ≥ `VotingEndsAt` |

## API

| Method | Route | Auth | Answers |
|---|---|---|---|
| `GET` | `/api/competitions/{slug}/voting` | anonymous | state, window, nominations, total votes |
| `GET` | `/api/competitions/{slug}/voting/works` | anonymous | page of works; `?nomination=&q=&page=&pageSize=` |
| `GET` | `/api/competitions/{slug}/voting/works/{reference}` | anonymous | one work, its files, its count, what this viewer may do |
| `POST` | `/api/competitions/{slug}/voting/works/{reference}/vote` | **signed in** | `201` · `401` · `403` own work / too new an account · `404` not voteable · `409` already voted · `422` window shut |

Nothing is taken from the caller but the route: the user comes from the token, the competition and the
nomination from the work, the count from the database. There is no `PUT` and no `DELETE` — D5.

Rate limiting: a `vote` policy alongside `submission-write`. Audit: `VoteCast`.

## Privacy

What the voting surface may carry, and nothing else: reference number, the work's title where that field
is public, the nomination, the safe server-made image, the vote count. No author, e-mail, phone, original
file, admin note, or anything from a rejected or withdrawn entry — see D2 and D9.

## Figma → component → data

| Figma | Component | Data |
|---|---|---|
| Voting list `2:8` / `2:402` | `VotingListPage` | `voting/works` |
| Nomination filter | `GalleryFilter` *(reused)* | `nomination` |
| Search | `VotingSearch` | `q` — **title only**, D2 |
| Card + count | adapted `GalleryCard` | `reference`, `title`, `voteCount` |
| Image | `WorkVisual` / `AuthedImage` *(reused)* | `/thumb`, `/public` |
| Work detail `2:136` / `2:487` | `VotingWorkPage` | `voting/works/{reference}` |
| Confirmation `2:245` / `2:569` | `VoteDialog` — `QrLoginDialog` pattern | — |
| Four states `2:192` | `VoteButton` | `canVote` + `reason` |
| Loading / empty / error `2:286` | `PageLoading`, `Empty`, `PageError` *(reused)* | — |
| Sticky mobile CTA | `.action-bar` *(reused)* | — |
| Home entry `2:611` | `HomePage` addition | `voting` state |
| Author line | **removed** | D2 |

## Migration plan

1. Two nullable columns on `Competitions`.
2. `Votes` table, its unique index and its composite foreign key.
3. A seed step, in the repository's own `RunStepAsync` idiom: create the three nominations per settlement
   competition from the `Select` options, then set `CompetitionCategoryId` on every submission whose
   nomination answer matches one exactly. A row that does not match is left alone and logged; the
   measurement says none exists.
4. On final submission, the server resolves the nomination answer to its category, so new entries keep the
   link without the participant being asked anything new.

Additive throughout: no answer text is changed, no submission is deleted, nothing is backfilled with a
guess, and `HasCategories` stays false so the entry form is untouched.

## Test plan

Backend: anonymous vote · valid vote · duplicate · **two parallel votes → one 201, one 409, one row** ·
before the window · after it · unconfigured · Draft · Rejected · Withdrawn · unknown reference · own work ·
a second nomination allowed · count accuracy · private fields absent · reads write nothing · account too
new (D11) · rate limit.

Frontend: 1440 / 1024 / 390 · confirmation dialog and bottom sheet · vote succeeds · already voted ·
closed · error · **state survives a refresh** · no console errors.

## Then

7B builds this. 7C builds the Figma screens on it. 7D gives the admin the window and the totals. 7E hardens
and measures. Nothing deploys without approval.
