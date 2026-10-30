// Production smoke test against the LIVE site (https://meydan.qrlog.az) — read-mostly, and everything it writes is
// marked as a test and closed off afterwards:
//   anonymous browsing → register a test participant → a real submission (phone photo with GPS/EXIF + PNG) in the
//   real competition → İşlərim → originals private, no public derivative before a result → admin finds it, waits for
//   the safe copies (Skia/PDFium in the container), then REJECTS it with a note so it can never be published →
//   the participant sees the status → security headers, health, redirects, listings, config flags.
// Nothing is published. No fake result. The test account and its rejected work stay in the database as what they are;
// its generated credentials are written to <outDir>/p6-test-account-<width>.json only (never committed).
// Usage: BASE_URL=https://meydan.qrlog.az QA_ADMIN_EMAIL=… QA_ADMIN_PASSWORD=… node e2e/prod-smoke.mjs <outDir> <width>
import { chromium } from '@playwright/test'
import { randomBytes } from 'node:crypto'
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'

const base = (process.env.BASE_URL ?? 'https://meydan.qrlog.az').replace(/\/$/, '')
const out = process.argv[2] ?? 'e2e/screenshots'
const width = Number(process.argv[3] ?? 1440)
const adminEmail = process.env.QA_ADMIN_EMAIL
const adminPassword = process.env.QA_ADMIN_PASSWORD
if (!adminEmail || !adminPassword) throw new Error('QA_ADMIN_EMAIL / QA_ADMIN_PASSWORD are not set')
const fixture = (name) => fileURLToPath(new URL(`./fixtures/${name}`, import.meta.url))
mkdirSync(out, { recursive: true })

const CHEVRON = 'baki-kendleri-sevron-dizayni'
const run = Date.now().toString(36)
// SMOKE_RESUME_ID / _EMAIL / _PASSWORD / _REF: pick up a test entry an earlier run left Submitted, instead of adding one.
const resume = process.env.SMOKE_RESUME_ID
  ? { id: process.env.SMOKE_RESUME_ID, email: process.env.SMOKE_RESUME_EMAIL, password: process.env.SMOKE_RESUME_PASSWORD, reference: process.env.SMOKE_RESUME_REF }
  : null
const SECRET_AUTHOR = 'Gizli Muellif Aysel'
const SECRET_CAMERA = 'SecretPhone X9'

// ---- a phone-style JPEG with GPS / camera / author EXIF (orientation 6), as in phase5-flow.mjs ------------------
function exifBlock() {
  const strings = []
  let dataStart = 152
  const add = (bytes) => {
    const off = dataStart + strings.reduce((n, b) => n + b.length, 0)
    strings.push(bytes)
    return off
  }
  const ascii = (s) => Buffer.concat([Buffer.from(s, 'ascii'), Buffer.from([0])])
  const make = ascii('SecretMaker'), model = ascii(SECRET_CAMERA), date = ascii('2026:09:18 10:11:12'), artist = ascii(SECRET_AUTHOR)
  const lat = add(Buffer.from([0, 0, 0, 40, 0, 0, 0, 1, 0, 0, 0, 24, 0, 0, 0, 1, 0, 0, 0, 33, 0, 0, 0, 1]))
  const lon = add(Buffer.from([0, 0, 0, 49, 0, 0, 0, 1, 0, 0, 0, 52, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1]))
  const makeOff = add(make), modelOff = add(model), dateOff = add(date), artistOff = add(artist)
  const d = []
  const u16 = (v) => d.push((v >> 8) & 255, v & 255)
  const u32 = (v) => { u16((v >>> 16) & 0xffff); u16(v & 0xffff) }
  const entry = (tag, type, count, value, short = false) => { u16(tag); u16(type); u32(count); if (short) { u16(value); u16(0) } else u32(value) }
  d.push(0x4d, 0x4d); u16(42); u32(8); u16(6)
  entry(0x010f, 2, make.length, makeOff)
  entry(0x0110, 2, model.length, modelOff)
  entry(0x0112, 3, 1, 6, true)
  entry(0x0132, 2, date.length, dateOff)
  entry(0x013b, 2, artist.length, artistOff)
  entry(0x8825, 4, 1, 98)
  u32(0)
  while (d.length < 98) d.push(0)
  u16(4)
  entry(0x0001, 2, 2, 'N'.charCodeAt(0) << 24)
  entry(0x0002, 5, 3, lat)
  entry(0x0003, 2, 2, 'E'.charCodeAt(0) << 24)
  entry(0x0004, 5, 3, lon)
  u32(0)
  while (d.length < 152) d.push(0)
  return Buffer.concat([Buffer.from(d), ...strings])
}
const phonePhoto = join(out, `.phone-${run}.jpg`)
{
  const jpeg = readFileSync(fixture('variant.jpg'))
  const exif = exifBlock()
  const len = 2 + 6 + exif.length
  const app1 = Buffer.concat([Buffer.from([0xff, 0xe1, len >> 8, len & 255]), Buffer.from('Exif\0\0', 'binary'), exif])
  writeFileSync(phonePhoto, Buffer.concat([jpeg.subarray(0, 2), app1, jpeg.subarray(2)]))
}

