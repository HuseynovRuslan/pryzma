# Jury evaluation of every work — InternalJuryRankingV1

Branch `feature/jury-all-works` (from `feature/final-show` at a7d5fa9). Owner decision: D16 in `OWNER-DECISIONS.md`,
as simplified by the owner on 8 October 2026. Deployed on 8 October 2026 (df027c1, then d39b788 with the admin's vote
analytics). The A4 jury catalog with informed evaluation (below) is local only until the owner approves its deploy.

## What it is

Every juror gives every eligible work (99 in production) one whole score from 1 to 10. Once every juror has submitted,
the administration sees every work ranked 1 to 99 by its exact average. Works with the same average share their
position and are marked as tied — nothing decides between them. The ranking is **internal**: no public page or
endpoint reads it, it awards no prize, and the public vote, its D14 invalidations, the frozen public Top 10 and the
public results are not touched. The old 50/50 combined final is closed (marks, re-freeze, show, publication) and its
records stay as history.

## Informed evaluation and the A4 jury catalog (owner, 8 October 2026)

The jury evaluates informed. On paper and online the works come **most valid public votes first** (reference order
inside a level group), each with:

- its public place, "1-ci yer", by valid votes — competition ranking, level works share it and are marked "bərabər";
- its short number, "İş №90" (the digits of the reference) — the long reference (`SUB-2026-00090`) never goes on paper;
- its valid votes and the votes approved audit decisions set aside, with a bar: green valid, red set aside, its length
  against the most-voted work. Nothing is drawn as "pending review": no such state is recorded.

These numbers come from `VoteCatalogAnalytics.StatsAsync` — the same counting and the same rule classifier
(`VoteAuditRules`) as the admin's vote analytics and the public audit — but the jury gets **counts only**: never the
rule (D12/R2/D14), a voter, an address, a phone, any audit evidence, another juror's score, an average or the internal
ranking. The ranking never reads them: it compares the jury's own scores only.

The printed catalog is the evaluation form itself — there are no separate score sheets any more:

- A4 portrait, four works to a page (2×2): 25 pages for 99 works; a card never runs onto another page and nothing in it
  is cut (the picture takes what the text and the fields leave).
- One score field per juror actually configured — `M1`, `M2`, … in the jurors' order, a 15 × 12 mm box under each key
  — and the key "M1 — name" at the top of every page; names never repeat on the cards. Five fit one row; more wrap
  onto a second (and third) row of the same card; nobody is left out and nobody is invented.
- Every page: the instruction, the bar's key, page numbers and "Məxfi: yalnız münsiflər və səlahiyyətli təşkilatçılar
  üçün".
- The admin's matrix and paper entry follow the same order and show the same short numbers, so the paper is typed in
  page by page.

## Architecture

| Layer | Where | What |
| --- | --- | --- |
| Domain | `Meydan.Domain/Entities/JuryEvaluation.cs` | `JuryEvaluation`, `JuryRosterEntry`, `JuryAssignment`, `JurySession`, `JuryWorkScore` |
| Persistence | `Configurations/JuryConfigurations.cs`, migration `20261008115043_Phase20JuryAllWorks` | keys, composite foreign keys, the 1–10 check |
| Juror access | `Features/Jury/JuryAccess.cs` | invitation secrets, code hashing, lock-out, cookie sessions |
| Evaluation | `Features/Jury/JuryEvaluationService.cs` | roster, jurors, invitations, scores, paper sheets, locks, matrix, ranking, print data |
| Ranking | `Features/Jury/JuryRanking.cs` | exact-average competition ranking with ties; `JuryFormatGuard` for the old final |
| Pictures | `Features/Jury/JuryFiles.cs` | public-safe copies for the juror's own roster |
| Juror UI | `frontend/src/jury/` | `/jury/start`, `/jury/evaluate[/:id]` |
| Admin UI | `admin/pages/JuryTab.tsx` ("Münsiflər" tab), `JuryPrintPage.tsx` | progress, invitations, matrix, paper entry, ranking, A4 documents |

## Data model

- `JuryEvaluations` — one per competition (unique); format, status Draft → Open, roster freeze, `xmin` row version.
- `JuryRosterEntries` — (evaluation, work) and (evaluation, position) unique; (work, competition) → the submission.
- `JuryAssignments` — (evaluation, juror) unique; juror → `FinalJurors`. Invitation token hash (SHA-256), code hash
  (PBKDF2-SHA256, 100 000 iterations), expiry, revocation, failed attempts, sign-in, name confirmation, submission.
