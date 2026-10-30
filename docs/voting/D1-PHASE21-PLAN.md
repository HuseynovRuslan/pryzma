# Votes in the database — Phase22 plan (D1 one vote per competition; D17 a vote outlives no account deletion)

Status: **plan only.** (Renumbered on 16 October 2026: `Phase21TwoCategoryAwards` of D18 took Phase21; the file keeps its name.) Nothing built, merged or deployed. Two owner decisions (`OWNER-DECISIONS.md`) that the
application holds today and the database does not:

- **D1** — a person votes once per competition. Held by `PublicVoting.CastAsync` (advisory lock + check, covered by
  tests); the database holds only `UNIQUE (UserId, CompetitionCategoryId)`.
- **D17** — an account that has voted is never permanently deleted. Held since release `release/post-close-d16` by
  `PermanentDeletion` (409, "Bu hesab səsvermədə iştirak etdiyi üçün həmişəlik silinə bilməz.") and by `DemoData`
  (such a demo account is kept). That is **temporary, application-level protection, not a replacement for the
  foreign key**: the database still has `Votes.UserId … ON DELETE CASCADE`, so any other path — a future code path,
  `UserManager.DeleteAsync`, a hand-written `DELETE` — would still take the person's votes with the account.

## Why Phase22, and why not the prepared commits

- The prepared D1 work — `a2ec283` (`Phase14…`) and `74127c6` on `feature/voting-competition-unique-invariant-v2`
  (`Phase16OneVotePerCompetition`) — was generated on a model snapshot from before Phase17. The "Phase18" slot the
  6 October plan reserved was taken by `Phase18JuryFinal`; production now runs 18 migrations up to
  `Phase20JuryAllWorks` (release `d39b788`). Deploying either prepared commit would fork the snapshot.
- So D1 is rebuilt on the production line and its migration regenerated as **`Phase22VoteIntegrity`** (the next free number: `Phase21TwoCategoryAwards` holds the two-choice result of D18), with the
  same hand-written steps, plus the `RESTRICT` key of D17 below.

## Production facts it relies on (read-only, 8 October 2026, ~20:30 Baku)

| Check | Result |
| --- | --- |
| Person/competition pairs with more than one vote (set-aside votes included) | **0** |
| Votes whose category and work belong to different competitions | **0** |
| Votes | 1626, all in competition 7 (voting closed 07.10 11:40) |
| `CompetitionCategories (Id, CompetitionId)` alternate key | present (`AK_CompetitionCategories_Id_CompetitionId`) |
| Foreign keys on `Votes` | `…CompetitionCategoryId` → `CompetitionCategories(Id)` RESTRICT; `(SubmissionId, CompetitionCategoryId)` → `Submissions`; `UserId` → `AspNetUsers` **CASCADE**; `InvalidatedById` SET NULL |
| `votes_fill_competition` function / trigger | absent |
| Voters a participant deletion could have removed (9 October, before the D17 guard) | 1605 of 1626 (1106 valid, 499 set aside); 0 such deletions since the vote opened |
| Other tables cascading from `AspNetUsers` | Identity claims/logins/roles/tokens, `EmailOutbox`, `QrlogLoginTickets`, `RefreshTokens` — and `Votes` |

## What Phase22 does (one transaction, additive)

1. **Refuses** to start if any person already holds two votes in one competition (raises; never picks a vote).
2. Adds `Votes.CompetitionId` nullable, fills it from each vote's category, makes it `NOT NULL`.
3. A `BEFORE INSERT` trigger fills the column when a writer leaves it out — the release before Phase22 (`d39b788` or
   its successor) during the deploy window and after an application-only rollback.
4. Replaces the single-column category key with the composite key `(CompetitionCategoryId, CompetitionId)` →
   `CompetitionCategories (Id, CompetitionId)`, so the column can never disagree with its category.
5. Adds `UNIQUE (UserId, CompetitionId)`. The per-category unique index stays.
6. **D17:** replaces `FK_Votes_AspNetUsers_UserId … ON DELETE CASCADE` with the same key `ON DELETE RESTRICT`, so
   no account that has a vote — counted or set aside — can be deleted by anything, and a vote can never vanish with
   its account. (`NO ACTION` would do the same, checked at the end of the statement; `RESTRICT` is what EF writes for
   `DeleteBehavior.Restrict` and refuses at once.)

Application side: `Vote.CompetitionId`, its configuration, and `CastAsync` writing it and mapping the unique
violation to the same `409` ("Səsinizi artıq vermisiniz"); the advisory lock stays. The wire value
`AlreadyVotedInNomination` keeps its name. `ModerationConfigurations`: `Vote.User` becomes
`OnDelete(DeleteBehavior.Restrict)`. The application guard of D17 stays — it gives the admin the plain 409 sentence
where the database would answer with a foreign-key error — and so does its row lock.

