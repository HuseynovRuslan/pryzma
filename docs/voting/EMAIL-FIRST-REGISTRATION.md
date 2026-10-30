# Email-first registration (prepared, not deployed)

Owner decision, 6 October 2026: no account may exist before its e-mail address is proven. Branch
`feature/email-first-registration`. Off unless `Auth__Registration__EmailFirst=true` (and mail is on).

## Flow

1. **Start** — `/register` asks for the address and the robot check (Turnstile). `POST /api/auth/registration/start`
   checks the robot check first, then: if the address already has an account, that account's owner is mailed
   ("you already have an account — sign in or reset your password") and the form gets the **same 202** as for a new
   address, so it cannot be used to find out who is registered. Otherwise one `PendingRegistrations` row per address
   is created or refreshed and a mail is queued. No name, phone or password is asked for or stored.
2. **Verify** — the mail's link `https://pryzma.az/register/verify#token=…` (fragment: never sent to a server log).
   `POST /api/auth/registration/verify` spends the token (one-time, 45 min from the mail) and returns a **proof**
   valid for 30 min, which the page keeps in this tab's session storage.
3. **Complete** — `/register/complete` asks for name, surname, phone, password, terms.
   `POST /api/auth/registration/complete` in one transaction: spends the proof (the delete is the condition), checks
   the address is still free and the phone (Azerbaijani mobile, not in use), creates the account with
   `EmailConfirmed = true`, gives it the participant role, then signs it in exactly like a normal registration.
   A refused form (phone in use, weak password) rolls the transaction back, so the same proof can be used again for
   the corrected form; an expired or used proof sends the person back to step 1.

While the flow is on, the one-step `POST /api/auth/register` answers 409, and `/api/config` says
`registrationFlow: "email-first"` so the SPA shows the new steps.

## Data

New table `PendingRegistrations` (migration `Phase17EmailFirstRegistration`, additive):
`Id`, `Email`, `NormalizedEmail` (unique), `TokenHash`/`TokenExpiresAt`, `ConfirmedAt`, `ProofHash`/`ProofExpiresAt`,
`CreatedAt`, `ExpiresAt` (24 h after the last link; swept on every start), `SendCount`/`SendWindowStartedAt`/`LastSentAt`.
`EmailOutbox.UserId` becomes nullable and gains `PendingRegistrationId` (cascade), with a check that every row has a
recipient. Existing rows are untouched (all have an account).

Since `Phase22RegistrationLinks` (21 October 2026) every mail's link is its own row in `PendingRegistrationLinks`
(`PendingRegistrationId` with cascade, `TokenHash` unique, `CreatedAt`, `ExpiresAt`) and the `TokenHash`/`TokenExpiresAt`
columns are gone; links still valid at the upgrade were carried over, and the downgrade keeps the newest one.

## Security

- Tokens and proofs: 32 random bytes (CSPRNG), base64url; only their SHA-256 is stored. The link's token is minted
  when the mail is composed by the outbox worker, so it exists only in the mail.
- One-time: verifying and completing are conditional writes (`DELETE … WHERE Id = <the link>`, `DELETE … WHERE
  ProofHash = …`); of two simultaneous requests exactly one wins. One pending row per address (unique index) and the
  delete-as-claim mean one proof makes at most one account.
- Several links (owner, 21 October 2026): a new link no longer cancels the earlier ones — people opened an older mail
  of the same thread and were refused. Every link works once until its own 45 minutes run out; opening one issues a
  fresh proof, which replaces the one before. All of them go when the account is created or the registration is swept.
- Rate limits: per address one link a minute and ten a day (five until 21 October 2026; 429 otherwise); per IP the existing auth limit
  (20 requests/minute); verification under the stricter password limit (5 per 15 minutes per IP).
- Enumeration: start answers the same for known and unknown addresses; the owner of a known address is told by mail
  (rationed the same way).
- The robot check stays on step 1; the phone rule and uniqueness run on step 3; R2, phone clusters and the vote
  rules are unchanged. Accounts made this way are confirmed, so the vote gate lets them vote at once.

## Rollout

1. Deploy the branch (migration `Phase17EmailFirstRegistration` runs in the deploy's migrate step; it is additive).
   With the default `Auth__Registration__EmailFirst=false` nothing changes for anyone.
2. In `/opt/meydan/.env` set `Auth__Registration__EmailFirst=true`; recreate **only the API**. `/api/config` then says
   `registrationFlow: "email-first"` and the next page load shows the new `/register`.
3. Smoke: `/register` shows only e-mail + Turnstile; a real address receives "PRYZMA: e-mailinizi təsdiqləyin"; the link
   opens the completion form; the account is created confirmed and signed in; the ballot offers "Səs ver" at once;
   `POST /api/auth/register` answers 409; existing accounts sign in and vote as before.

## Rollback

- `Auth__Registration__EmailFirst=false` and recreate the API: the one-step form is back at once (the
  `RequiredForAccountsFrom` vote gate still asks those accounts to confirm). Pending rows simply expire.
- Code: redeploy `ce2dc33`. The migration can stay (nothing reads the new table then); its `Down` deletes
  registration mail first so it can be reversed if ever needed.
