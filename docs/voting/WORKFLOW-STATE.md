# Voting module — workflow state

Kept in step with reality. Updated at the end of every phase.

| | |
|---|---|
| **Phase** | **Deployed and smoke-tested on production** (`827d6e2`) |
| **Branch** | merged into `master` as `827d6e2` |
| **Latest commit** | `827d6e2` — the merge that went out |
| **Blockers** | none — the module is live, idle, and proved end to end |

## Completed

**7A — architecture and specification.** Findings from the earlier reading re-verified against the current
code rather than trusted: no `Vote` entity, no voting window on `Competition`, nominations still free text
with the participant's choice in a `Select` answer, `EligibleStatuses` still Submitted/UnderReview/Approved,
the gallery still gated on `CompetitionResult.PublishedAt`, names still withheld from everyone but a
published winner.

Ten decisions were settled: six read out of the code, the database or Figma, four asked. One further rule
(D11) was derived from an answer and is marked as needing confirmation before production.

The finding that shaped the build: **no new nomination table is needed.** `CompetitionCategory` is
documented as *"a village, a medium, a nomination…"*, and it brings the composite key a vote needs,
one-winner-per-category, and the admin screens with it.

The migration was proved before being written: 58 of 58 eligible submissions carry one of exactly three
declared `Select` options, in both competitions — no nulls, no drift, nothing ambiguous.

**7B — backend, database and API.** Built as specified, and green.

The migration is additive: two nullable columns on `Competitions`, the `Votes` table, its
`UNIQUE(UserId, CompetitionCategoryId)` and the composite foreign key that makes a vote whose nomination
disagrees with its work unstorable. The seed step creates the three nominations per settlement competition
and backfills `CompetitionCategoryId` by exact match; from now on the server does the same at final
submission, so a participant is never asked the question twice.

Six endpoints, not four: the spec's four, plus the two that serve a work's public-safe picture — without
them D9's "safe server-made image" has no address, and the gallery's own file route is shut while results
are unpublished.

Two decisions taken while building, both recorded here because neither was in the spec:

- **Upcoming is a 404 for the works, not an empty list.** Before the window opens the ballot is not public
  at all; only the state and the dates are. 422 is kept for a vote after the window has shut.
- **Answers appear, through the gallery's own switch.** First built without them, on a strict reading of D9;
  reversed in 7C because the approved design shows *"Konsept təsviri"* on the work page and a voter needs it
  to judge. It adds no new rule: a field reaches the ballot only when an admin has ticked "public after
  results", exactly as for the gallery, so unticking it removes it from both. **Worth the owner's eye** —
  see the note under 7C.

Tests: 14 new, all green — including two votes cast at the same instant leaving exactly one row, a 409 for
the loser, and one audit entry. The full suite is 324 passed, 2 failed; the same two fail identically on
`b63d593` before any of this existed (`DerivativeBackfillTests`, `QrlogQrLoginTests` — both wall-clock
races that pass in isolation). Not regressions, and worth fixing in 7E.

## Owner decisions

| # | Answer |
|---|---|
| D1 | One vote per user per nomination — `UNIQUE(UserId, CompetitionCategoryId)` |
| D2 | Participant identity stays private |
| D3 | The vote decides the official winner; an admin still publishes, and a tie is never broken silently |
| D4 | Live counts |
| D5–D10 | Derived — see OWNER-DECISIONS.md |
| D11 | Only accounts that existed when voting opened may vote — **confirm before production** |

**7C — the public voting frontend.** All ten Figma screens, and green.

Two routes — `/competitions/:slug/voting` and `.../voting/:reference` — plus the entry points the design
asks for: a band on the home page while any vote is open, and a block on the competition's own page.

Reused rather than rebuilt: `WorkVisual`, `Answers`, `PageLoading`, `PageError`, the `.empty` and
`.skeleton` patterns, the `QrLoginDialog` focus trap, and `GalleryFilter` — which gained two optional props
so the same chips can carry `?nomination=` and keep the search term on the address. `.action-bar` moved
from the competition page's stylesheet to the shared one: two routes use it now, and each loads only its
own CSS, so a pattern left in one route's file is simply missing from the other.

