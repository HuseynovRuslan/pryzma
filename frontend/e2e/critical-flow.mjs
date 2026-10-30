// The journey the platform exists for, in a real browser, on the competitions that are actually published:
//   visitor → home poster → competition → İştirak et → register → form → files → preview → submit →
//   İşlərim → admin finds it by reference → sees who sent it → opens the file → changes the status →
//   the participant sees the new status.
// Then the ways it must not work: a missing answer, a refused format, a file over the limit, a signed-out
// submit, another participant's entry, and the admin area from a participant's account.
//
// Every entry it creates is its own: a fresh throwaway account per competition, never an existing one. Run it
// against a QA stack, never against production (prod-smoke.mjs is the one that touches the live site).
// Usage: BASE_URL=http://localhost:5174 QA_ADMIN_EMAIL=… QA_ADMIN_PASSWORD=… node e2e/critical-flow.mjs [outDir] [width]
import { chromium } from '@playwright/test'
import { mkdirSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'

const base = (process.env.BASE_URL ?? 'http://localhost:5174').replace(/\/$/, '')
const out = process.argv[2] ?? 'e2e/screenshots'
const width = Number(process.argv[3] ?? 1280)
const adminEmail = process.env.QA_ADMIN_EMAIL
const adminPassword = process.env.QA_ADMIN_PASSWORD
if (!adminEmail || !adminPassword) throw new Error('QA_ADMIN_EMAIL / QA_ADMIN_PASSWORD are not set')
const fixture = (name) => fileURLToPath(new URL(`./fixtures/${name}`, import.meta.url))
mkdirSync(out, { recursive: true })

const COMPETITIONS = [
  { slug: 'novxaninin-simvolu', settlement: 'Novxanı' },
  { slug: 'emircanin-simvolu', settlement: 'Əmircan' },
]
const run = Date.now().toString(36)
const PASSWORD = 'meydan2026'
const PHONE = '+994 50 777 11 22'

// A real PNG padded past the 10 MB limit: the form must refuse it on the spot, without uploading anything.
const oversized = join(out, `.boyuk-${run}.png`)
{
  const png = Buffer.alloc(10 * 1024 * 1024 + 1)
  Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]).copy(png)
  writeFileSync(oversized, png)
}

const browser = await chromium.launch({ channel: 'chromium' })
const contexts = []
const failures = []
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
const shot = (p, name) => p.screenshot({ path: `${out}/cf-${name}-${width}.png`, fullPage: true })

/** Runs one named area and records its verdict, so one broken area does not hide the rest. */
async function area(name, body) {
  console.log(`▶ ${name}`)
  try {
    await body()
    console.log(`PASS  ${name}`)
    return true
  } catch (err) {
    failures.push(`${name}: ${err.message}`)
    console.error(`FAIL  ${name}: ${err.message}`)
    for (const [i, ctx] of contexts.entries())
      for (const pg of ctx.pages())
        await pg.screenshot({ path: `${out}/cf-failure-${name.replace(/\W+/g, '-')}-${i}-${width}.png`, fullPage: true }).catch(() => {})
    return false
  }
}

/** The status the server reports for a request made from inside the page (same origin, no token). */
const statusOf = (p, url, init) =>
  p.evaluate(async ([u, i]) => (await fetch(u, i)).status, [url, init ?? {}])

/**
 * The SPA keeps its access token in memory and sends it as a bearer header, so a bare fetch() from the page is
 * an anonymous request. Where a test means "as this signed-in person", it asks the API for a token first.
 */
const tokenFor = (p, email, password) =>
  p.evaluate(async ([e, pw]) => {
    const r = await fetch('/api/auth/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ email: e, password: pw }),
    })
    if (!r.ok) throw new Error(`login for the test token failed: ${r.status}`)
    return (await r.json()).accessToken
  }, [email, password])

const asUser = (token, init = {}) => ({ ...init, headers: { ...init.headers, Authorization: `Bearer ${token}` } })