Considered, not included without a separate owner decision: `Votes.InvalidatedById` is `ON DELETE SET NULL`
(deleting the admin who set a vote aside would blank who did it on the row; the audit entry keeps it). Admin accounts
cannot be deleted from the panel, so it is left as it is.

It does **not** delete, move or re-validate any vote; set-aside votes keep their time, reason and actor and still
count as the person's one vote. It changes no deadline, no result, no jury score.

`Down` removes exactly this (index, composite key, trigger and function, column), restores the previous category
key, and puts `Votes.UserId` back to `CASCADE`.

## How it is built

1. Branch `feature/d1-phase21` from the release line (production `d39b788` plus whatever release is current when
   work starts) — never from `master`, which trails production by 28 commits.
2. Take the code parts of `74127c6` (`Vote.cs`, `ModerationConfigurations.cs`, `PublicVoting.cs`, `MigrationTests`,
   `docs/voting/D1-migration-up/down.sql`); drop its `Phase16…` migration and snapshot changes.
3. Change `Vote.User` to `DeleteBehavior.Restrict`; `dotnet ef migrations add Phase22VoteIntegrity`; put the
   hand-written SQL (steps 1, 2-fill, 3) back into the generated `Up`/`Down`; check
   `dotnet ef migrations has-pending-model-changes` reports none.
4. Tests: the migration tests (refusal on a duplicate, fill, trigger with an old-style insert, down/up), the voting
   tests (two votes in different categories at once → exactly one row; second vote → 409; a set-aside vote still
   blocks a second), the D17 tests — the existing `PermanentDeletionTests` (409 for a counted and for a set-aside
   vote, nothing changed by a refusal, accounts without votes as before) **plus** a database-level one: a raw
   `DELETE FROM "AspNetUsers"` of a voter fails with `23503` and leaves the vote, a raw delete of a non-voter still
   succeeds — then the full backend and frontend suites.

## Proof on a restored production backup (VPS, throwaway database)

1. Restore the latest `meydan-db-*.dump` into a throwaway database; record: vote count 1626 / 1126 / 500, the app's
   vote fingerprint (`07a45128…` today), the set-aside rows' md5, users, outbox, audit count, results 0, jury
   scores.
2. Apply the EF-generated script for Phase22 in one transaction; verify the column is filled for all rows, the
   constraints exist (`pg_get_constraintdef` of `FK_Votes_AspNetUsers_UserId` says `ON DELETE RESTRICT`), and every
   recorded value is unchanged.
3. Deletion probes, each inside `BEGIN … ROLLBACK`: deleting one voter with a counted vote and one whose only vote
   was set aside fails with `23503` (foreign-key violation) and both votes are still there; deleting an account with
   no vote succeeds (then rolled back). Vote count, fingerprint and audit are re-checked after the probes.
4. Run `Down`; verify the schema and data equal the restore exactly, and that the user key is `CASCADE` again (the
   same probes inside `BEGIN … ROLLBACK` now delete — proving `Down` is the real previous state). Run `Up` again;
   identical to step 2.
5. Drop the throwaway database. Record timings (1626 rows: well under a second; the `ALTER`s take a short exclusive
   lock on `Votes`, harmless while no vote is open).

## Deploy (separate release, its own owner approval)

- Its own release, never bundled with a feature; outside the QRLog peaks (07:30–09:30, 17:00–19:00 Baku); no vote
  open on the site.
- `deploy.sh` takes the safety backup, applies Phase22 in the migrate step (which also runs the content seed — check
  the seed is a no-op on production first), then starts api + web. Watch `/health/live` only.
- Before/after: votes 1626 / 1126 / 500 and fingerprint unchanged; `Votes.CompetitionId` filled for every row; the
  unique index present; `FK_Votes_AspNetUsers_UserId` is `ON DELETE RESTRICT`; results, jury scores, audit history
  unchanged.
- Rollback: application-only to the previous release (the trigger keeps it working). `Down` only with a separate
  owner decision.

## Open for the owner

1. When to run it — the vote is closed, so any quiet window works; it protects the next competition, not this one.
2. *(Decided 9 October — D17.)* The application guard ships first, in `release/post-close-d16`, with no migration;
   the database `RESTRICT` comes here, in Phase22. Deleting work, a competition or a project already cannot take
   votes (`RESTRICT`, and the jury roster/final guards).
3. Whether `Votes.InvalidatedById` should also become `RESTRICT` (see above).