Departures from the Figma, each for a stated reason:

- **The author comes off every screen** — the card, the work page and the confirmation sheet — and the
  search box loses "və ya müəllif üzrə". D2.
- **The confirmation gains a line: "Səs bir dəfə verilir və geri alınmır."** D5 requires the finality to be
  said before the vote, and the design's own copy does not say it.
- **"Lider işlərdən biri" is not built.** It needs a rule for what counts as leading that nobody has set,
  and under D4 a leaderboard is the riskiest thing on the page. The slot says "Səs verdiniz" when the vote
  is the viewer's own, and nothing otherwise.
- **No "Səsvermə" item in the site header.** The header is on every page and does not know where a vote is
  open; an item that sometimes 404s is worse than none. The home band and the competition page carry it
  instead. Worth revisiting in 7D, when an admin can see the window.

Backend additions this needed: `voting` on the competition summary and detail, and `votingCompetitions` on
the home payload — a vote usually runs after entries have closed, so the competition would otherwise be
missing from the home page exactly when it has something to ask of the reader.

Tests: 13 new frontend tests, 235 in all, green; lint and the type check clean; the production build fine.
Backend 326/327 — the one failure is the same wall-clock race that fails on `b63d593`.

**7D — the vote, for the administration.** Green.

The window is set where every other date already lives — on the competition's own form — because that is
where an admin looks for a date, and it saves without a second screen. It is validated on both sides: both
ends or neither, and the end after the start. A half-set window is refused rather than half-believed.

Moving the window writes its own audit line (`VotingWindowChanged`, "was → is"), separate from the edit that
carried it. Under D3 the vote decides who receives a prize, so when the window moved, and who moved it, is
not a detail of an ordinary edit. An edit that leaves the dates alone adds no line.

The tally is a tab of its own, shown only where a window exists: votes and voters counted, each nomination's
works ordered by votes with places shared where they are level, the winner an admin has already recorded,
and — loudest of all — the final works that belong to no nomination and that nobody can therefore vote for.

It reads and does not decide. Three things follow, each deliberate:

- **A tie is shown, never broken.** D3: where two works finish level the admin chooses, on the results
  screen that already keeps one winner per nomination. The tally says "Bərabər" and names no winner.
- **No winner is recorded from here.** `CompetitionCategoryWinner` and its screen exist and work; a second
  way to write the same row would be a second place for it to go wrong.
- **A voter is counted, never named.** The tally answers "how many people", never "who voted for what" —
  tested by asserting no voter's name and no user id appears anywhere in the response.

Tests: 5 new backend (20 in the voting file), 5 new frontend (240 in all). Backend 330/332 — the two
failures are the wall-clock races, both passing in isolation, both failing on `b63d593`.

**7E — hardening, end to end, and what it costs.** Green.

