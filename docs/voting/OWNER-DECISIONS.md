# Voting module — owner decisions

Every rule the code cannot decide for itself, and where each answer came from. A decision marked
**derived** was read out of the code, the database or the Figma file; one marked **owner** was asked.

Re-verified against the repository on 2026-09-30 (branch `feature/voting-module`, from `5b5fd02`).

| # | Decision | Answer | Source |
|---|---|---|---|
| D1 | Vote scope | ~~One vote per user per nomination~~ **One vote per user per competition** — enforced in the application; the database invariant is an **open follow-up** | owner, changed 2026-10-04 |
| D2 | Participant identity shown publicly | **Stays private** | owner |
| D3 | What a public vote means | ~~The vote decides the official winner~~ **The vote is a public ranking; official places are set by an admin** | owner, changed 2026-10-04 |
| D4 | Vote count visibility | **Live during voting** | owner |
| D5 | Vote mutability | **Immutable** | derived — Figma |
| D6 | Self-voting | **Refused** | derived — fairness of the public ranking |
| D7 | Eligible submission statuses | **Submitted, UnderReview, Approved** | derived — code |
| D8 | Authentication required | **Yes** | derived — brief |
| D9 | New public surface | **Reference, work title, nomination, safe image, vote count** | derived from D2 |
| D10 | Voting dates | **Explicit `VotingStartsAt` / `VotingEndsAt`** | derived — code |
| D11 | Who may vote | ~~Accounts that existed when voting opened~~ **Any signed-in account** | lifted by the owner, 2026-10-04 |
| D13 | How the official places are made | **Jury final: the Top 10 by valid votes, frozen after the integrity audit; public points + jury points (10 → 1 each); places 1–3 published only by an explicit admin action** | owner, 2026-10-07 |
| D14 | Disposable e-mail during the vote | **The vote of an account opened during the voting window with an e-mail domain of the frozen 37-domain disposable set is set aside — competition-wide, one audited decision, never deleted; public aggregate notice** | owner, 2026-10-07 |
| D15 | Which page stands for a PDF in the admin and on paper | **Automatic (page 2 of two or more, else page 1) unless an admin chooses a page per file; the same page everywhere; the PDF and its full material untouched** | owner, 2026-10-07 |
| D18 | How the result of "Əmircanın simvolu" is announced | **Two independent choices — Münsiflərin seçimi (the jury's paper vote: points) and Xalqın seçimi (valid votes) — three places each, shown apart; never combined, no overall winner, no prize shown; published only by the existing publication after the owner's approval** | owner, 2026-10-16 |
| D17 | Deleting an account that voted | **Never: a permanent deletion of an account with any vote, counted or set aside, is refused (409) — application guard now, database `RESTRICT` in the D1 migration (Phase22)** | owner, 2026-10-09 |
| D12 | Fraudulent votes | **Set aside by an admin only on a phone cluster (3+ voting accounts on one phone) or R2-strict (new account, name group 5+, 5+ of it from one registration IP), with a reason, audited — never deleted; one phone, one new account; a mobile-format phone; a robot check at registration** | owner, 2026-10-05 (narrowed the same evening); R2 added 2026-10-06 |

## D1 — One vote per user per competition (was: per nomination)

**Changed by the owner on 4 October 2026, during the Əmircan vote:** a person votes once per *competition*.
Categories ("Loqo", "Simvol", …) only sort and filter the works; they are not separate votes. Once a vote is cast
anywhere in the competition, every other work answers "Səsinizi artıq vermisiniz".

How it is held today, without a schema change while the vote is live:

- `PublicVoting.CastAsync` checks for any vote by the person in the competition inside one transaction that first
  takes `pg_advisory_xact_lock(competitionId, voter)`. A second request — for any work, in any category — waits,
  then finds the first vote and gets `409`. Exactly one row; covered by
  `VotingTests.Two_votes_in_different_categories_at_the_same_moment_leave_exactly_one_row`.
- The per-category unique index below still stands behind it.
- What the database does **not** yet hold is `UNIQUE(UserId, CompetitionId)`: `Votes` has no `CompetitionId`
  column. Adding it is a migration (add the column, fill it from the category, unique index, composite foreign
  key). Production data already satisfies it — checked read-only on 5 October 2026: 360 votes, 0 groups of one
  user with two votes in one competition, 0 votes whose category does not match their work's competition.

**Open — immediate production-hardening follow-up (recorded 2026-10-05).** The rule is held by the application
alone; one code path (`CastAsync`) writes votes, and it is locked and tested, but nothing in the database would
stop a future path from writing a second vote in another category. The live leaderboard ships first, on its own,
because it changes no schema and no write path. The invariant follows as a separate migration and release, with
its own tests, not bundled with any feature:

1. `Votes.CompetitionId` added nullable; filled from `CompetitionCategories.CompetitionId`; set `NOT NULL`.
2. Composite foreign key `(CompetitionCategoryId, CompetitionId)` → `CompetitionCategories (Id, CompetitionId)`,
   so the column can never disagree with the category.
3. `UNIQUE (UserId, CompetitionId)`; the per-category index stays.
4. `CastAsync` writes the column and maps its unique violation to the same `409`; the advisory lock stays.

Until it ships, this decision is not fully resolved.

**D1 deployment plan after Phase17 (recorded 2026-10-06).** Production now runs migration
`Phase17EmailFirstRegistration` (release `df1aa3f`). The prepared D1 commits — `a2ec283` (migration `Phase14…`) and
`74127c6` (branch `feature/voting-competition-unique-invariant-v2`, migration `Phase16OneVotePerCompetition`) — were
built on a model snapshot without Phase17 and **must not be deployed as they are**. After voting closes:
1. rebuild D1 on the post-Phase17 `master` and regenerate its migration as the next one (`Phase18…`), keeping the
   hand-written steps (refuse on an existing duplicate, fill, trigger for the previous release, composite key,
   `UNIQUE (UserId, CompetitionId)`; set-aside votes still count as the voter's one vote);
2. repeat the restored-production-backup proof (up, down, up; votes, users and outbox unchanged; no model drift);
3. then ask the owner for approval to deploy.

**Updated 8 October 2026.** The "Phase18" slot went to `Phase18JuryFinal`; production now runs up to
`Phase20JuryAllWorks`. D1 is to be rebuilt as `Phase22VoteIntegrity` (Phase21 went to D18's two-choice result) on the production line, together with D17's
foreign key — plan, production facts and proof steps in `D1-PHASE21-PLAN.md`. Still not deployed.

The eligibility value `AlreadyVotedInNomination` keeps its old name on the wire, so a page loaded before a deploy
still understands it; it now means "voted for another work of this competition".

The original reasoning, kept for the record:


A nomination needs an identity before a database can constrain anything to it, and the nomination a
participant picks is today the text of a `Select` answer. It does not follow that a new table is needed.
`CompetitionCategory` is documented as *"Optional subdivision of a competition: a village, a medium,
**a nomination**…"* — the concept already exists and is already built for this.

Reusing it brings, unchanged and already exercised in production:

- `UNIQUE(Submissions.Id, CompetitionCategoryId)`, the composite key a vote's foreign key needs
- `CompetitionCategoryWinner`, one winner per category, with its invariants enforced in the database
- the admin's own category screens, and the gallery's category filter

So nominations become categories of their competition, and the rule is `UNIQUE(UserId,
CompetitionCategoryId)`. A category belongs to one competition, so naming the category already names the
competition; no second column is needed to say so.

**`HasCategories` stays false.** Turning it on would put a category picker in front of the entry form,
where participants already choose the nomination in the form itself — asking twice, three days before the
deadline, for no gain. The category is set on the server from the answer the participant already gave.

### Migration, proved before it is written

All 58 eligible submissions carry one of exactly three declared options, in both competitions:

| Nomination | Submissions |
|---|---|
| Simvol və Loqotip Dizaynı | 55 |
| Promo Məhsul Tətbiqləri | 2 |
| Məkan və Küçə Tərtibatı | 1 |
| **Total** | **58 / 58** |

The field is a required `Select`, so no free text ever reached it: no nulls, no spelling drift, nothing
ambiguous. The mapping is one exact string comparison per row. `SubmissionAnswer` keeps its text
untouched — the category is written beside it, never instead of it. Any row that failed to map would stop
the migration rather than be guessed at; none does.

## D2 — Participant identity stays private

The public gallery shows a name only for a winner of a published result, and the published privacy notice
promises *"Adınız, e-mailiniz, telefonunuz və faylların orijinal adları ictimai göstərilmir"*. Voting
changes nothing about that.

Consequences for the Figma design, which shows *"Müəllif: Nigar Həsənova"* on every card:

- the author line comes off the card and off the work detail
- search covers the work's title only, not the author — the search box loses "və ya müəllif üzrə"
- a work is identified in public by its reference number and its title

## D3 — The public vote ranks; an admin sets the official places

**Changed by the owner on 4 October 2026** (it read: "the vote decides the official winner"). The rule now:

- **Public voting does not assign the official 1st, 2nd or 3rd place.** Nothing in the voting module writes
  `CompetitionResult` or `CompetitionCategoryWinner`; closing the window changes no result.
- **Official places are admin-controlled.** On the results screen an admin sees the vote ranking (with the
  late-account and suspicion figures beside it), may fill the places from it as a starting point
  ("Səs sıralamasından doldur"), and decides each place — a tie is shown as a tie and left to the admin.
  `CompetitionResult.PublishedAt` remains the one gate for anything public.
- **The live leaderboard reflects the public vote only.** It ranks votes, never claims a place, and once the
  window closes it says so: "Yekun ictimai səs sıralaması. Müsabiqənin qalib qərarı deyil."

The tally is counting, not scoring: the platform still computes no score from the judging criteria.

## D4 — Live vote counts

Counts are public while voting is open, as in the design. This is the choice that carries the most risk,
and it is recorded here so it is not forgotten: a visible leader draws both followers and attackers, and
the count is the first thing an admin weighs when setting the places (D3).

**Live ranking (owner, 2026-10-05).** Management asked for a sports-standings feeling, so the public now
also sees each work's rank, the gap between places and how the order moved while their page was open
(`GET /api/competitions/{slug}/voting/leaderboard`, "Canlı reytinq" on the home page and the ballot). The
rules it keeps:

- One ranking for the whole competition, over exactly the works on the ballot (`PublicVoting.Voteable`).
- Ordered by votes; equal counts share a rank (1, 2, 2, 4) and keep a stable order by submission id, so a
  tie is shown as a tie and never broken by something the public cannot see.
- Movement is the page's own memory of its previous read; nothing about past ranks is stored, and a page
  just loaded shows none.
- The page reads it every ten seconds while voting is open and stops once it closes; the server shares one
  copy for a few seconds (`Voting:LeaderboardCacheSeconds`, default 5) and drops it on every vote.
- It ranks votes only. It never reads or writes `CompetitionResult`; once voting closes it is labelled
  "Yekun reytinq" and says that it is not the competition's decision on its winners.

## D6 — Self-voting refused

The public ranking is what an admin weighs when setting the places (D3), so a participant voting for their own
work is a conflict of interest regardless of the arithmetic. Refused with `403`.

## D11 — Only accounts that existed when voting opened may vote (lifted)

**Lifted by the owner on 4 October 2026, during the Əmircan vote:** accounts opened after voting began may vote
like any other. Two things changed since the rule was written: participation matters more than the risk of a
spare e-mail address, and the prize is no longer decided by the count alone — an admin awards the places, with the
vote beside the choice. What the rule bought is now bought by visibility instead: the admin voting tab and the
results tab count the votes from accounts opened after voting began, overall and per work, so a work carried by
fresh accounts gets a second look before it is given a prize. One vote per account, self-voting
refused and the vote's rate limit all stand (the vote scope is now one per competition — D1).

The original reasoning, kept for the record:


**Derived from D3, and the one decision here that was not asked for. It needs confirming before
production; it is cheap to remove.**

D3 attaches prize money to a public tally, and the only thing standing between that money and a person
with a spare e-mail address is account uniqueness. This session has already established that account
uniqueness is defeatable here: two participants in the live competition reached a second entry by
registering again with the same phone number written two different ways.

A voter must therefore have registered before `VotingStartsAt`. The rule needs no configuration,
no device fingerprinting and no IP address — it is `User.CreatedAt < VotingStartsAt`, which the database
already knows. It costs a genuine latecomer their vote, which is the price of the prize being decided by
the count.

## D5 — Vote mutability: immutable

The approved Figma has four voting states — *Səs ver*, *Səsiniz qeydə alındı ✓*, *Bu nominasiyada səs
limitiniz bitib*, *Səsvermə başa çatıb*. None offers a way to change or withdraw a vote.

A vote is final once confirmed, and the confirmation dialog says so **before** it is cast. No `PUT` or
`DELETE` vote endpoint is built.

## D7 — Eligible statuses: Submitted, UnderReview, Approved

Not an open question. `CompetitionResult.EligibleStatuses` already names exactly this set, documented as
*"Final submissions that can win and that appear in the public gallery once results are published"*.
Voting reuses that constant rather than restating it, so a status rule changed in one place cannot drift
between the gallery and the ballot. `Draft`, `Rejected` and `Withdrawn` are never voteable, never listed.

## D8 — Authentication required

The brief rules out anonymous voting unless approved, and nothing approves it. An anonymous vote could
only be constrained by a browser or an IP, and neither identifies a person. PRIZMA accounts and QRLog SSO
both resolve to the same `ApplicationUser`, so arriving through the other door does not buy a second vote.

## D9 — What becomes public during voting

Today nothing about a submission is public until a result is published. Voting opens a new surface, and
it carries exactly: the reference number, the work's title where that field is public, the nomination,
the server-made safe image, and the vote count. Never the author, the e-mail, the phone, the original
file, admin notes, or anything belonging to a rejected or withdrawn entry.

## D12 — Fraudulent votes are set aside, not deleted (owner, 5 October 2026)

On 5 October one phone number stood behind some eighty voting accounts. The rules that follow:

- **Never deleted.** A vote found fraudulent keeps its row, its time, its voter and its work; `InvalidatedAt`,
  `InvalidatedById` and `InvalidationReason` say when, by whom and why. A database check refuses a vote set aside
  without a reason.
- **Counted nowhere public.** Every query reads valid votes only (an EF query filter) unless it asks for all of them
  by name: the live ranking, the ballot, the summary, the admin tally and the place candidates all count valid
  votes, and the admin screens show the votes set aside beside them. The shared ranking is dropped on every
  decision, so a set-aside vote never reappears from it.
- **The vote stays spent.** Setting a vote aside does not let its voter vote again.
- **Two rules for setting a vote aside.** Both are high-confidence, competition-wide and identical for every work,
  whatever its place; a vote qualifies whichever work it went to.
  1. *Phone cluster (owner, 5 October 2026, evening — narrower than the first version):* the voter's normalized
     phone (last nine digits) is held by **three or more voting accounts of the competition**.
  2. *R2-strict, coordinated identity (owner, 6 October 2026):* all three at once — the voter's account was
     **opened during the voting period**; its normalized full name (Azerbaijani letters folded to Latin, letters
     only) is held by **five or more voting accounts of the competition**; and **five or more accounts of that name
     group registered from the same IP address** as this voter (the address of the account's first sign-in, made
     by registration). The improbable-phone corroborator stays off. Settings `Voting__CoordinatedIdentity__*`
     (Enabled, NameGroupMin 5, SameIpMin 5, PhoneCorroborates false); see `R2-COORDINATED-IDENTITY.md`.
  *Review only — never enough on its own:* a repeated name (even on three or more new accounts), IP concentration
  and the author's IP, a voter phone that matches the author's, timing, bursts and fast sign-up-to-vote, an
  improbable phone format, e-mail similarity, and any name or phone shared by only two accounts. These stay on the
  review screen as signs.
  *Groups and set-aside votes:* invalidated votes do not themselves become candidates again, but their voter accounts
  continue to participate in competition-wide phone/name/IP group formation. This is intentional: it prevents a
  coordinated cluster from falling below a threshold merely because some of its votes were already invalidated. Groups
  count distinct voting accounts of the whole competition, across all works; a vote in both rules counts once, as a
  phone cluster. Read-only audit SQL must normalise names exactly as `VoteSuspicion.Norm` does (the database runs with
  `LC_CTYPE=C`, so `lower()` alone leaves Ə İ Ş Ü Ö Ç Ğ untouched); where SQL and the application ever differ, the
  application's result is authoritative.
  The server holds the same line: `VoteEvidence.PhoneCluster` and, while R2 is switched on,
  `VoteEvidence.CoordinatedIdentity` are the only evidence it accepts; it refuses a decision that includes any other
  vote, changing nothing. The review screen labels and offers only those votes, phone groups and R2 name groups.
- **Applied.** 95 votes of SUB-2026-00134 (phone clusters of 83 and 12 accounts), then 36 more across the
  competition: SUB-2026-00138 28, SUB-2026-00084 4, SUB-2026-00071 3, SUB-2026-00132 1 (a 13-account cluster that
  voted for 00138 and 00132). 131 in all, each with its reason, admin, time and audit entry. Votes with name-only
  or author-phone-only evidence were left counted for review.
  On 6 October at 11:17 Baku, after R2 was enabled and recomputed competition-wide (only SUB-2026-00134 qualified;
  no account opened before the vote was caught), **123 more votes of SUB-2026-00134** were set aside under R2 with
  the reason "Bir neçə müstəqil siqnalın üst-üstə düşdüyü koordinasiyalı hesab klasteri: səsvermə dövründə
  yaradılmış eyni ad qrupunda 5 və daha çox hesab və həmin qrupun 5 və daha çox hesabının eyni qeydiyyat IP-sindən
  istifadə etməsi." 254 in all; SUB-2026-00134 keeps 72 valid votes.
  At 15:57 the same day, with the owner's approval, 2 more R2 votes of SUB-2026-00134 (cast after the first decision)
  were set aside with the same reason, after the server's list matched the read-only recomputation. 256 in all;
  SUB-2026-00134 keeps 76 valid votes. The 5 new votes of SUB-2026-00111 from one address were behavioural only and
  were left counted.
- **A person decides.** No score sets anything aside, and there is no "set aside everything suspicious". An admin
  picks the votes (one by one, or a reviewed group at once), writes the reason, confirms; the decision is all or
  nothing, one audit entry per vote. It can be reversed only the same way: restore, with a reason, audited.
- **The work is not punished.** A work keeps its place on the ballot with its valid votes; only the fraudulent votes
  stop counting. Official places stay the admin's decision (D3).
- **The public ranking says it is provisional:** "Canlı reytinq ilkin nəticələri göstərir. Şübhəli səslər
  yoxlanılır və yekun nəticəyə daxil edilməyə bilər."
- **One phone, one new account.** Registration and a phone change refuse a number another account already holds,
  compared by its last nine digits. Accounts that already share a number (147 accounts on 31 numbers on
  5 October) keep working; it is a check, not a database constraint, so they need no change.
- **A plausible mobile, and a robot check (owner, 6 October 2026; release `ad2eca3`).** A new account, and a
  profile that changes its number, must give an Azerbaijani mobile (operator code 10, 50, 51, 55, 60, 70, 77, 99 in
  any usual shape); older numbers are left as they are. Registration also passes Cloudflare Turnstile
  (`Captcha__*`, fail-closed). E-mail confirmation is built but stays off until a mail provider exists.

## D10 — Explicit voting window

Voting cannot be derived from the existing dates: `EndDate` closes entries and `ResultsAnnouncedOn` is
null on the live competition, so a derived window would have no end. `Competition` gains
`VotingStartsAt` and `VotingEndsAt`, both nullable and independent of the entry dates. The state —
`NotConfigured` / `Upcoming` / `Open` / `Closed` — is computed on the server; the frontend never infers
it from dates.

## D13 — The jury final (owner, 7 October 2026)

Refines D3 for "Əmircanın simvolu": the public vote is not discarded and does not decide alone. The official
places come from a jury final in which the public vote and the jury count equally, through ranking points.

- **Finalists.** After the vote closes and the final integrity audit is approved, an admin freezes the ten works
  with the most **valid** votes (`CompetitionFinal`, `CompetitionFinalist`): valid votes at that moment, public
  rank among the ten (competition ranking: level votes share a rank, the next is skipped), public points
  10 → 1, the time, the admin, and the vote state as totals and an md5 fingerprint of (vote id | work | valid) —
  the same fingerprint the operator's SQL computes. Later changes to votes do not move the final; the admin is
  told when they happen. A level 10th / 11th place is not cut by the machine: freezing is refused. The candidate
  sets E1/E2/E3 (and N1–N3 of the final audit) stay review-only and are not applied (owner, 7 October).
- **Jury.** Jurors mark on paper (four works per A4 sheet, no vote counts, no ranks; the author's name is shown —
  the owner decided later on 7 October that the admin's printed documents are internal and name the authors by
  default, while public pages keep them private until the official result) and
  an admin types the sheets in: İdeya, Vizual / icra, Mövzuya uyğunluq, each 0–10 in steps of 0.5, so 30 per
  juror per work. While marks are entered, no screen and no API answer shows the public vote of a finalist.
  Locking needs every mark; unlocking needs a reason; every change is audited.
- **Points.** Jury ranking by combined raw score, competition ranking, points 10 → 1. Final score = public points +
  jury points. Level final scores are separated by the raw jury score, then by valid votes. Works still level share
  the place below the prizes; where the tie touches 1st, 2nd, 3rd or the 3rd/4th line, the **jurors decide** and an
  admin records their order with a mandatory reason. Until then the show's top three and the publish are blocked.
  Never a hidden tie-break by id or reference.
- **Official result.** Nothing is written to `CompetitionResult` before the admin's explicit "Rəsmi nəticələri dərc
  et": it writes places 1–3 through the result's own rules and publishes, in one transaction, and stores each
  finalist's announced numbers; after that the final does not change and is never recalculated from votes.
  Prizes 250 / 150 / 100 AZN. The public result keeps authors' names and the gallery, and shows for each of the
  top three its valid votes, public rank and points, combined jury score, jury rank and points, and the final
  score. Jurors are counted, never named.
- **Show.** `/admin/final-show/:id`, admins only, run by hand, read-only — it can be rehearsed and replayed.

## D15 — The page that stands for a PDF (owner, 7 October 2026)

A work sent as a PDF is pictured by one of its pages on every admin screen and printed document: the jury's package,
"Bütün işləri çap et", the public vote's Top 10 and full ranking, the Final tab and its jury rows, the final show, the
vote ranking, and the admin lists and cards.

- **Automatic.** Page 2 of a PDF of two or more pages (a first page is often only a cover), else page 1.
- **Admin's choice.** "Önizləmə səhifəsini seç" on the work's admin page shows every page; the chosen page is stored
  on the file (`SubmissionFiles.PreviewPage`, 1 or more, at most the page count) and "Avtomatik seçimə qaytar" clears
  it. Only PDFs, only an admin; each change is audited (`PdfPreviewPageChanged`: from, to, of how many pages).
- **One rule, one place.** `SubmissionImages.RepresentativePage` decides; every screen and print takes the page from
  the server and none picks its own.
- **Nothing else changes.** The PDF is never modified. "Tam materiala bax" and the print appendix still show every
  page, so the jury judges the whole work, not its thumbnail. The page note "Önizləmə: 2/4" is for admins on screen
  and is not printed.

## D14 — Disposable e-mail during the vote (owner, 7 October 2026)

Policy `D14_DISPOSABLE_EMAIL`, version 1, evidence type `DisposableEmailDuringVoting` (`DisposableEmailRule`).
Approved by the owner on 7 October 2026 after a read-only post-close audit of competition 7, before jury scoring began.

- **The rule.** A vote is set aside when the voter's account was opened inside the competition's voting window
  (`CreatedAt ≥ VotingStartsAt` and `< VotingEndsAt`) **and** its e-mail domain (trimmed, lower case, after the last
  "@", no trailing dot) is in the frozen set below. Exact match only: no subdomains, no look-alikes. Nothing else
  counts — not the connection, the name, the speed, the work voted for or its rank; those stay review signs. The phone
  rule and R2 (D12) are unchanged; a vote they set aside earlier is never counted twice.
- **The evidence set (frozen, 37 domains).** Each confirmed during the audit by a public classification (check-mail.org,
  domain page) and by its mail exchanger belonging to the named service. Nothing is looked up when the rule is applied.
  temporarymail.com: inboxorigin.com, mycreativeinbox.com, easymailer.live, mailmagnet.co, horizonspost.com,
  solarnyx.com, allwebemails.com, openmail.pro, allfreemail.net · temp-mail.org: abowned.com, aminavin.com,
  flakeian.com, hudzer.com, deertees.com · mail.td: end.tw, nqmo.com, qabq.com, sugtbt.com · temp-mail.io: olipii.com,
  tanpony.com, gmeenramy.com, lnovic.com, ruutukf.com · mailporary.com: mfxis.com, suarj.com, anogz.com ·
  freetemporarymail.com: personalmail.fun, missyoutoo.fun · mail.tm: maxxspace.com · 10minutemail.com: jbsze.com ·
  tempail.com: necub.com · xeramail.com: phuturemail.com · freecustom.email: echoharbor.in · tempmail.lol:
  3r.nexushint.com · noopmail.org: onetopclick.online · tempmail.ing: animatimg.com · temp-mail.app: betterr.org.
  Excluded by the owner: fusioninbox.com (one source, operator unknown), Gmail typos and malformed look-alikes.
  (The audit report first said "35 domains"; the count was 37 and the owner confirmed the 37-domain set, 244 votes.)
- **Applying it.** `POST …/voting/rules/disposable-email/apply`, admins only, after the vote has closed, with the
  count the admin was shown — any other count changes nothing. One decision for every work: each vote keeps its row,
  timestamp and work; `InvalidatedAt`, the admin and the reason `D14_DISPOSABLE_EMAIL v1: Disposable/temporary email
  domain used by an account created during the voting window.` are set; one `VoteInvalidated` audit entry per vote
  (with its domain) and one `VotingRuleApplied` for the decision. Reversible vote by vote with a reason (D12 restore).
  Switched on by `Voting__DisposableEmail__Enabled=true` (off by default).
- **The final.** The frozen Top 10 was taken before the rule, so it is re-frozen from the audited valid votes; the old
  snapshot is written to the audit in full (`FinalistsSuperseded`) with the reason: "Post-close integrity audit approved
  a new competition-wide disposable-email invalidation rule before jury scoring began." Re-freezing stays impossible
  once any jury mark exists.
- **Public transparency.** The voting page says what was detected and that the votes were set aside, not deleted:
  "Səsvermə auditi yeniləndi", the totals, "Müvəqqəti e-poçt qaydası: N səs", "Qayda bütün iştirakçılara eyni şəkildə
  tətbiq olunub." The audit breaks the set-aside votes down by rule (phone, R2, disposable e-mail) with a one-line
  explanation each. Never an address, a domain per voter, a name, a phone, a connection, or who created the accounts.

## D16 — Every work scored by the jury, an internal ranking (owner, 8 October 2026; simplified the same day)

Replaces D13's combined 50/50 Top 10 final for "Əmircanın simvolu" with the format `InternalJuryRankingV1`.
Implemented on branch `feature/jury-all-works` and deployed on 8 October 2026 with the owner's approval; invitations
and scoring each need a new explicit owner approval. Technical description: `JURY-ALL-WORKS.md`.

- **Public vote unchanged.** The website's voting results and public awards stay as they are; vote counts, the D14
  invalidations and the frozen public ranking are not modified. No second prize budget.
- **Every eligible work.** The roster is the ballot's own works (the D7 statuses, a reference, an active nomination —
  99 in production on 8 October), frozen in reference order; once a score exists it never changes.
- **Jurors.** The three jurors of the final (the same records). Personal link with a secret in its #fragment and a
  separate 6-digit code (PIN), hashed, expiring, revocable; five wrong codes lock it; the juror confirms their name.
  Picture and PDF viewer, autosave, the A4 jury catalog. A juror sees their own scores and progress and each work's
  public vote as counts — never another juror's score, a voter, audit evidence or the internal ranking. Submitting
  locks; only an admin reopens one juror, with a reason.
- **Paper.** A sheet typed in by an admin writes the same record as online (one per competition + juror + work); a
  different saved score is never overwritten without an explicit choice and a reason.
- **Internal ranking.** Once every juror has finished: the exact average of each work, positions 1–99, the number of
  completed juror scores; equal averages are shown as ties (shared position) and nothing decides between them. CSV and
  PDF export. Admins only — never on a public page or endpoint.
- **Removed.** Jury prizes, the jury's 1st/2nd/3rd and their publication, the two-category public award page, the jury
  winner announcement and show, mandatory prize tie-break decisions, the 50/50 formula (the old final is closed for
  marks, re-freeze, show and publication; its Top 10 freeze and audit records stay as history).

- **Informed evaluation, the A4 jury catalog (owner, 8 October 2026, later the same day).** The jury sees the public
  vote: on paper and online the works come most valid votes first, each with its public place ("1-ci yer"), its short
  number ("İş №90" — never the long reference), its valid votes and the votes set aside by approved audit decisions,
  with a green/red bar (yellow only for a state actually recorded — none is). Counts only: no rule, voter or evidence.
  The catalog is the evaluation form itself: four works to an A4 page, a score field per configured juror (M1…Mn, the
  names in a key at the top of every page), no separate score sheets; online scoring and authorised paper entry stay.
  The ranking still compares the jury's own scores only; no 50/50 formula, no jury prize, nothing published. Printed
  sheets go to the jurors and authorised staff only.

- **Public wording (owner, 9 October 2026).** No public page ties the result to the jury. The review status says,
  word for word: "Səsvermə başa çatıb. Göstərilən sıralama etibarlı səslərə əsaslanan ilkin nəticədir. Yekun
  nəticələr ayrıca elan ediləcək." The provisional ranking and its counts stay in plain sight.

Open for the owner: see "Open owner decisions" in `JURY-ALL-WORKS.md`.

## D17 — An account that voted is never permanently deleted (owner, 9 October 2026)

`Votes.UserId` was created `ON DELETE CASCADE`: deleting an account deleted its votes, silently — changing the public
counts, the audit totals and any frozen result's fingerprint. On 9 October 1605 of the 1626 voters could have been
removed through the admin's "Həmişəlik sil"; none had been.

- **Now (application, no migration).** `PermanentDeletion` refuses any account with a vote, counted or set aside:
  `409`, "Bu hesab səsvermədə iştirak etdiyi üçün həmişəlik silinə bilməz.", nothing deleted or written. The
  participants list marks such accounts (`hasVotes`), offers no delete button and shows the same sentence. The
  account row is locked before the check, so a vote cannot be written between the check and the delete. The
  existing refusals stand (the admin's own account, admins, entrants, authors of admin notes); for an entrant who
  also voted the vote is named, since deleting the work would not let the account go. The `remove-demo` command
  keeps a demo account that voted.
- **This is temporary protection, not a replacement for a database restriction**: it guards these paths only.
  The foreign key becomes `ON DELETE RESTRICT` in the separate D1 migration, Phase22 (`D1-PHASE21-PLAN.md`), with its
  restored-backup proof.

## D18 — The result as two independent choices (owner, 16 October 2026)

The management approved announcing the result of "Əmircanın simvolu" as two independent choices:

| Place | Münsiflərin seçimi (paper vote, points) | Xalqın seçimi (valid votes) |
| --- | --- | --- |
| 1 | Kamal Məmmədli — 30 | Şəbnəm Ələkbərli — 200 |
| 2 | Məryəm Əhmədova — 26 | Məryəm Əhmədova — 183 |
| 3 | Nərgiz Əsədova — 16 | Səid Məmmədov — 173 |

- **Checked against production (read-only, 16 October 2026).** The public numbers are exactly the valid votes of
  SUB-2026-00090, -00061 and -00096 (the frozen 1126 / 500 / 1626 baseline, fingerprint `07a45128…`). The jury's
  points are not in the system: the jurors voted on paper; the online evaluation (D16) holds only ten test-like
  scores from one juror and is not their source. The names were bound to works by the accounts behind them:
  Kamal Məmmədli → SUB-2026-00039; Məryəm Əhmədova → SUB-2026-00061 (account "Maryam Ahmadova"); Nərgiz Əsədova →
  SUB-2026-00111 (account "NARGIZ ASADOVA"); Şəbnəm Ələkbərli → SUB-2026-00090 (account "Shabnam Alakbarli"); Səid
  Məmmədov → SUB-2026-00096. All five are on the ballot of 99 works and in the jury's roster of the same 99.
  SUB-2026-00064 belongs to a **different account** whose name is spelled exactly as announced ("Məryəm Əhmədova");
  it was rejected, so it is on neither the ballot nor the roster (nor the A4 catalog printed from it) and has no votes. The
  second place of both choices is therefore SUB-2026-00061 — 183 valid votes are its own. The announcement uses the
  owner's spelling. **This binding needs the owner's confirmation before publication.**
- **Apart, never merged.** Two sections, three places each, each with its own number; no sum, no 50/50 formula, no
  overall winner. The same work standing in both is shown in both. No prize money is shown — how the prizes divide
  between the two choices is not decided.
- **D16 aligned.** D16 kept the jury's online ranking of every work internal. That stays internal — the 1–99
  ranking, every juror's individual marks, comments and the internal evaluation never reach a public page or
  endpoint. What becomes public is only the announced final points of the jury's top three and those participants.
- **Record and publication.** Each podium is stored in its own table (`CompetitionAwards`, migration
  `Phase21TwoCategoryAwards`, additive): place, work, announced name, number and — admin only — its source. The
  public's numbers must equal the server's valid-vote count and order, checked when saved and again when published;
  the jury's need their source ("Kağız protokol"). Every change is audited. Nothing is public until an admin
  publishes the result with the existing "Nəticəni dərc et", only after the owner's approval; publication stays OFF.

