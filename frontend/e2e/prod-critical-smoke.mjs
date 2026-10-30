// One smoke test of the critical submission path against the LIVE site (https://prizma.qrlog.az).
//
// It reads both competitions, then walks the whole participant journey on ONE of them with a throwaway
// account: register → İştirak et → answers → file → submit → İşlərim → the originals stay private. Two live
// competitions would mean two pieces of fake work in real competitions; the second one is covered read-only
// (its page, its phase, its form fields, its file rules), which is what a second entry would have proved.
//
// It writes exactly two rows: one account and one submission, both named so nobody mistakes them for an
// entry. It deletes nothing — production data is never removed from a test — and prints what to delete by
// hand afterwards (Admin → Təkliflər → Həmişəlik sil, then Admin → İştirakçılar → Həmişəlik sil).
// The admin half of the journey is checked on the QA stack (critical-flow.mjs); here the same facts are read
// back out of the live database read-only, because signing in as the admin from a script would spend the
// account's lockout budget.
//
// Usage: node e2e/prod-critical-smoke.mjs [outDir] [width]
import { chromium } from '@playwright/test'
import { randomBytes } from 'node:crypto'
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'

const base = (process.env.BASE_URL ?? 'https://prizma.qrlog.az').replace(/\/$/, '')
const out = process.argv[2] ?? 'e2e/screenshots'
const width = Number(process.argv[3] ?? 1280)
const fixture = (name) => fileURLToPath(new URL(`./fixtures/${name}`, import.meta.url))
mkdirSync(out, { recursive: true })

const COMPETITIONS = [
  { slug: 'novxaninin-simvolu', settlement: 'Novxanı' },
  { slug: 'emircanin-simvolu', settlement: 'Əmircan' },
]
const FIELDS = ['nomination', 'title', 'concept', 'symbols', 'files', 'notes']
const FILE_RULES = 'Qəbul olunan formatlar: PNG, JPEG, WEBP və PDF. Ən çox 5 fayl, hər biri ən çox 10 MB.'
const run = Date.now().toString(36)
const warnings = []
// SMOKE_RESUME=<file written by an earlier run>: finish that run's entry instead of starting another. A draft
// left behind by a failed run never reaches the admin lists, so it could never be deleted — it gets finished.
const resume = process.env.SMOKE_RESUME ? JSON.parse(readFileSync(process.env.SMOKE_RESUME, 'utf8')) : null

const browser = await chromium.launch({ channel: 'chromium' })
const contexts = []
const newPage = async () => {
  const ctx = await browser.newContext({
    viewport: { width, height: width < 768 ? 844 : 900 },
    deviceScaleFactor: width < 768 ? 2 : 1,
    reducedMotion: 'reduce',
  })
  contexts.push(ctx)
  return ctx.newPage()
}
const step = (msg) => console.log(`  ✓ ${msg}`)
const check = (cond, msg) => {
  if (!cond) throw new Error(msg)
}
const shot = (p, name) => p.screenshot({ path: `${out}/prod-${name}-${width}.png`, fullPage: true })
const getJson = (p, url, init) =>
  p.evaluate(async ([u, i]) => {
    const r = await fetch(u, i)
    return { status: r.status, body: r.headers.get('content-type')?.includes('json') ? await r.json() : null }
  }, [url, init ?? {}])
const statusOf = async (p, url, init) => (await getJson(p, url, init)).status

