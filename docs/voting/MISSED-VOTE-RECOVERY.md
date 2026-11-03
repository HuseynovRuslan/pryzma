# Missed-vote reconciliation (server-evidenced)

## Scope

This is **not** a manual vote-count editor. It never accepts an arbitrary +N or a fabricated voter. It restores a single existing account's intended vote only when a server-generated **VoteAttempted** audit entry exists and the normal Votes table has no row for that voter in the competition.

## What is recorded

After authentication, work-ownership check, email confirmation and any enabled CAPTCHA check, the voting service commits a VoteAttempted audit entry **before** the vote database transaction. It records user ID, work ID, competition/category and timestamp. Successful voting still records the VoteCast action in the regular transaction.

## Administrator workflow

After the voting window closes, open the competition's Voting tab. If unmatched, eligible server attempts exist, a small 'Bazaya düşməmiş səslər' panel appears with work reference and time. Select relevant attempts, then confirm recovery. The operation writes real Vote rows for the original users and records a VoteRecovered internal audit action; the normal ranking/counting logic already uses those Vote rows. There is **no public audit panel** and no separate audit-history UI.

The service excludes any account which already has a vote (including an invalidated one), entries outside the voting window, the author's own entry, withdrawn/rejected/unplaced works, and users with ambiguous attempts for different works. Every requested attempt must be eligible. It cannot be replayed. Recovery is blocked before voting closes, after finalists are frozen, or after publication of results.

## Limitations and cautions

- **Only future attempts are logged.** This feature does not reconstruct user identity from old HTTP logs, because those logs intentionally omit authenticated voter identity. Historical failures before deployment cannot be automatically restored by this feature.
- A server-recorded intent proves that an authenticated request passed normal preliminary checks. It does not prove why a subsequent write failed. Before approving a recovery, the administrator must check the incident window and related server logs; an automated server-error classifier is not implemented.
- A pre-transaction audit write adds one database write per voting attempt. If the database itself is unavailable before that write, the attempt may not be recoverable.
- Existing VoteCast and VoteRecovered actions are intentionally separate. Do not change or remove original attempts.
- This feature does not alter Vote counts through a shadow total or synthetic accounts.
- Review test results and deployment safeguards before merging. Pull requests do not deploy by themselves.