const browser = await chromium.launch({ channel: 'chromium' })
const contexts = []
const cspViolations = []
const page = async () => {
  const ctx = await browser.newContext({
    viewport: { width, height: width < 768 ? 844 : 900 },
    deviceScaleFactor: width < 768 ? 2 : 1,
    reducedMotion: 'reduce',
  })
  contexts.push(ctx)
  const p = await ctx.newPage()
  p.on('console', (m) => {
    if (/Content Security Policy|Refused to/i.test(m.text())) cspViolations.push(m.text())
  })
  return p
}
const step = (msg) => console.log(`✓ [${width}] ${msg}`)
const check = (cond, msg) => {
  if (!cond) throw new Error(msg)
}
const overflow = []
async function shot(p, name) {
  const wide = await p.evaluate(() => document.documentElement.scrollWidth - window.innerWidth)
  if (wide > 1) overflow.push(`${name}: +${wide}px`)
  await p.screenshot({ path: `${out}/p6-${name}-${width}.png`, fullPage: true })
}
const fetchIn = (p, url, init) =>
  p.evaluate(
    async ([u, i]) => {
      const r = await fetch(u, i)
      const buf = new Uint8Array(await r.arrayBuffer())
      let latin = ''
      for (const b of buf) latin += String.fromCharCode(b)
      return { status: r.status, type: r.headers.get('content-type'), latin, size: buf.length, headers: Object.fromEntries(r.headers.entries()) }
    },
    [url, init ?? {}],
  )