try {
  // ---- The live site as a stranger finds it ---------------------------------------------------------
  const visitor = await newPage()
  const home = await visitor.goto(base + '/')
  const h = home.headers()
  check(home.status() === 200, `home → ${home.status()}`)
  check((h['content-security-policy'] ?? '').includes("script-src 'self'"), 'CSP on the SPA')
  check((h['strict-transport-security'] ?? '').includes('max-age=31536000'), 'HSTS from the edge')
  check(h['x-frame-options'] === 'DENY' && h['x-content-type-options'] === 'nosniff', 'frame/nosniff headers')
  check((await statusOf(visitor, '/health/ready')) === 200, 'health/ready')
  check((await statusOf(visitor, '/api/admin/submissions')) === 401, 'admin API closed to anonymous')
  await visitor.getByRole('heading', { name: 'Hazırda açıq müsabiqələr' }).waitFor()
  await shot(visitor, '01-home')
  step('home 200, CSP/HSTS/DENY/nosniff, health ready, admin API 401')

  // What state each competition is actually in. A closed one is reported, never assumed away: the entries it
  // would have taken are the whole point of the launch, so it is louder than a passing line.
  for (const c of COMPETITIONS) {
    const { status, body } = await getJson(visitor, `/api/competitions/${c.slug}`)
    check(status === 200, `${c.slug} → ${status}`)
    c.open = body.phase === 'Open' && body.isAcceptingSubmissions
    check(body.coverImage, `${c.settlement} has no poster`)
    check((body.requirements ?? '').includes(FILE_RULES), `${c.settlement}: the page does not state the real file rules`)
    if (c.open) {
      await visitor.locator(`.poster-grid a[href="/competitions/${c.slug}"]`).first().waitFor()
      step(`${c.settlement}: Open and accepting, on the home page, poster and file rules published`)
    } else {
      warnings.push(`${c.settlement} (${c.slug}) is ${body.status}/${body.phase} — nobody can enter it`)
      console.log(`  ! ${c.settlement}: ${body.status}/${body.phase} — poster and text are published, but the form is shut`)
    }
  }
  const ENTERED = COMPETITIONS.find((c) => c.open)
  check(ENTERED, 'no competition on the live site is accepting work')

  // ---- One real journey, with a throwaway account ---------------------------------------------------
  const p = await newPage()
  const email = resume?.email ?? `sinaq-${run}@prizma.qrlog.az`
  const password = resume?.password ?? `Sinaq-${randomBytes(12).toString('base64url')}-9a`
  const title = resume?.title ?? `SINAQ — axın yoxlaması ${run} (silinəcək)`
  const record = process.env.SMOKE_RESUME ?? join(out, `prod-smoke-${run}.json`)
  let submissionId

  if (resume) {
    await p.goto(base + '/login')
    await p.getByLabel('E-mail').fill(email)
    await p.getByLabel('Şifrə', { exact: true }).fill(password)
    await p.getByRole('button', { name: 'Daxil ol', exact: true }).click()
    await p.waitForURL((u) => !u.pathname.startsWith('/login'))
    submissionId = resume.submissionId
    await p.goto(`${base}/submissions/${submissionId}`)
    await p.getByLabel('Nominasiya').waitFor()
    step(`resumed the entry an earlier run left open (#${submissionId}, ${email})`)
  } else {
    await p.goto(`${base}/competitions/${ENTERED.slug}`)
    await p.getByRole('heading', { level: 1, name: new RegExp(ENTERED.settlement) }).waitFor()
    await p.getByRole('button', { name: 'İştirak et' }).first().click()
    await p.waitForURL(/\/login\?next=/)
    await p.getByRole('link', { name: 'Qeydiyyatdan keçin' }).click()
    await p.waitForURL(/\/register\?next=/)
    await p.getByLabel('Ad', { exact: true }).fill('Sınaq')
    await p.getByLabel('Soyad').fill('Yoxlama')
    await p.getByLabel('Telefon').fill('+994 50 000 00 00')
    await p.getByLabel('E-mail').fill(email)
    await p.getByLabel('Şifrə', { exact: true }).fill(password)
    await p.getByLabel('Şifrəni təkrar et').fill(password)
    await p.locator('input[name="acceptTerms"]').check()
    await p.getByRole('button', { name: 'Hesab yarat' }).click()
    await p.waitForURL(/\/submissions\/\d+$/)
    submissionId = Number(p.url().split('/').pop())
    // Written before anything else can fail, so the account is always traceable for cleanup.
    writeFileSync(record, JSON.stringify({ base, email, password, submissionId, title }, null, 2))
    step(`İştirak et → register → draft #${submissionId} (credentials in ${record})`)
  }

  // The SPA keeps its token in memory, so a fetch from the page is anonymous unless it carries one.
  const token = await p.evaluate(async ([e, pw]) => {
    const r = await fetch('/api/auth/login', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ email: e, password: pw }),
    })
    return (await r.json()).accessToken
  }, [email, password])
  const auth = { headers: { Authorization: `Bearer ${token}` } }

  // The form the live site actually serves: the six launch questions and the limits its page promises.
  const draft = (await getJson(p, `/api/submissions/${submissionId}`, auth)).body
  check(draft.fields.map((f) => f.key).join(',') === FIELDS.join(','), `form fields: ${draft.fields.map((f) => f.key)}`)
  const rules = draft.fields.find((f) => f.key === 'files').files
  check(rules.maxFiles === 5 && rules.maxFileSizeBytes === 10 * 1024 * 1024, `file limits: ${JSON.stringify(rules)}`)
  check(rules.accept.map((a) => a.key.toLowerCase()).sort().join(',') === 'jpeg,pdf,png,webp', `accepts ${rules.accept.map((a) => a.key)}`)
  step('the live form asks the six launch questions, 5 files × 10 MB, PNG/JPEG/WEBP/PDF')

  await p.getByLabel('Nominasiya').selectOption('Simvol və Loqotip Dizaynı')
  await p.getByLabel('İşin adı').fill(title)
  await p.getByLabel('Konseptin izahı').fill('Bu, canlı saytın texniki yoxlamasıdır; real təklif deyil və silinəcək.')
  await p.getByLabel('İstifadə olunan simvollar və onların mənası').fill('Yoxdur — sınaq.')
  await p.getByLabel('Əlavə qeyd').fill(`Avtomatik smoke test ${run}. Bu işi silin.`)
  await p.getByText(/Qaralama saxlanıldı/).waitFor({ timeout: 20_000 })
  await p.getByRole('button', { name: 'Davam et' }).click()
  await p.waitForURL(/step=files/)

  await p.locator('input[type="file"]').setInputFiles(fixture('eskiz.png'))
  await p.locator('.filecard:not(.is-uploading):not(.is-error)').first().waitFor({ timeout: 90_000 })
  await p.locator('.filecard img.thumb').first().waitFor({ timeout: 90_000 })
  step('file uploaded and the server made its thumbnail')

  await p.getByRole('button', { name: 'Önizləməyə keç' }).click()
  await p.waitForURL(/step=preview/)
  await p.getByText(title).waitFor()
  await p.waitForFunction(() => !document.querySelector('.sub__actions--final .btn--primary')?.hasAttribute('disabled'))
  await p.getByRole('button', { name: 'Təklifi təqdim et' }).click()
  await p.getByRole('heading', { name: 'Təklifiniz qəbul edildi.' }).waitFor()
  const reference = (await p.locator('.receipt__ref-value').textContent()).trim()
  check(/^SUB-\d{4}-\d{5}$/.test(reference), `unexpected reference ${reference}`)
  writeFileSync(record, JSON.stringify({ base, email, password, submissionId, title, reference }, null, 2))
  await shot(p, '02-receipt')
  step(`submitted on the live site, reference ${reference}`)

  await p.goto(base + '/my/submissions')
  await p.getByText(`Təklif #${reference}`).waitFor()
  await p.locator('.subs__status', { hasText: 'Göndərilib' }).waitFor()
  await shot(p, '03-islerim')
  step('İşlərim lists it as Göndərilib')

  // A second submit is refused, and what was uploaded belongs to its owner alone.
  check((await statusOf(p, `/api/submissions/${submissionId}/submit`, { ...auth, method: 'POST' })) === 409, 'a second submit was accepted')
  const mine = (await getJson(p, `/api/submissions/${submissionId}`, auth)).body
  for (const f of mine.files) {
    check((await statusOf(p, `/api/submissions/${submissionId}/files/${f.id}`, auth)) === 200, `owner cannot read file ${f.id}`)
    check((await statusOf(visitor, `/api/submissions/${submissionId}/files/${f.id}`)) === 401, `file ${f.id} readable anonymously`)
    check((await statusOf(visitor, `/api/competitions/${ENTERED.slug}/gallery/${reference}/files/${f.id}`)) === 404, 'a public copy exists before any result')
  }
  step('second submit 409; originals: owner 200, anonymous 401; nothing public')

  console.log('')
  console.log(`Production smoke passed at ${width}px on ${base} (entered ${ENTERED.settlement}).`)
  for (const w of warnings) console.log(`  ! ${w}`)
  console.log(`  entry      ${reference} — "${title}"`)
  console.log(`  account    ${email}`)
  console.log('  clean up   Admin → Təkliflər → bu təklif → «Həmişəlik sil», sonra Admin → İştirakçılar → bu hesab → «Həmişəlik sil»')
} catch (err) {
  for (const [i, ctx] of contexts.entries())
    for (const pg of ctx.pages()) await pg.screenshot({ path: `${out}/prod-failure-${i}-${width}.png`, fullPage: true }).catch(() => {})
  console.error(`✗ ${err.message}`)
  process.exitCode = 1
} finally {
  await browser.close()
}