- `JurySessions` — cookie session hash, expiry, revocation.
- `JuryWorkScores` — **unique (competition, juror, work)**, online and paper alike; `Score` with check `BETWEEN 1 AND
  10`; comment ≤ 1000; source; row version.

## Migration

`Phase20JuryAllWorks` creates the five tables above with their indexes and constraints — nothing else. No existing
table, column or row changes; `Down()` drops only those five tables. (It was regenerated after the owner's
simplification; the earlier version was never deployed.)

## API

Juror (`/api/jury`, session cookie only, `no-store`): `POST sign-in` (rate limit `jury-auth`), `POST sign-out`,
`GET session`, `POST confirm`, `GET dashboard`, `GET works/{id}`, `PUT works/{id}/score` (`jury-write`),
`POST submit`, picture endpoints for roster works.

Admin (`/api/admin/competitions/{id}/jury-evaluation`, Admin role): `GET`, `POST` (start), `POST roster/freeze`,
`POST|PUT|DELETE jurors[/{jurorId}]`, `POST|DELETE jurors/{jurorId}/invitation`, `POST jurors/{jurorId}/lock`,
`POST jurors/{jurorId}/reopen`, `GET matrix`, `GET export.csv`, `POST paper/preview`, `POST paper/commit`,
**`GET ranking`**, **`GET ranking.csv`**, `GET print`, `GET audit`.

No public endpoint.

## The ranking

- Available only when every juror has submitted and every work has every juror's score; before that the endpoint
  returns the progress and no rows (the CSV answers 409).
- Averages are compared as exact fractions (sum × count against sum × count), never after rounding.
- Competition ranking: 1, 1, 1, 4, …; each row carries `tied` and `tieSize`. The reference only orders a tied group on
  screen; nothing breaks a tie.
- Admin screen: position, reference and title, average (2 places; the exact sum / count on hover), "3 / 3" scores, a
  "Bərabər · N iş" mark; "only ties" filter; CSV; PDF via `/admin/jury-print/{id}?doc=ranking` (33 rows per A4 page,
  "Daxili sənəd — ictimai deyil" on every page).

## Juror authentication

1. The admin issues an invitation: 32 random bytes in the link's `#fragment` (never sent to a server) and a separate
   6-digit code; both shown once, stored as hashes.
2. `/jury/start` takes the secret out of the address bar and asks for the code. Five wrong codes lock the invitation;
   expired or revoked invitations say so. Per-address throttle: 10 sign-ins per 15 minutes.
3. The session cookie `meydan_jury` is HttpOnly, SameSite=Strict, `Path=/api/jury`, 12 hours, stored as a hash; reissue
   or revoke ends every session of that juror. No admin endpoint accepts it; no juror endpoint accepts a JWT.
4. The juror confirms the name shown before anything else opens. A juror sees their own scores and progress, and
   each work's public vote as counts (place, valid, set aside) — nothing of other jurors, voters or the ranking.

## Operations

- Print: `/admin/jury-print/{id}?doc=catalog[&material=1]` (the jury catalog; an old `?doc=sheets` link opens it),
  `?doc=ranking`, `?doc=analytics` (admins only).
- Local visual QA, against a throwaway local database only: `frontend/e2e/jury-qa.local.mjs`,
  `jury-catalog.local.mjs` (3, 5 and 7 jurors, page size and page breaks, PDFs, juror screens) and
  `jury-catalog-pages.local.mjs`.

## Deployment risks

- The migration is additive; the deploy restarts the API for a few seconds — avoid the QRLog peak windows.
- Starting the evaluation closes the old 50/50 final's marks, re-freeze, show and publication. A deliberate owner step,
  not part of the deploy. The public vote's own result can still be prepared and published as before.
- The roster is the ballot at the moment it is frozen; a later status change is shown ("roster differs"), not followed.
- `AuthCookie:Secure` must stay true in production; the proxy must pass `/api/jury/*` like other `/api/*` paths.
- With three jurors and whole scores, many averages will be equal: the ranking will show several tie groups.

## Open owner decisions

1. When to send the real invitations, their lifetime (default 14 days), and how the code reaches each juror.
2. What happens if a roster work is withdrawn or rejected after scoring has begun.
3. Whether the internal ranking is ever used for anything beyond the administration's information.