try {
  // ---- Anonymous: the public site, its headers, the doors that are open --------------------------------
  const visitor = await page()
  const home = await visitor.goto(base + '/')
  const h = home.headers()
  check(home.status() === 200, `home → ${home.status()}`)
  check((h['content-security-policy'] ?? '').includes("script-src 'self'"), 'CSP on the SPA')
  check((h['strict-transport-security'] ?? '').includes('max-age=31536000'), 'HSTS from the edge')
  check(h['x-frame-options'] === 'DENY' && h['x-content-type-options'] === 'nosniff', 'frame/nosniff headers')
  check(!('server' in h) || !/nginx|caddy/i.test(h['server']), `server header hidden (${h['server']})`)
  const config = await fetchIn(visitor, '/api/config')
  check(config.status === 200, 'config endpoint')
  const flags = JSON.parse(config.latin)
  const ready = await fetchIn(visitor, '/health/ready')
  check(ready.status === 200 && ready.latin.includes('"Healthy"') && !/host|password|version|path/i.test(ready.latin), 'health/ready is healthy and terse')
  check((await fetchIn(visitor, '/api/admin/projects')).status === 401, 'admin API closed to anonymous')
  check((await fetchIn(visitor, '/uploads/')).status === 404, 'no /uploads/ path')
  check([403, 404].includes((await fetchIn(visitor, '/assets/')).status), 'no directory listing')
  check((await fetchIn(visitor, '/index.html.map')).status === 404, 'no source maps')
  check((await fetchIn(visitor, '/api/nope')).status === 404, 'unknown API path is a 404')
  const apiHeaders = (await fetchIn(visitor, '/api/home')).headers
  check(apiHeaders['content-security-policy']?.includes("default-src 'none'"), 'API sends its own CSP')
  await shot(visitor, '01-home')
  step(`home 200; CSP, HSTS, DENY, nosniff; health ready; admin API 401; config qrlogLogin=${flags.qrlogLogin} passwordReset=${flags.passwordReset}`)

  await visitor.goto(base + '/forgot-password')
  if (!flags.passwordReset) {
    await visitor.getByText('hələ aktiv deyil').waitFor()
    check((await visitor.getByLabel('E-mail').count()) === 0, 'no address field when mail is off')
    step('forgot-password says honestly that reset mail is not active yet')
  }
  await shot(visitor, '02-forgot')

  // ---- Participant: home → project → competition → Maştağa → register → submit -------------------------
  const p = await page()
  let submissionId, reference, email, password
  if (resume) {
    ;({ id: submissionId, email, password, reference } = resume)
    await p.goto(base + '/login')
    await p.getByLabel('E-mail').fill(email)
    await p.getByLabel('Şifrə', { exact: true }).fill(password)
    await p.getByRole('button', { name: 'Daxil ol' }).click()
    await p.waitForURL((u) => !u.pathname.startsWith('/login'))
    await p.goto(base + '/my/submissions')
    await p.getByText(`Təklif #${reference}`).waitFor()
    step(`resumed test entry ${reference} (signed in as its owner)`)
  } else {
  await p.goto(base + '/')
  await p.locator('main a[href="/projects/baki-kendleri"]').first().click()
  await p.waitForURL(/\/projects\/baki-kendleri$/)
  await p.locator(`main a[href="/competitions/${CHEVRON}"]`).first().click()
  await p.waitForURL(new RegExp(`/competitions/${CHEVRON}`))
  await p.getByRole('heading', { level: 1, name: /şevron/i }).waitFor()
  await p.getByRole('searchbox').fill('mastaga')
  await p.locator('.picker__option', { hasText: 'Maştağa' }).click()
  await p.waitForURL(/category=mastaga/)
  await shot(p, '03-competition')
  await p.locator('.action-bar button, .comp__panel .btn').filter({ visible: true }).first().click()
  await p.waitForURL(/\/login\?next=/)
  await p.getByRole('heading', { name: 'Daxil ol' }).waitFor()
  const qrlogButtons = await p.getByRole('button', { name: /QRLog ilə/ }).count()
  check(qrlogButtons === (flags.qrlogLogin ? 1 : 0), `QRLog button shown=${qrlogButtons} while config says ${flags.qrlogLogin}`)
  await shot(p, '04-login')
  await p.getByRole('link', { name: 'Qeydiyyatdan keçin' }).click()
  await p.getByRole('heading', { name: 'Qeydiyyat' }).waitFor()
  email = `sinaq-${run}-${width}@meydan.qrlog.az`
  // Random per run; kept only in the output folder (design-review/ is git-ignored) for a later resume.
  password = `Sinaq-${randomBytes(12).toString('base64url')}-9a`
  await p.getByLabel('Ad', { exact: true }).fill('Sınaq')
  await p.getByLabel('Soyad').fill(`Deploy ${width}`)
  await p.getByLabel('Telefon').fill('+994 50 000 00 00')
  await p.getByLabel('E-mail').fill(email)
  await p.getByLabel('Şifrə', { exact: true }).fill(password)
  await p.getByLabel('Şifrəni təkrar et').fill(password)
  await p.locator('input[name="acceptTerms"]').check()
  await p.getByRole('button', { name: 'Hesab yarat' }).click()
  await p.waitForURL(/\/submissions\/\d+$/)
  submissionId = p.url().split('/').pop()
  await p.getByLabel('İşin adı').fill(`SINAQ — deploy yoxlaması ${run} (silinəcək)`)
  await p.getByLabel('Konseptin izahı').fill('Bu, istehsal yerləşdirməsinin texniki yoxlamasıdır; real təklif deyil.')
  await p.getByLabel('İstifadə olunan simvollar və onların mənası').fill('Yoxdur — sınaq.')
  await p.getByRole('button', { name: 'Davam et' }).click()
  await p.waitForURL(/step=files/)
  await p.locator('input[type="file"]').setInputFiles([phonePhoto, fixture('eskiz.png')])
  await p.locator('.filecard:not(.is-uploading):not(.is-error)').nth(1).waitFor({ timeout: 60_000 })
  await shot(p, '05-files')
  await p.getByRole('button', { name: 'Önizləməyə keç' }).click()
  await p.waitForURL(/step=preview/)
  await p.waitForFunction(() => !document.querySelector('.sub__actions--final .btn--primary')?.hasAttribute('disabled'))
  await p.getByRole('button', { name: 'Təklifi təqdim et' }).click()
  await p.getByRole('heading', { name: 'Təklifiniz qəbul edildi.' }).waitFor()
  reference = (await p.locator('.receipt__ref-value').textContent()).trim()
  writeFileSync(join(out, `p6-test-account-${width}.json`), JSON.stringify({ email, password, submissionId, reference }, null, 2))
  await shot(p, '06-receipt')
  await p.getByRole('link', { name: 'İşlərimə keç' }).click()
  await p.getByText(`Təklif #${reference}`).waitFor()
  await shot(p, '07-islerim')
  step(`register → phone photo with GPS + PNG → submit → ${reference} → İşlərim`)
  }

  // Originals: the owner can read them, nobody else can; no public copy exists before a published result.
  const ownerToken = await p.evaluate(async ([e, pw]) => {
    const r = await fetch('/api/auth/login', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ email: e, password: pw }) })
    return (await r.json()).accessToken
  }, [email, password])
  const asOwner = { headers: { Authorization: `Bearer ${ownerToken}` } }
  const mine = JSON.parse((await fetchIn(p, `/api/submissions/${submissionId}`, asOwner)).latin)
  const files = mine.files ?? (mine.fields ?? []).flatMap((f) => f.files ?? [])
  check(files.length === 2, `two files on the submission (${files.length}; keys ${Object.keys(mine).join(',')})`)
  for (const f of files) {
    const own = await fetchIn(p, `/api/submissions/${submissionId}/files/${f.id}`, asOwner)
    check(own.status === 200, `owner reads original ${f.id} (${own.status})`)
    const anon = await fetchIn(visitor, `/api/submissions/${submissionId}/files/${f.id}`)
    check(anon.status === 401, `anonymous refused original ${f.id} (${anon.status})`)
    const pub = await fetchIn(visitor, `/api/competitions/${CHEVRON}/gallery/${reference}/files/${f.id}`)
    check(pub.status === 404, `no public copy before a result (${pub.status})`)
  }
  step('originals: owner 200, anonymous 401; public derivative URLs 404 (nothing published)')

  // ---- Admin: finds it, safe copies get made, closes it off ---------------------------------------------
  const admin = await page()
  await admin.goto(base + '/admin')
  await admin.waitForURL(/\/login\?next=/)
  await admin.getByLabel('E-mail').fill(adminEmail)
  await admin.getByLabel('Şifrə', { exact: true }).fill(adminPassword)
  await admin.getByRole('button', { name: 'Daxil ol' }).click()
  await admin.waitForURL(/\/admin$/)
  await shot(admin, '08-admin-dashboard')
  await admin.goto(`${base}/admin/submissions?q=${encodeURIComponent(reference)}`)
  await admin.getByRole('link', { name: reference }).click()
  await admin.waitForURL(/\/admin\/submissions\/\d+$/)
  await admin.getByRole('heading', { name: /^Fayllar/ }).waitFor()
  // The competition's file field is private (not "public after results"), so the panel says so for each file; the
  // safe copies are still made in the background — their state is read from the admin API.
  check((await admin.getByText('Gizli: sahə nəticədən sonra göstərilmir').count()) === 2, 'both files are marked private')
  const adminToken = await admin.evaluate(async ([e, pw]) => {
    const r = await fetch('/api/auth/login', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ email: e, password: pw }) })
    return (await r.json()).accessToken
  }, [adminEmail, adminPassword])
  let detail
  for (let i = 0; i < 40; i++) {
    detail = JSON.parse((await fetchIn(admin, `/api/admin/submissions/${submissionId}`, { headers: { Authorization: `Bearer ${adminToken}` } })).latin)
    if (detail.files.every((f) => f.publicAsset === 'Ready')) break
    await admin.waitForTimeout(1000)
  }
  check(detail.files.length === 2 && detail.files.every((f) => f.publicAsset === 'Ready'), `safe copies: ${detail.files.map((f) => f.publicAsset).join(',')}`)
  check(detail.files.every((f) => f.scanStatus === 'NotScanned'), 'files honestly marked NotScanned (no antivirus attached)')
  await shot(admin, '09-admin-submission')
  if (detail.status !== 'Rejected') {
    await admin.getByLabel('Yeni status').selectOption('Rejected')
    await admin.getByLabel('Şərh (istəyə bağlı)').fill(`Sınaq: istehsal yerləşdirməsinin smoke testi (${run}). Real təklif deyil.`)
    await admin.getByRole('button', { name: 'Statusu dəyiş' }).click()
    await admin.getByText('Status dəyişdi: Rədd edilib.').waitFor() // the admin wording; participants read "Qəbul edilməyib"
    await admin.getByLabel('Yeni qeyd').fill(`Daxili qeyd: deploy smoke testi ${run} — bu hesab və iş sınaqdır.`)
    await admin.getByRole('button', { name: 'Qeydi əlavə et' }).click()
    await admin.getByText(`deploy smoke testi ${run}`).waitFor()
  } else {
    await admin.getByText('Rədd edilib').first().waitFor() // closed off by an earlier run
  }
  await shot(admin, '10-admin-rejected')
  step('admin: found by reference, 2 metadata-free copies ready, status → Rejected with a note (never publishable)')

  await p.goto(base + '/my/submissions')
  await p.getByText(`Təklif #${reference}`).waitFor()
  await p.getByText('Qəbul edilməyib').first().waitFor()
  await shot(p, '11-participant-rejected')
  step('participant sees the rejected status')

  // Results page is still empty publicly.
  await visitor.goto(base + '/results')
  await visitor.getByRole('heading', { name: 'Hələ elan olunmuş nəticə yoxdur' }).waitFor()
  check((await fetchIn(visitor, `/api/competitions/${CHEVRON}/results`)).status === 404, 'no public result')

  if (cspViolations.length) throw new Error(`CSP violations: ${cspViolations.slice(0, 3).join(' | ')}`)
  if (overflow.length) throw new Error(`horizontal overflow: ${overflow.join(', ')}`)
  console.log(`Production smoke passed at ${width}px — test participant ${email}, ${reference} (Rejected).`)
} catch (err) {
  for (const [i, ctx] of contexts.entries())
    for (const pg of ctx.pages()) await pg.screenshot({ path: `${out}/p6-failure-${i}-${width}.png`, fullPage: true }).catch(() => {})
  console.error(`✗ [${width}] ${err.message}`)
  process.exitCode = 1
} finally {
  await browser.close()
}
