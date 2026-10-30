# R2 — coordinated identity (adopted into D12, 6 October 2026)

Status: released in `ad2eca3`; **adopted by the owner into D12 on 6 October 2026 and switched on in production**
(`Voting__CoordinatedIdentity__Enabled=true`, N 5, K 5, phone corroborator off). Applied the same day: 123 votes of
SUB-2026-00134 set aside (see OWNER-DECISIONS.md, D12). The code default stays off, so any other deployment needs the
setting explicitly.

## The rule

A vote meets R2 when all three hold:

1. the voter's account was opened during the vote (`CreatedAt ≥ VotingStartsAt`);
2. the voter's normalized full name (Azerbaijani letters folded to Latin, letters only) is held by **5 or more voting
   accounts** of the competition, counted competition-wide;
3. **5 or more** of those accounts were opened from the voter's own registration address (the IP of the account's
   first sign-in token, which registration creates).

Optionally (`PhoneCorroborates`, off by default), a phone that is not a plausible Azerbaijani mobile may stand in for
condition 3. The backtest found it adds nothing today and has an 18.6 % base rate, so it stays off.

Accounts opened before the vote are never caught, though they count toward a name group. A pair is never a cluster:
thresholds below 3 are raised to 3.

## What it does and does not do

- Always worked out, always shown on the admin review screen ("R2" tag, "R2 koordinasiyalı kimlik (sayılan)" count),
  with no change to the suspicion score.
- Switched on, R2 votes become evidence `CoordinatedIdentity` and an admin may select them (one by one or by their
  name group) and set them aside — with a reason, all or none, one audit entry per vote, the leaderboard cache cleared.
- Nothing is automatic. Votes set aside earlier are untouched. Phone clusters keep working as before and take
  precedence when a vote meets both.

## Configuration

```
Voting__CoordinatedIdentity__Enabled=true          # default false
Voting__CoordinatedIdentity__NameGroupMin=5        # default 5, never below 3
Voting__CoordinatedIdentity__SameIpMin=5           # default 5, never below 3
Voting__CoordinatedIdentity__PhoneCorroborates=false
```

Rollback: unset `Enabled` (or deploy the previous release). Votes already set aside under R2 stay set aside until an
admin restores them, with a reason, from the same screen.

## Backtest (6 October 2026, read-only, competition 7)

R2 as above meets 100 counted votes, all for SUB-2026-00134 (valid 161 → 61, rank 1 → 5), and independently re-finds
84 of the 95 votes of #134 and 5 of #138 set aside earlier under the phone rule. No vote of any other work, and no
account opened before the vote, meets it. Thresholds from 5 to 10 give the same result; at 3 it begins to touch
SUB-2026-00068 (a 4-account group), which is why 5 is the floor recommended.
