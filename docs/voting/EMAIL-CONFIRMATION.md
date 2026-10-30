# E-mail confirmation before voting (prepared, not deployed)

Branch `feature/email-verification`. Off unless configured; with today's production settings it changes nothing.

## What exists already

- Durable outbox (`EmailOutbox` table, `EmailOutboxWorker`, retries, expiry, 30-day retention), SMTP sender
  (MailKit, STARTTLS/SSL), Azerbaijani templates, and the password-reset flow that this one copies: the outbox row holds
  only the user id; the token is minted when the mail is sent; the link carries address and token in the URL fragment.
- `AspNetUsers.EmailConfirmed` already exists — **no migration is needed.**

## What production lacks — the blocker

Production runs with `Email__Mode=Disabled` and no `Email__FromAddress`: no mail has ever left the server (all 124
"submission received" mails are Cancelled). 1 of 1163 accounts is confirmed (the seeded admin). Confirmation therefore
needs, first, an SMTP provider account and its credentials — an external account and secrets that only the owner
can create. Until then the requirement stays off by design (see "Safety" below).

Also before enabling: the templates are branded **MEYDAN** (header, footer, sender name `Email__FromName`), while the
site is PRIZMA. Decide the brand for outgoing mail.

## The change

- `Auth:EmailConfirmation:RequiredForAccountsFrom` (ISO date-time, unset = off). Accounts **opened at or after** that
  moment get a confirmation mail at registration and may vote only once confirmed. Accounts opened earlier are never
  asked. Nobody is blocked from signing in, from their profile or from submitting work — only from casting a vote.
- `POST /api/auth/confirm-email {email, token}` — idempotent; a wrong, borrowed, expired or superseded link is 400.
  Confirming does not end the session.
- `POST /api/auth/email-confirmation` (signed in) — a new link: not within a minute of the last, at most 5 per 24 h
  (429 with a plain message otherwise); 204 when already confirmed.
- Link lifetime `Auth:EmailConfirmationTokenLifetime`, default 48 h (1 h – 7 d), a token provider of its own so a
  reset token is never accepted as a confirmation and vice versa. A password change invalidates outstanding links.
- Ballot: a new unconfirmed account sees "E-maili təsdiqləyin" with "Linki yenidən göndər" instead of "Səs ver"
  (`VoteEligibility.EmailNotConfirmed`); the vote endpoint refuses with 403 and the same message. Page `/confirm-email`.
- `UserDto.emailConfirmed` added.

## Safety

- With `Email:Mode` Disabled the requirement is ignored even when the date is set: a link that can never arrive must
  not stop honest people from voting.
- No vote already cast is touched; no existing account is asked; no data migration.

## Production checklist (6 October 2026)

State checked 12:5x: the feature is live in `ad2eca3` but off; `Email__Mode=Disabled`, no sender, no SMTP host or
credentials; `App__PublicUrl=https://pryzma.az` (links are right); outbound ports 587 and 465 are open from the
server. Branding fix `c9bed7e` (branch `feature/email-branding-pryzma`) must ship first — mails otherwise say MEYDAN.

0. **Owner:** a transactional SMTP provider with a verified sender on `pryzma.az` — SPF include, DKIM records and a
   DMARC record (`p=none` to start) in the domain's DNS. Expected volume: new registrations plus resends, a few
   hundred a day. Sender: `PRYZMA <no-reply@pryzma.az>`.
1. **Deploy `c9bed7e`** (templates and sender name follow the platform name; no migration). Outside the QRLog peaks.
2. **Mail on, requirement still off** — in `/opt/meydan/.env` (back it up first):
   ```
   Email__Mode=Smtp
   Email__FromAddress=no-reply@pryzma.az
   Email__FromName=PRYZMA
   Email__Smtp__Host=<provider host>
   Email__Smtp__Port=587
   Email__Smtp__Security=StartTls        # or Port=465 with SslOnConnect
   Email__Smtp__Username=<provider user>
   Email__Smtp__Password=<provider secret>
   ```
   Recreate **only the API** (`IMAGE_TAG` from `current/RELEASE`, `docker compose … up -d --no-deps api`). The web
   container needs nothing. Startup refuses Smtp without a host or a sender address, so a typo shows at once.
   Side effects of mail being on: "Şifrəni unutdum" appears (`passwordReset: true`) and new submissions get their
   receipt mail.
   *Smoke:* `/health/ready` Healthy; `/api/config` → `passwordReset: true`, `captchaSiteKey` still set; the owner
   requests a password reset for **their own existing account** → the mail arrives from PRYZMA in the inbox (not
   spam) with a `https://pryzma.az/reset-password#…` link; the outbox shows the row `Sent`.
3. **Requirement on** — add `Auth__EmailConfirmation__RequiredForAccountsFrom=<ISO date-time with offset>`, set to the
   moment of the switch, e.g. `2026-10-06T15:00:00+04:00` (not in the past: accounts opened before it are never
   asked). Recreate only the API again.
   *Smoke:* one real new account, by the owner or someone who agrees, in a private window (Turnstile and the phone
   rule apply): the ballot shows "E-maili təsdiqləyin" with "Linki yenidən göndər" instead of "Səs ver"; the mail
   arrives; the link opens "E-mail təsdiqləndi"; back on the ballot "Səs ver" is offered at once (casting a vote is
   the tester's own choice). Existing accounts keep "Səs ver"; votes keep arriving from older accounts; R2,
   Turnstile and the phone rule unchanged.
4. **Watch** for the first hour: outbox `email-confirmation` Sent vs Failed; any spike in "Linki yenidən göndər".

Rollback, fastest first — each is an `.env` edit plus an API recreate:
- remove `Auth__EmailConfirmation__RequiredForAccountsFrom` (or leave it empty): nobody is asked any more; mail stays on;
- `Email__Mode=Disabled`: mail off, and the requirement switches itself off with it;
- the branding release: redeploy `ad2eca3` (no migration).
Confirmations already made stay; no vote is touched either way.

## Expected effect and friction

- 142 of the 148 accounts that voted for SUB-2026-00134 after the phone rule use gmail.com addresses, all distinct
  mailboxes (no dot/plus aliasing). Confirmation turns each fake vote into a real mailbox the attacker must open.
- Honest friction: one extra step per new voter; mail can land in spam (SPF/DKIM matter); typo'd addresses (two
  "gamil.com" among the #134 accounts) can never confirm and must re-register. Expect a measurable drop in new-voter
  completion — this is the trade-off the owner decides.
- It does not stop someone who controls many real mailboxes; it raises the cost per fake vote.