**What it costs.** A page of the ballot was measured, not guessed: the same number of queries whether it
holds three works or thirteen. That is now a test, so the one mistake that would matter here — a query per
work, on the day the most people are looking — cannot come back unnoticed. The per-nomination tally is
indexed (`IX_Votes_CompetitionCategoryId`, which EF's own convention created); there is still no
denormalised counter, and the reason has not changed.

**What it lets out.** Every public answer of the module is now read in one test and searched for everything
it must never carry: a participant's name, e-mail or phone, an answer to a field an admin has not made
public, a storage key, an original file name, a user id, a moderation status. The picture route was checked
too — `no-cache` with an ETag, `nosniff`, a sandbox CSP, a 304 on revalidation, and a 404 the moment the
work leaves the ballot.

**End to end.** A real API on its own database, the built frontend, and Chrome at 1440, 1024 and 390: 33
checks, all green. A vote cast through the dialog, the count the server returned appearing on the card, the
other work in that nomination closing, the state surviving a reload, the sheet at the bottom on a phone,
Escape closing it, and no console errors beyond the two the design expects.

**It found two real faults, both now fixed:**

- **The ballot was fetched before the session was known.** A page opened straight from its address is
  anonymous for as long as the refresh takes, so a signed-in visitor was shown a ballot telling them to
  sign in — and nothing corrected it. Both voting pages now wait for the session before asking anything.
  This is the "state survives a refresh" requirement, and unit tests could not have caught it: they mock
  the session as already settled.
- **A long button label widened the grid.** `.btn` never wraps, so "Səs vermək üçün daxil olun" pushed the
  cards 7 px past the page at 1024. The card's actions now let a label wrap and shrink.

**Also written:** [RUNNING-A-VOTE.md](RUNNING-A-VOTE.md) — how to open, watch and close a vote, for whoever
administers the site rather than for whoever wrote it.

## Deployed — 2026-09-30

The owner approved on 2026-09-30, keeping **D11 as written** (with the refusal now naming the date voting
opened, rather than refusing flatly) and asking for the concept description to be shown **through the
gallery flag**, which is what the code already does.

Release `meydan-827d6e2.tgz`, built from a clean tree, deployed with `deploy/scripts/deploy.sh`. The
migration applied, the seed step ran once, the API came back healthy.

### What the seed did to live data

```
Nominations for novxaninin-simvolu: 0 entries linked, 0 left unlinked
Nominations for emircanin-simvolu: 94 entries linked, 0 left unlinked
```

| | Simvol və Loqotip | Promo Məhsul | Məkan və Küçə |
|---|---|---|---|
| Əmircan, works linked | 87 | 4 | 3 |

**Every final work has a nomination** — 51 Submitted, 3 UnderReview, 6 Approved, none unplaced. The nine
entries still without one are drafts that have not answered yet; they are placed when they are submitted.
Nothing was deleted and no answer was rewritten.

### Verified on production

- `20260930120634_Phase12Voting` applied; `/health/ready` answers `Healthy` on database and storage; the
  running image carries commit `827d6e2`.
- The `Votes` table, its three indexes and — read back from `pg_constraint` — the composite key
  `FK_Votes_Submissions_OwnCategory`.
- **The two rules that decide the prize, proved on the production table inside a rolled-back transaction:**
  a second vote by the same person in the same nomination raised `23505` on
  `IX_Votes_UserId_CompetitionCategoryId`, and a vote whose nomination disagreed with its work was refused
  by the composite key. Nothing was left behind: `Votes` holds 0 rows.
- The public voting addresses answer 404 while no window is set, and the SPA serves the voting deep links.
- `voting` reached the public competition payloads (`NotConfigured`), `votingCompetitions` is empty, and the
  admin tally answers 401 without a token.

### Smoke-tested end to end — 2026-09-30 / 10-01

A ten-minute window was opened on the live competition and everything the module does was exercised against
it, then production was put back exactly as it was.

| | |
|---|---|
| Window | `23:46:00 → 23:50:00 (+04)`, set from the admin form by the owner |
| Ballot | 62 works in 3 nominations, public while the window stood |
| Votes cast | 2 — one from a throwaway participant, one from the owner's own account |
| After cleanup | `Votes` empty, window cleared, ballot 404 again |

Everything passed: the vote (**201**), the same work again (**409**), another work in the same nomination
(**409** — the rule is per nomination, not per work), an anonymous attempt (**401**), and a request whose
body tried to name a different nomination (**ignored** — the server reads the nomination off the work).
Unknown, foreign and malformed references all answered **404**.

The database held exactly what it should: one row, its `CompetitionCategoryId` equal to the submission's
own, and one `VoteCast` audit entry written in the same transaction. **The refused attempts wrote nothing** —
over the whole test the audit gained only the lines that describe real changes.

Privacy held on every public answer and in the rendered page: no name, e-mail, phone or user id; the
concept description visible because its field is ticked public; the symbols field and the private note
absent because theirs are not.

Two things were learned that no test had shown before:

- **The tie is real, and the screen says so.** The owner's second vote landed on another work in the same
  nomination, producing 1–1. The admin tally showed *"səslər bərabərdir"*, put both works at place 1 with a
  "Bərabər" badge, and named no winner — exactly what D3 asks for, on live data.
- **Saving the form three times wrote one `VotingWindowChanged` line, not three.** An edit that leaves the
  dates alone adds nothing, as intended.

Cleanup removed the throwaway account (its vote went with it, by cascade) and then, on the owner's word,
the owner's own test vote. The audit lines stay: an append-only log keeps what happened even when the rows
it describes are gone.

### Earlier, before the admin session was available

Three of the checks the owner asked for wait on the voting window being opened, which is the owner's own
click: the administrator password is deliberately kept off the server (`deploy/README.md`, "First admin"),
and opening the window is also the moment the ballot becomes public on a competition that is **still
accepting entries** — a decision about the day, not only a test.

1. Setting the window on the competition's own form — the owner's to do.
2. A vote cast over HTTP, and a second one answering **409** — needs no admin, only an open window.
3. The admin tally screen — the owner's to look at.

All three were done once the owner opened the window from the admin form; see the section above. No
smoke-test account remains, and no competition has a voting window.

## The original approval note

## PRODUCTION READY — OWNER APPROVAL REQUIRED

Nothing has been deployed. The branch is `feature/voting-module`, five commits, off `5b5fd02`.

**Before it goes anywhere, two answers are needed:**

1. **D11 — only accounts that existed when voting opened may vote.** Derived, not asked for. It is the only
   thing between prize money and a person with a spare e-mail address, and it costs a genuine latecomer
   their vote. Cheap to remove: one condition.
2. **Answers on the ballot.** The work page shows the answers of fields ticked "public after results" — the
   gallery's own switch. On the live competitions only the title is ticked, so today it shows nothing. Say
   whether the concept text should be on the ballot, and the switch is all it takes.

**And one thing to decide about the vote itself:** the migration is additive and safe, but it has never run
against production data. It adds two columns, one table and its indexes, creates three nominations per
settlement competition, and writes `CompetitionCategoryId` on the 58 submissions whose nomination answer
matches one exactly. Nothing is deleted, no answer is rewritten, and a row that does not match is left
alone and logged. The measurement says none does.

## Deployment, when the word comes

1. Back up the database first — the migration is additive, and a backup still costs less than a surprise.
2. Deploy as usual; the seed step runs on start-up and is guarded by `SeedHistory`, so it happens once.
3. Check the log for `Nominations for …: N entries linked, 0 left unlinked`. A non-zero second number
   means an entry's nomination answer matched nothing and needs a hand.
4. Leave the voting dates empty. The module is invisible until an admin sets them — see
   [RUNNING-A-VOTE.md](RUNNING-A-VOTE.md).

## For the owner, when 7E stops

1. **D11** — only accounts that existed when voting opened may vote. Cheap to remove, and until it is
   confirmed it is a rule nobody asked for.
2. **Answers on the ballot** — currently governed by the per-field "public after results" switch. On the
   live competitions only the title is ticked, so today the ballot shows no description at all.

## Known, and deliberately left

- Two tests in the backend suite fail under the load of a full parallel run and pass in isolation
  (`DerivativeBackfillTests`, `QrlogQrLoginTests`). Both are wall-clock races, both fail identically on
  `b63d593` — before any of this existed. Not voting's doing; worth fixing on their own.
- The site header carries no "Səsvermə" item. It is on every page and cannot know where a vote is open
  without a request on every page; the home band and the competition page carry it instead.
- Works are listed in the gallery's order — nomination, then submission time — not shuffled. Where a vote
  decides money, the order of a long list is worth a thought; at 58 works across three nominations it is
  not yet one.

## Standing constraints

- Nothing deploys without explicit approval.
- No migration destroys or rewrites data; no historical answer is altered.
- `git add .` is never used — the working tree carries an unrelated untracked `frontend/public/videos/`.
