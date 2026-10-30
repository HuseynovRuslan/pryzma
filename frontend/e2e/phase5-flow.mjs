// Phase 5 journey on the PRODUCTION BUILD (vite preview → real CSP/security headers) against a throwaway API whose
// fresh QA database was just seeded, with e-mail in "Pickup" mode (EML_DIR). Covers:
// public home → project → competition → register → upload a phone photo WITH GPS/EXIF + PNG + PDF → submit → receipt
// e-mail → İşlərim; admin → public files → safe copies ready → choose preview → keyboard-only winner dialog →
// close → publish; anonymous → results/gallery/work get metadata-free WEBP only, originals refused; forgot/reset
// password through the real e-mail, old session ended; 404 / network-error / empty states; no CSP violations.
// Usage: BASE_URL=http://localhost:4174 EML_DIR=… QA_ADMIN_EMAIL=… QA_ADMIN_PASSWORD=… node e2e/phase5-flow.mjs <outDir> <width>
import { chromium } from '@playwright/test'
import { mkdirSync, readdirSync, readFileSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'

const base = process.env.BASE_URL ?? 'http://localhost:4174'
const emlDir = process.env.EML_DIR
const out = process.argv[2] ?? 'e2e/screenshots'
const width = Number(process.argv[3] ?? 1440)
const adminEmail = process.env.QA_ADMIN_EMAIL
const adminPassword = process.env.QA_ADMIN_PASSWORD
if (!adminEmail || !adminPassword || !emlDir) throw new Error('QA_ADMIN_EMAIL / QA_ADMIN_PASSWORD / EML_DIR are not set')
const fixture = (name) => fileURLToPath(new URL(`./fixtures/${name}`, import.meta.url))
mkdirSync(out, { recursive: true })

const CHEVRON = 'baki-kendleri-sevron-dizayni'
const run = Date.now().toString(36)
const SECRET_AUTHOR = 'Gizli Muellif Aysel'
const SECRET_CAMERA = 'SecretPhone X9'

// ---- a real phone-style JPEG with GPS / camera / author EXIF (orientation 6) -----------------------------
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
  // pad IFD0 to the 90 bytes the layout assumes (6 entries instead of 7)
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

// ---- e-mail pickup ----------------------------------------------------------------------------------------
function qpDecode(text) {
  return text.replace(/=\r?\n/g, '').replace(/=([0-9A-F]{2})/gi, (_, h) => String.fromCharCode(parseInt(h, 16)))
}
async function waitForMail(to, subjectPart) {
  for (let i = 0; i < 100; i++) {
    for (const f of readdirSync(emlDir).filter((n) => n.endsWith('.eml'))) {
      const raw = readFileSync(join(emlDir, f), 'latin1')
      if (!raw.includes(to)) continue
      const decoded = Buffer.from(qpDecode(raw), 'latin1').toString('utf8')
      if (decoded.includes(subjectPart) || raw.includes(subjectPart)) return decoded
    }
    await new Promise((r) => setTimeout(r, 200))
  }
  throw new Error(`no e-mail "${subjectPart}" to ${to}`)
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
const overflow = []
async function shot(p, name) {
  const wide = await p.evaluate(() => document.documentElement.scrollWidth - window.innerWidth)
  if (wide > 1) overflow.push(`${name}: +${wide}px`)
  await p.screenshot({ path: `${out}/p5-${name}-${width}.png`, fullPage: true })
}
const check = (cond, msg) => {
  if (!cond) throw new Error(msg)
}
const fetchIn = (p, url) =>
  p.evaluate(async (u) => {
    const r = await fetch(u)
    const buf = new Uint8Array(await r.arrayBuffer())
    let latin = ''
    for (const b of buf) latin += String.fromCharCode(b)
    return { status: r.status, type: r.headers.get('content-type'), latin, size: buf.length }
  }, url)

try {
  // ---- Empty & error states on a fresh site ----------------------------------------------------------
  const visitor = await page()
  const home = await visitor.goto(base + '/')
  check((home.headers()['content-security-policy'] ?? '').includes("script-src 'self'"), 'SPA must be served with the CSP')
  await visitor.goto(base + '/results')
  await visitor.getByRole('heading', { name: 'Hələ elan olunmuş nəticə yoxdur' }).waitFor()
  await shot(visitor, '01-results-empty')
  await visitor.goto(base + '/bu-sehife-yoxdur')
  await visitor.getByRole('heading', { name: 'Səhifə tapılmadı' }).waitFor()
  await shot(visitor, '02-not-found')
  await visitor.route('**/api/projects', (r) => r.abort())
  await visitor.goto(base + '/projects')
  await visitor.getByRole('button', { name: 'Yenidən yüklə' }).waitFor()
  await shot(visitor, '03-network-error')
  await visitor.unroute('**/api/projects')
  await visitor.getByRole('button', { name: 'Yenidən yüklə' }).click()
  await visitor.getByRole('heading', { name: 'Layihələr' }).first().waitFor()
  await visitor.goto(base + '/privacy')
  await visitor.getByRole('heading', { name: 'Məxfilik', level: 1 }).waitFor()
  await shot(visitor, '04-privacy')
  step('empty results, 404, recoverable network error, privacy page')

  // ---- Participant: home → project → competition → register → submit ------------------------------
  const p = await page()
  await p.goto(base + '/')
  await p.locator('main a[href="/projects/baki-kendleri"]').first().click()
  await p.waitForURL(/\/projects\/baki-kendleri$/)
  await p.locator(`main a[href="/competitions/${CHEVRON}"]`).first().click()
  await p.waitForURL(new RegExp(`/competitions/${CHEVRON}`))
  await p.getByRole('heading', { level: 1, name: /şevron/i }).waitFor()
  await p.getByRole('searchbox').fill('mastaga')
  await p.locator('.picker__option', { hasText: 'Maştağa' }).click()
  await p.waitForURL(/category=mastaga/)
  await p.locator('.action-bar button, .comp__panel .btn').filter({ visible: true }).first().click()
  await p.waitForURL(/\/login\?next=/)
  await p.getByRole('link', { name: 'Qeydiyyatdan keçin' }).click()
  const email = `p5-${run}-${width}@meydan.test`
  await p.getByLabel('Ad', { exact: true }).fill('Leyla')
  await p.getByLabel('Soyad').fill('Əsgərova')
  await p.getByLabel('Telefon').fill('+994 70 123 45 67')
  await p.getByLabel('E-mail').fill(email)
  await p.getByLabel('Şifrə', { exact: true }).fill('meydan2026')
  await p.getByLabel('Şifrəni təkrar et').fill('meydan2026')
  await p.locator('input[name="acceptTerms"]').check()
  await p.getByRole('button', { name: 'Hesab yarat' }).click()
  await p.waitForURL(/\/submissions\/\d+$/)
  const submissionId = p.url().split('/').pop()
  await p.getByLabel('İşin adı').fill('Maştağa: küləyin izi')
  await p.getByLabel('Konseptin izahı').fill('Xəzər küləyi və bağlar.')
  await p.getByLabel('İstifadə olunan simvollar və onların mənası').fill('Dalğa — Xəzər.')
  await p.getByRole('button', { name: 'Davam et' }).click()
  await p.waitForURL(/step=files/)
  await p.locator('input[type="file"]').setInputFiles([phonePhoto, fixture('eskiz.png'), fixture('izah.pdf')])
  await p.locator('.filecard:not(.is-uploading):not(.is-error)').nth(2).waitFor()
  await p.getByRole('button', { name: 'Önizləməyə keç' }).click()
  await p.waitForURL(/step=preview/)
  await p.waitForFunction(() => !document.querySelector('.sub__actions--final .btn--primary')?.hasAttribute('disabled'))
  await p.getByRole('button', { name: 'Təklifi təqdim et' }).click()
  await p.getByRole('heading', { name: 'Təklifiniz qəbul edildi.' }).waitFor()
  const reference = (await p.locator('.receipt__ref-value').textContent()).trim()
  const receipt = await waitForMail(email, reference)
  check(receipt.includes('Təklifiniz qəbul edildi') && receipt.includes('Maştağa'), 'receipt e-mail content')
  await p.getByRole('link', { name: 'İşlərimə keç' }).click()
  await p.getByText(`Təklif #${reference}`).waitFor()
  await shot(p, '05-islerim')
  step(`home → project → competition → register → phone photo with GPS + PNG + PDF → ${reference}; receipt e-mail arrived`)

  // ---- Admin: public files, safe copies, preview, keyboard dialog, close, publish ---------------------
  const admin = await page()
  await admin.goto(base + '/admin')
  await admin.waitForURL(/\/login\?next=/)
  await admin.getByLabel('E-mail').fill(adminEmail)
  await admin.getByLabel('Şifrə', { exact: true }).fill(adminPassword)
  await admin.getByRole('button', { name: 'Daxil ol' }).click()
  await admin.waitForURL(/\/admin$/)
  const token = await admin.evaluate(async ([e, pw]) => {
    const r = await fetch('/api/auth/login', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ email: e, password: pw }) })
    return (await r.json()).accessToken
  }, [adminEmail, adminPassword])
  const comps = await admin.evaluate(async (t) => (await fetch('/api/admin/competitions', { headers: { Authorization: `Bearer ${t}` } })).json(), token)
  const chevronId = comps.find((c) => c.slug === CHEVRON).id

  await admin.goto(`${base}/admin/competitions/${chevronId}?tab=form`)
  await admin.locator('.a-fieldrow', { hasText: 'Fayllar' }).getByRole('button', { name: 'Redaktə' }).click()
  await admin.getByLabel('Nəticələr dərc olunandan sonra qalereyada göstər').check()
  await admin.getByRole('button', { name: 'Yadda saxla' }).click()
  await admin.getByText('Sahə yeniləndi.').waitFor()

  await admin.goto(`${base}/admin/submissions?q=${encodeURIComponent(reference)}`)
  await admin.getByRole('link', { name: reference }).click()
  await admin.waitForURL(/\/admin\/submissions\/\d+$/)
  const filesPanel = () => admin.getByRole('heading', { name: /^Fayllar/ }).waitFor()
  await filesPanel()
  for (let i = 0; i < 30 && (await admin.getByText('Təhlükəsiz nüsxə hazırdır').count()) < 3; i++) {
    await admin.waitForTimeout(700)
    await admin.reload()
    await filesPanel()
  }
  check((await admin.getByText('Təhlükəsiz nüsxə hazırdır').count()) === 3, 'three safe copies (photo, PNG, PDF page)')
  // Upload order is not fixed (parallel uploads), so choose whichever image is not the automatic preview yet.
  const choice = admin.locator('.a-file').filter({ has: admin.getByRole('button', { name: 'Önizləmə et' }) })
    .filter({ hasNot: admin.locator('.thumb--doc') }).first()
  const chosenName = (await choice.locator('.a-file__name').textContent()).trim()
  await choice.getByRole('button', { name: 'Önizləmə et' }).click()
  await admin.locator('.a-file', { hasText: chosenName }).locator('.a-file__flag').waitFor()
  const tnum = await admin.locator('.a-head__meta .tabular').first().evaluate((el) => getComputedStyle(el).fontVariantNumeric)
  check(!tnum.includes('tabular-nums'), `dates must use proportional figures (got ${tnum})`)
  await shot(admin, '06-admin-files-preview')
  step(`admin: files made public, 3 metadata-free copies ready, ${chosenName} chosen as gallery preview; dates without "2026 , 19 : 13"`)

  // Keyboard only: open the winner dialog, focus is trapped, Escape closes and returns focus.
  const selectBtn = admin.getByRole('button', { name: /üzrə qalib seç$/ })
  await selectBtn.focus()
  await admin.keyboard.press('Enter')
  const dialog = admin.getByRole('dialog', { name: /üzrə qalibi seçmək istəyirsiniz\?$/ })
  await dialog.waitFor()
  check(await admin.evaluate(() => document.activeElement?.textContent?.trim() === 'Ləğv et'), 'focus starts on "Ləğv et"')
  for (let i = 0; i < 4; i++) {
    await admin.keyboard.press('Tab')
    check(await admin.evaluate(() => !!document.activeElement?.closest('[role="dialog"]')), 'focus stays inside the dialog')
  }
  await admin.keyboard.press('Escape')
  await dialog.waitFor({ state: 'detached' })
  check(await admin.evaluate(() => !!document.activeElement?.textContent?.trim().endsWith('üzrə qalib seç')), 'focus returns to the opener')
  await admin.keyboard.press('Enter')
  await dialog.waitFor()
  await shot(admin, '07-winner-dialog')
  await dialog.getByRole('button', { name: 'Bəli, qalib seç' }).click()
  await admin.getByText('Qalib seçildi. Nəticə hələ dərc olunmayıb.').waitFor()
  step('keyboard: Enter opens the dialog, focus starts on cancel and is trapped, Escape closes and restores focus; winner selected')

  await admin.goto(`${base}/admin/competitions/${chevronId}`)
  await admin.getByRole('button', { name: 'Qəbulu bağla', exact: true }).click()
  await admin.getByRole('button', { name: 'Bəli, bağla', exact: true }).click()
  await admin.getByText('Bağlanıb: saytda görünür, təklif qəbul etmir.').waitFor()
  await admin.goto(`${base}/admin/competitions/${chevronId}?tab=results`)
  await admin.getByRole('button', { name: 'Nəticəni dərc et' }).click()
  await admin.getByRole('button', { name: 'Bəli, dərc et' }).click()
  await admin.locator('.a-badge').getByText('Dərc olunub', { exact: true }).waitFor()
  await shot(admin, '08-results-published')
  step('admin: acceptance closed, result published')

  // ---- Anonymous: only metadata-free derivatives, never the originals -----------------------------------
  const anon = await page()
  await anon.goto(`${base}/competitions/${CHEVRON}/results`)
  const hero = anon.locator('.wcard img')
  await hero.waitFor()
  await anon.waitForFunction(() => {
    const img = document.querySelector('.wcard img')
    return img && img.complete && img.naturalWidth > 0
  })
  await shot(anon, '09-results')
  await anon.goto(`${base}/competitions/${CHEVRON}/gallery/${reference}`)
  await anon.locator('.work__hero img, .work__more img').nth(2).waitFor()
  await anon.getByText('Sənədin birinci səhifəsi (önizləmə)', { exact: false }).waitFor()
  await shot(anon, '10-work')
  const urls = await anon.locator('.work__hero img, .work__more img').evaluateAll((imgs) => imgs.map((i) => i.getAttribute('src')))
  check(urls.length === 3, 'photo, PNG and PDF page preview')
  for (const url of urls) {
    const r = await fetchIn(anon, url)
    check(r.status === 200 && r.type === 'image/webp', `derivative ${url} → ${r.status} ${r.type}`)
    for (const secret of ['Exif', 'GPS', SECRET_CAMERA, SECRET_AUTHOR, 'SecretMaker', '%PDF', 'Leyla', 'Əsgərova'])
      check(!r.latin.includes(secret), `public image leaks ${secret}`)
  }
  const heroSrc = await anon.evaluate(() => document.querySelector('.work__hero img')?.getAttribute('src'))
  const detail = await admin.evaluate(async ([t, id]) => (await fetch(`/api/admin/submissions/${id}`, { headers: { Authorization: `Bearer ${t}` } })).json(), [token, submissionId])
  const png = detail.files.find((f) => f.originalFileName === chosenName)
  const gallery = await anon.evaluate(async (s) => (await fetch(`/api/competitions/${s}/gallery`)).json(), CHEVRON)
  check(gallery.items.find((i) => i.reference === reference).preview.url.endsWith(`/files/${png.id}`), 'the admin-chosen preview is the gallery image')
  for (const f of detail.files) {
    const orig = await fetchIn(anon, `/api/submissions/${submissionId}/files/${f.id}`)
    check(orig.status === 401, `original ${f.originalFileName} must not be public (${orig.status})`)
  }
  const pageText = await anon.evaluate(() => document.body.innerText)
  // The winner's name is public once results are (a business decision); contact details and file names never are.
  check(pageText.includes('Leyla Əsgərova'), "the winner's name is shown on the winning work")
  for (const secret of [email, '+994', 'telefon', '.jpg', 'izah.pdf'])
    check(!pageText.includes(secret), `public page shows ${secret}`)
  void heroSrc
  step('anonymous: results/work serve only WEBP copies without EXIF/GPS/camera/author; originals 401; chosen preview used')

  // ---- Forgot / reset password through the real e-mail; the old session ends --------------------------
  const forgot = await page()
  await forgot.goto(base + '/login')
  await forgot.getByRole('link', { name: 'Şifrəni unutmuşam' }).click()
  // The login form (with its own "E-mail" field) stays up during the route transition: wait for the new page.
  await forgot.getByRole('heading', { name: 'Şifrəni bərpa et' }).waitFor()
  await forgot.getByLabel('E-mail').fill(email)
  await forgot.getByRole('button', { name: 'Link göndər' }).click()
  await forgot.getByRole('heading', { name: 'E-maili yoxlayın' }).waitFor()
  await shot(forgot, '11-forgot-sent')
  const resetMail = await waitForMail(email, 'reset-password#')
  const link = resetMail.match(/https?:\/\/[^\s"<>]+\/reset-password#email=[^&\s"<>]+&token=[A-Za-z0-9_-]+/)?.[0]
  check(link && link.startsWith(base), `reset link on the configured site (${link?.slice(0, 40)})`)
  check(!link.includes('?'), 'token in the fragment, not the query string')
  await forgot.goto(link)
  await forgot.getByRole('heading', { name: 'Yeni şifrə' }).waitFor()
  await shot(forgot, '12-reset-form')
  await forgot.getByLabel('Yeni şifrə', { exact: true }).fill('yenisifre2026')
  await forgot.getByLabel('Şifrəni təkrar et').fill('yenisifre2026')
  await forgot.getByRole('button', { name: 'Şifrəni yenilə' }).click()
  await forgot.getByRole('heading', { name: 'Şifrə yeniləndi' }).waitFor()
  await shot(forgot, '13-reset-done')

  await p.goto(base + '/my/submissions') // the participant's original session (refresh cookie from before the reset)
  await p.waitForURL(/\/login\?next=/)
  await forgot.locator('#main').getByRole('link', { name: 'Daxil ol' }).click()
  await forgot.getByLabel('E-mail').fill(email)
  await forgot.getByLabel('Şifrə', { exact: true }).fill('yenisifre2026')
  await forgot.getByRole('button', { name: 'Daxil ol' }).click()
  await forgot.waitForURL((u) => !u.pathname.startsWith('/login'))
  step('forgot → e-mail → fragment link → new password → login; the session opened before the reset was ended')

  await admin.goto(`${base}/admin/submissions?q=heç-nə-yoxdur-${run}`)
  await admin.getByText('Bu filtrlərə uyğun təklif yoxdur.').waitFor()
  await shot(admin, '14-admin-empty-filter')

  if (cspViolations.length) throw new Error(`CSP violations: ${cspViolations.slice(0, 3).join(' | ')}`)
  if (overflow.length) throw new Error(`horizontal overflow: ${overflow.join(', ')}`)
  console.log(`Phase 5 flow passed at ${width}px.`)
} catch (err) {
  for (const [i, ctx] of contexts.entries())
    for (const pg of ctx.pages()) await pg.screenshot({ path: `${out}/p5-failure-${i}-${width}.png`, fullPage: true }).catch(() => {})
  console.error(`✗ [${width}] ${err.message}`)
  process.exitCode = 1
} finally {
  await browser.close()
}