async function register(p, email) {
  await p.getByRole('heading', { name: 'Qeydiyyat' }).waitFor()
  await p.getByLabel('Ad', { exact: true }).fill('Qa')
  await p.getByLabel('Soyad').fill('Sınaqlı')
  await p.getByLabel('Telefon').fill(PHONE)
  await p.getByLabel('E-mail').fill(email)
  await p.getByLabel('Şifrə', { exact: true }).fill(PASSWORD)
  await p.getByLabel('Şifrəni təkrar et').fill(PASSWORD)
  await p.locator('input[name="acceptTerms"]').check()
  await p.getByRole('button', { name: 'Hesab yarat' }).click()
}

async function signIn(p, email, password) {
  await p.goto(base + '/login')
  await p.getByLabel('E-mail').fill(email)
  await p.getByLabel('Şifrə', { exact: true }).fill(password)
  await p.getByRole('button', { name: 'Daxil ol', exact: true }).click()
  await p.waitForURL((u) => !u.pathname.startsWith('/login'))
}

// ---- The whole journey, per competition ----------------------------------------------------------------

async function journey({ slug, settlement }) {
  const p = await newPage()
  const email = `qa-${slug}-${run}@meydan.test`
  const title = `${settlement} üçün simvol (QA ${run})`

  // A visitor arrives at the home page and the competition's own poster is the way in.
  await p.goto(base + '/')
  await p.getByRole('heading', { name: 'Hazırda açıq müsabiqələr' }).waitFor()
  const poster = p.locator(`.poster-grid a[href="/competitions/${slug}"]`).first()
  await poster.waitFor()
  await poster.click()
  await p.waitForURL(new RegExp(`/competitions/${slug}$`))
  await p.getByRole('heading', { level: 1, name: new RegExp(settlement) }).waitFor()
  // The page promises what the form will actually accept.
  await p.getByText(/Qəbul olunan formatlar: PNG, JPEG, WEBP və PDF/).waitFor()
  await shot(p, `${slug}-01-competition`)
  step('home poster → competition page, with the real file rules on it')

  // "İştirak et" from a signed-out visitor keeps the intent and asks who they are.
  await p.getByRole('button', { name: 'İştirak et' }).first().click()
  await p.waitForURL(/\/login\?next=/)
  await p.getByText('Seçiminiz saxlanılıb').waitFor()
  await p.getByRole('link', { name: 'Qeydiyyatdan keçin' }).click()
  await p.waitForURL(/\/register\?next=/)
  await register(p, email)
  await p.waitForURL(/\/submissions\/\d+$/)
  const submissionUrl = p.url()
  const submissionId = Number(submissionUrl.split('/').pop())
  step(`İştirak et → register → straight into the ${settlement} draft (#${submissionId})`)

  // Step 1 refuses to move on while a required answer is missing, and names the field.
  await p.getByRole('button', { name: 'Davam et' }).click()
  await p.getByText('“Nominasiya” sahəsini doldurun.').waitFor()
  await p.getByText('“İşin adı” sahəsini doldurun.').waitFor()
  await shot(p, `${slug}-02-required`)

  await p.getByLabel('Nominasiya').selectOption('Simvol və Loqotip Dizaynı')
  await p.getByLabel('İşin adı').fill(title)
  await p.getByLabel('Konseptin izahı').fill(`${settlement} qəsəbəsinin dəniz və daş kimliyini birləşdirən simvol.`)
  await p.getByLabel('İstifadə olunan simvollar və onların mənası').fill('Xilə-buta, əhəngdaşı faktura, dalğa xətti.')
  await p.getByText(/Qaralama saxlanıldı/).waitFor({ timeout: 15_000 })
  step('required answers named one by one, then autosaved')

  await p.getByRole('button', { name: 'Davam et' }).click()
  await p.waitForURL(/step=files/)
  const input = p.locator('input[type="file"]')

  // A format the competition does not accept, and a file past its size limit: both refused with a reason,
  // both dismissable, and neither leaves anything behind.
  await input.setInputFiles(fixture('zererli.svg'))
  await p.getByText(/Bu format qəbul olunmur/).waitFor()
  await p.getByRole('button', { name: 'Bağla' }).click()
  await input.setInputFiles(oversized)
  await p.getByText(/Fayl çox böyükdür/).waitFor()
  await p.getByRole('button', { name: 'Bağla' }).click()
  check((await p.locator('.filecard').count()) === 0, 'a refused file was attached anyway')
  step('SVG and an 11 MB file refused with a reason; nothing attached')

  await input.setInputFiles([fixture('eskiz.png'), fixture('izah.pdf')])
  await p.locator('.filecard:not(.is-uploading):not(.is-error)').nth(1).waitFor({ timeout: 60_000 })
  await p.locator('.filecard img.thumb').first().waitFor({ timeout: 60_000 })
  await shot(p, `${slug}-03-files`)
  step('PNG and PDF uploaded, both with a server-made thumbnail')

  await p.getByRole('button', { name: 'Önizləməyə keç' }).click()
  await p.waitForURL(/step=preview/)
  await p.getByText(title).waitFor()
  await p.waitForFunction(() => !document.querySelector('.sub__actions--final .btn--primary')?.hasAttribute('disabled'))
  await shot(p, `${slug}-04-preview`)

  await p.getByRole('button', { name: 'Təklifi təqdim et' }).click()
  await p.getByRole('heading', { name: 'Təklifiniz qəbul edildi.' }).waitFor()
  const reference = (await p.locator('.receipt__ref-value').textContent()).trim()
  check(/^SUB-\d{4}-\d{5}$/.test(reference), `unexpected reference ${reference}`)
  await shot(p, `${slug}-05-receipt`)
  step(`submitted, reference ${reference}`)

  // Sending it twice must not make a second entry: the form is read-only and the server refuses a repeat.
  await p.goto(submissionUrl)
  await p.getByText(`Təklif #${reference}`).waitFor()
  check((await p.getByRole('button', { name: 'Təklifi təqdim et' }).count()) === 0, 'submitted work still offers to submit')
  const token = await tokenFor(p, email, PASSWORD)
  const repeat = await statusOf(p, `/api/submissions/${submissionId}/submit`, asUser(token, { method: 'POST' }))
  check(repeat === 409, `a second submit answered ${repeat}, not 409`)
  step('a second submit is refused (409) and the work opens read-only')

  await p.goto(base + '/my/submissions')
  await p.getByText(`Təklif #${reference}`).waitFor()
  await p.locator('.subs__status', { hasText: 'Göndərilib' }).waitFor()
  await p.getByText(title).waitFor()
  await shot(p, `${slug}-06-islerim`)
  step('İşlərim lists it as Göndərilib')

  // ---- The admin side of the same entry ----------------------------------------------------------
  const admin = await newPage()
  await signIn(admin, adminEmail, adminPassword)
  await admin.goto(base + '/admin/submissions')
  await admin.getByLabel('Axtarış').fill(reference)
  await admin.waitForURL(/q=/)
  await admin.getByText('1 təklif', { exact: true }).waitFor()
  await admin.getByRole('link', { name: reference }).click()
  await admin.waitForURL(/\/admin\/submissions\/\d+$/)

  // Who sent it, exactly as they typed it at registration.
  await admin.getByText('Qa Sınaqlı').first().waitFor()
  await admin.getByRole('link', { name: email }).waitFor()
  await admin.getByText(PHONE).waitFor()
  // And what they sent.
  await admin.getByText(title).first().waitFor()
  await admin.getByText(`${settlement} qəsəbəsinin dəniz və daş kimliyini birləşdirən simvol.`).waitFor()
  await admin.getByText('Simvol və Loqotip Dizaynı').first().waitFor()
  await admin.getByText('eskiz.png').first().waitFor()
  await admin.getByText('izah.pdf').first().waitFor()
  await shot(admin, `${slug}-07-admin-detail`)
  step('admin sees the participant’s name, e-mail and phone, the answers and both files')

  // The admin opens the original in a new tab, served only to them.
  const [opened] = await Promise.all([
    admin.context().waitForEvent('page'),
    admin.getByRole('button', { name: 'eskiz.png — orijinalı yeni vərəqdə aç' }).click(),
  ])
  check(opened.url().startsWith('blob:'), `opened file url was ${opened.url()}`)
  await opened.close()
  const adminToken = await tokenFor(admin, adminEmail, adminPassword)
  const strayFile = await statusOf(admin, `/api/submissions/${submissionId}/files/1000000`, asUser(adminToken))
  check(strayFile === 404, 'a file id that is not on this submission should be 404')
  step('admin opens the original file in a new tab')

  await admin.getByLabel('Yeni status').selectOption('UnderReview')
  await admin.getByLabel('Şərh (istəyə bağlı)').fill(`QA ${run}: axın yoxlanışı.`)
  await admin.getByRole('button', { name: 'Statusu dəyiş' }).click()
  await admin.getByText('Status dəyişdi: Yoxlanılır.').waitFor()
  step('admin moves it to Yoxlanılır')

  await p.goto(base + '/my/submissions')
  await p.locator('.subs__status', { hasText: 'Qiymətləndirilir' }).waitFor()
  await shot(p, `${slug}-08-status-changed`)
  step('the participant sees the new status in İşlərim')

  return { email, reference, submissionId, submissionUrl, page: p }
}

