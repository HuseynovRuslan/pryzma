# Robot check at registration — Cloudflare Turnstile (prepared, not deployed)

Branch `feature/captcha-turnstile`. Off unless configured; with today's production settings it changes nothing
except the page's CSP (see below).

## Why Turnstile

| | Turnstile | hCaptcha | reCAPTCHA v3 |
|---|---|---|---|
| Cost | free, no volume cap | free tier | free to 10k/month |
| Friction | usually invisible, no puzzle | often a puzzle | invisible, score only |
| Privacy | no ad tracking, no cookies for tracking | similar | Google account signals |
| Server check | one POST (siteverify) | same | same + score threshold to tune |

Turnstile gives a pass/fail (no score to tune), is usually invisible, and Cloudflare is already a common dependency.

## What it stops and what it does not

It stops scripted registration (headless browsers, HTTP scripts). It does **not** stop a person — or a paid click
farm — registering by hand. The #134 pattern (median 9 s from registration to vote, bursts of 50 accounts an hour
from 4 addresses) looks automated or semi-automated, so Turnstile should cut it substantially; it is a cost raiser,
not proof, and works best together with e-mail confirmation.

## The change

- `Captcha:Provider=Turnstile`, `Captcha:SiteKey` (public), `Captcha:SecretKey` (env only). All three set = on.
- `GET /api/config` returns `captchaSiteKey` (null when off). The register page then loads Cloudflare's script, shows
  the widget (action `register`) and sends `captchaToken`; after a refused submit it asks for a fresh token.
- `POST /api/auth/register` checks the token first: missing/failed/reused/other-action → 400 on field `captcha`.
- Provider unreachable or our key rejected → 503 "try again" (`Captcha:FailOpen=false`, default) — an outage cannot
  be used to slip a wave through. `FailOpen=true` lets registrations through and logs a warning.
- Sign-in and password reset are never asked. Existing accounts are untouched. Voting is asked too — see "The check
  on the vote" below.
- CSP (`security-headers.ts`, `nginx-headers.conf`): `script-src` and `frame-src` allow
  `https://challenges.cloudflare.com` — the only third-party origin on the site. This part ships with the branch even
  while the check is off; the script is only loaded when a site key is configured.

## The check on the vote

The check at registration stops a script from opening accounts; it does not stop a script that already holds
sessions from casting with them. So casting a vote asks for the same check, with its own action.

- On whenever the check is on (`Captcha:OnVote`, default `true`); `Captcha__OnVote=false` in `/opt/meydan/.env`
  turns off the vote's check alone and keeps it on the forms. `GET /api/config` says `voteCaptcha`.
- The vote dialog shows the widget (action `vote`) under the question; "Səsi təsdiqlə" stays disabled until it has
  passed, and the token goes with the vote (`POST …/vote`, body `{"captchaToken": …}`; with the check off the body
  may be empty, as before). After a refused vote the dialog asks for the check afresh: a token works once.
- The server asks after the refusals that need no outside call (window, the voter's own work, e-mail confirmation)
  and before the vote is written: missing/failed/reused/other-action → 400 on field `captcha`, no vote; provider
  unreachable → 503 unless `Captcha:FailOpen` (an outage stops voting rather than letting a wave through).
- A tab opened before this release has no widget in its dialog: its vote is refused with the 400 above until the
  page is reloaded.

## Owner steps to enable

1. Cloudflare account → Turnstile → add a widget for `pryzma.az` (mode "Managed"). This gives a site key and a secret.
2. In `/opt/meydan/.env`: `Captcha__Provider=Turnstile`, `Captcha__SiteKey=…`, `Captcha__SecretKey=…` (and `Captcha__FailOpen` if wanted); `deploy/compose.yml` passes them to the API container from this branch on. Deploy the branch, or recreate the API container if it is already deployed.
3. Check: the register page shows the widget; a test registration from a normal browser passes.
4. Consider a line in the privacy page: the robot check is run by Cloudflare, which sees the visitor's IP address.

Rollback: remove `Captcha__Provider` (or a key) and recreate the API container — the widget disappears at once.

## Tests

Backend: 4 verifier tests without network (pass sends only secret/token/IP; fail, reused, other-action fail; no
token fails without a call; our bad key, HTTP 502, network error and timeout are "unavailable") and 4 API tests
(off by default with `captchaSiteKey` null; on: missing/failed token 400 on `captcha`, passing token registers with
action `register`; outage 503 unless `FailOpen`; sign-in never asked). Frontend: 3 tests (no widget or script while
off; must pass before submit and its token is sent; a refused submit asks for a fresh token).

The vote (`VotingTests`): own work refused before any check; missing/failed token 400 on `captcha` and no vote; a
passing token counts, asked with action `vote`; outage 503; `OnVote=false` leaves the vote unasked while the forms
keep the check. Frontend (`voting.test.tsx`): the dialog's confirm waits for the check and sends its token; a
refused vote asks afresh; no widget while the server does not ask on the vote.