// ---- Run ------------------------------------------------------------------------------------------------

const entries = {}
for (const c of COMPETITIONS)
  await area(`submission flow — ${c.settlement}`, async () => {
    entries[c.slug] = await journey(c)
  })

await area('signed-out and cross-participant access', async () => {
  const first = entries[COMPETITIONS[0].slug]
  check(first, 'the first competition’s journey did not get far enough to test access against')

  // Signed out: the entry form sends people to the login page and the API refuses outright.
  const anon = await newPage()
  await anon.goto(base + `/competitions/${COMPETITIONS[0].slug}/submit`)
  await anon.waitForURL(/\/login/)
  const anonStart = await statusOf(anon, '/api/submissions', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ competitionSlug: COMPETITIONS[0].slug }),
  })
  check(anonStart === 401, `anonymous start answered ${anonStart}, not 401`)
  check((await statusOf(anon, '/api/me/submissions')) === 401, 'anonymous could read İşlərim')
  check((await statusOf(anon, `/api/submissions/${first.submissionId}/files/1`)) === 401, 'anonymous could read a file')
  step('signed out: redirected to login; the API answers 401')

  // Another participant: the entry is not theirs, so for them it does not exist.
  const other = await newPage()
  const intruderEmail = `qa-intruder-${run}@meydan.test`
  await other.goto(base + '/register')
  await register(other, intruderEmail)
  await other.waitForURL(base + '/')
  await other.goto(first.submissionUrl)
  await other.getByRole('heading', { name: /tapılmadı/i }).waitFor()
  const theirs = await tokenFor(other, intruderEmail, PASSWORD)
  check((await statusOf(other, `/api/submissions/${first.submissionId}`, asUser(theirs))) === 404, 'another participant could read the entry')
  check((await statusOf(other, `/api/submissions/${first.submissionId}/files/1`, asUser(theirs))) === 404, 'another participant could read a file')
  const mine = await other.evaluate(async (t) => (await fetch('/api/me/submissions', { headers: { Authorization: `Bearer ${t}` } })).json(), theirs)
  check(mine.length === 0, 'İşlərim showed someone else’s work')
  step('another participant: 404 on the entry and its files; their own İşlərim is empty')

  // And the admin area is closed to them, in the browser and at the API.
  await other.goto(base + '/admin/submissions')
  await other.getByRole('heading', { name: 'Bu bölmə yalnız administratorlar üçündür' }).waitFor()
  check((await other.locator('.a-nav, .a-table').count()) === 0, 'the admin shell rendered for a participant')
  const adminApi = await statusOf(other, '/api/admin/submissions', asUser(theirs))
  check([401, 403].includes(adminApi), `a participant reached the admin API: ${adminApi}`)
  await shot(other, '09-admin-closed')
  step('a participant cannot reach the admin area or its API')
})

await browser.close()

console.log('')
if (failures.length) {
  console.error(`FAILED — ${failures.length} area(s):`)
  for (const f of failures) console.error(`  • ${f}`)
  process.exitCode = 1
} else {
  const made = Object.values(entries).map((e) => `${e.reference} (${e.email})`).join(', ')
  console.log(`Critical flow passed at ${width}px for both competitions. QA entries: ${made}`)
}
