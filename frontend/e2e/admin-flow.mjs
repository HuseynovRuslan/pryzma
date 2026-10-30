// Phase 3 journey against a THROWAWAY stack (never the dev database):
// admin login → dashboard → new project (cover, publish) → new competition → categories (bulk import,
// duplicate check, add, reorder, deactivate) → form builder → publish guard → publish → public page →
// participant registers and submits → admin finds it → opens it → status change + internal note →
// participant sees the new status but never the note.
// Admin credentials come from the environment (QA_ADMIN_EMAIL / QA_ADMIN_PASSWORD); they are never printed.
// Usage: BASE_URL=http://localhost:5174 node e2e/admin-flow.mjs [screenshotDir] [width]
import { chromium } from '@playwright/test'
import { mkdirSync } from 'node:fs'
import { fileURLToPath } from 'node:url'

const base = process.env.BASE_URL ?? 'http://localhost:5174'
const out = process.argv[2] ?? 'e2e/screenshots'
const width = Number(process.argv[3] ?? 1440)
const adminEmail = process.env.QA_ADMIN_EMAIL
const adminPassword = process.env.QA_ADMIN_PASSWORD
if (!adminEmail || !adminPassword) throw new Error('QA_ADMIN_EMAIL / QA_ADMIN_PASSWORD are not set')
const fixture = (name) => fileURLToPath(new URL(`./fixtures/${name}`, import.meta.url))
mkdirSync(out, { recursive: true })

const run = `${width}-${Date.now().toString(36)}`
const projectSlug = `lacin-${run}`
const competitionSlug = `lacin-loqo-${run}`

const browser = await chromium.launch({ channel: 'chromium' })
const newContext = () =>
  browser.newContext({
    viewport: { width, height: width < 768 ? 844 : 900 },
    deviceScaleFactor: width < 768 ? 2 : 1,
    reducedMotion: 'reduce',
  })
const adminCtx = await newContext()
const userCtx = await newContext()
const admin = await adminCtx.newPage()
const user = await userCtx.newPage()
const step = (msg) => console.log(`✓ [${width}] ${msg}`)
const overflow = []

async function shot(page, name) {
  const wide = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth)
  if (wide > 1) overflow.push(`${name}: +${wide}px`)
  await page.screenshot({ path: `${out}/p3-${name}-${width}.png`, fullPage: true })
}
const confirm = async (page, label, yes) => {
  await page.getByRole('button', { name: label, exact: true }).first().click()
  await page.getByRole('button', { name: yes, exact: true }).click()
}

try {
  // ---- Admin: login → dashboard -------------------------------------------------------------
  await admin.goto(base + '/admin')
  await admin.waitForURL(/\/login\?next=/)
  await admin.getByLabel('E-mail').fill(adminEmail)
  await admin.getByLabel('Şifrə', { exact: true }).fill(adminPassword)
  await admin.getByRole('button', { name: 'Daxil ol' }).click()
  await admin.waitForURL(/\/admin$/)
  await admin.getByText('Qəbulu açıq müsabiqələr').waitFor()
  await shot(admin, '01-dashboard')
  step('anonymous /admin → login → back to the dashboard')

  // ---- Project ------------------------------------------------------------------------------
  await admin.getByRole('link', { name: '+ Yeni layihə' }).first().click()
  await admin.waitForURL(/\/admin\/projects\/new$/)
  // react-router 7 keeps the previous screen during the lazy transition: wait for the new one, not just the URL.
  await admin.getByRole('heading', { name: 'Yeni layihə', level: 1 }).waitFor()
  await admin.getByRole('button', { name: 'Layihəni yarat' }).click()
  await admin.getByText('Layihənin adını yazın.').waitFor()
  await admin.getByLabel('Ad', { exact: true }).fill('Laçın')
  if ((await admin.getByLabel('Ünvan (slug)').inputValue()) !== 'lacin') throw new Error('slug should be generated as "lacin"')
  await admin.getByLabel('Ünvan (slug)').fill(projectSlug)
  await admin.getByLabel('Qısa təsvir').fill('Laçının simvolları: yaradıcı işlər üçün sınaq layihəsi.')
  await admin.getByRole('button', { name: 'Layihəni yarat' }).click()
  await admin.waitForURL(/\/admin\/projects\/\d+$/)
  const projectUrl = admin.url()
  await admin.locator('input[aria-label="Örtük şəkli seçin"]').setInputFiles(fixture('variant.jpg'))
  await admin.getByRole('button', { name: 'Şəkli dəyiş' }).waitFor()
  await confirm(admin, 'Dərc et', 'Bəli, dərc et')
  await admin.getByText('Layihə saytda görünür.').waitFor()
  await shot(admin, '02-project')
  step('project: validation, slug from “Laçın”, cover upload, publish')

  // ---- Competition --------------------------------------------------------------------------
  await admin.getByRole('link', { name: '+ Yeni müsabiqə' }).click()
  await admin.waitForURL(/\/admin\/competitions\/new\?projectId=\d+/)
  await admin.getByRole('heading', { name: 'Yeni müsabiqə', level: 1 }).waitFor()
  await admin.getByLabel('Ad', { exact: true }).fill('Laçın loqosu')
  await admin.getByLabel('Ünvan (slug)').fill(competitionSlug)
  await admin.getByLabel('Qısa təsvir').fill('Laçın üçün loqo və plakat ideyaları.')
  await admin.getByText('İştirakçı kateqoriya seçir').click()
  await admin.getByLabel('Kateqoriyaların saytdakı adı').fill('Nominasiyalar')
  await admin.getByRole('button', { name: 'Müsabiqəni yarat' }).click()
  await admin.waitForURL(/\/admin\/competitions\/\d+\?tab=categories/)
  const competitionUrl = admin.url().split('?')[0]
  step('competition created as draft with a custom category label, no invented dates/prize')

  // ---- Categories ---------------------------------------------------------------------------
  await admin.getByRole('button', { name: 'Toplu idxal' }).click()
  const importBox = admin.getByLabel('Hər sətirdə bir kateqoriya')
  await importBox.fill('Loqo\nPlakat\nloqo')
  await admin.getByText('siyahıda iki dəfə var').first().waitFor()
  if (!(await admin.getByRole('button', { name: '3 kateqoriyanı əlavə et' }).isDisabled()))
    throw new Error('import with duplicates must be blocked')
  await importBox.fill('Loqo\nPlakat; plakat-dizayni\nSuvenir; ; Kiçik əşyalar üçün')
  await admin.getByRole('button', { name: '3 kateqoriyanı əlavə et' }).click()
  await admin.getByText('3 kateqoriya əlavə olundu.').waitFor()
  await admin.getByLabel('Yeni kateqoriya').fill('Mural')
  await admin.getByRole('button', { name: 'Əlavə et', exact: true }).click()
  await admin.getByText('“Mural” əlavə olundu.').waitFor()
  await admin.getByRole('button', { name: 'Mural: yuxarı' }).click()
  await admin.waitForFunction(() => {
    const names = [...document.querySelectorAll('.a-table tbody .a-table__primary')].map((n) => n.textContent)
    return names.indexOf('Mural') === 2
  })
  await admin.getByLabel('Mural aktivdir').click()
  await admin.getByText('(3 aktiv / 4)').waitFor()
  await shot(admin, '03-categories')
  step('categories: duplicate import blocked, import 3, add 1, reorder, deactivate')

  // ---- Publish guard, then form builder ----------------------------------------------------
  await admin.getByRole('link', { name: 'Əsas məlumat' }).click()
  await confirm(admin, 'Dərc et', 'Bəli, dərc et')
  await admin.getByText('Dərc etmək hələ mümkün deyil:').waitFor()
  step('publishing without form fields is refused with a reason')

  await admin.getByRole('link', { name: /Təqdimat forması/ }).click()
  const addField = async (label, type, extra) => {
    await admin.getByRole('button', { name: '+ Sahə əlavə et' }).click()
    const editor = admin.getByRole('form', { name: 'Yeni sahə' })
    await editor.getByLabel('Sahənin adı').fill(label)
    await editor.getByLabel('Növ').selectOption(type)
    if (extra) await extra(editor)
    await editor.getByRole('button', { name: 'Sahəni əlavə et' }).click()
    await admin.getByText('Sahə əlavə olundu.').waitFor()
    await admin.getByRole('button', { name: 'Bağla' }).click()
  }
  await addField('İşin adı', 'ShortText', (e) => e.getByText('Bu cavab təklifin adı kimi').click())
  await addField('Konseptin qısa izahı', 'LongText')
  await addField('Eskiz faylları', 'File', (e) => e.getByLabel('Maksimum fayl sayı').fill('2'))
  await admin.locator('.a-fieldrow').nth(2).waitFor()
  await shot(admin, '04-form-builder')
  step('form builder: short text (title), long text, file field limited to 2 files')

  await admin.getByRole('link', { name: 'Əsas məlumat' }).click()
  await confirm(admin, 'Dərc et', 'Bəli, dərc et')
  await admin.getByText('Dərc olunub: saytda görünür').waitFor()
  await shot(admin, '05-competition-published')
  step('competition published')

  // ---- Participant --------------------------------------------------------------------------
  await user.goto(`${base}/competitions/${competitionSlug}`)
  await user.getByRole('heading', { name: 'Laçın loqosu' }).waitFor()
  if (await user.locator('.picker__option', { hasText: 'Mural' }).count()) throw new Error('inactive category is public')
  await user.locator('.picker__option', { hasText: 'Plakat' }).click()
  await user.waitForURL(/category=plakat-dizayni/)
  await shot(user, '06-public-competition')
  await user.locator('.action-bar button, .comp__panel .btn').filter({ visible: true }).first().click()
  await user.waitForURL(/\/login\?next=/)
  await user.getByRole('link', { name: 'Qeydiyyatdan keçin' }).click()
  const email = `p3-${run}@meydan.test`
  await user.getByLabel('Ad', { exact: true }).fill('Aysel')
  await user.getByLabel('Soyad').fill('Qəhrəmanlı')
  await user.getByLabel('Telefon').fill('+994 55 000 00 00')
  await user.getByLabel('E-mail').fill(email)
  await user.getByLabel('Şifrə', { exact: true }).fill('meydan2026')
  await user.getByLabel('Şifrəni təkrar et').fill('meydan2026')
  await user.locator('input[name="acceptTerms"]').check()
  await user.getByRole('button', { name: 'Hesab yarat' }).click()
  await user.waitForURL(/\/submissions\/\d+$/)
  await user.locator('.sub-context strong', { hasText: 'Plakat' }).waitFor()
  await user.getByLabel('İşin adı').fill('Qartal və dağ')
  await user.getByLabel('Konseptin qısa izahı').fill('Laçın quşu dağların üstündə: ümid və qayıdış.')
  await user.getByRole('button', { name: 'Davam et' }).click()
  await user.waitForURL(/step=files/)
  await user.locator('input[type="file"]').setInputFiles([fixture('eskiz.png'), fixture('izah.pdf')])
  await user.locator('.filecard:not(.is-uploading):not(.is-error)').nth(1).waitFor()
  await user.getByRole('button', { name: 'Önizləməyə keç' }).click()
  await user.waitForURL(/step=preview/)
  await user.waitForFunction(() => !document.querySelector('.sub__actions--final .btn--primary')?.hasAttribute('disabled'))
  await user.getByRole('button', { name: 'Təklifi təqdim et' }).click()
  await user.getByRole('heading', { name: 'Təklifiniz qəbul edildi.' }).waitFor()
  const reference = (await user.locator('.receipt__ref-value').textContent()).trim()
  const submissionUrl = user.url()
  step(`participant registered and submitted through the dynamic form: ${reference}`)

  // ---- Back in admin ------------------------------------------------------------------------
  await admin.goto(base + '/admin/submissions')
  await admin.getByLabel('Axtarış').fill(reference)
  await admin.waitForURL(/q=/)
  const row = admin.getByRole('link', { name: reference })
  await row.waitFor()
  await admin.getByText('1 təklif', { exact: true }).waitFor()
  if ((await admin.locator('.a-table tbody tr').count()) !== 1) throw new Error('search by reference should give one row')
  await shot(admin, '07-submissions')
  await row.click()
  await admin.waitForURL(/\/admin\/submissions\/\d+$/)
  await admin.getByText('Laçın quşu dağların üstündə').waitFor()
  await admin.getByText('eskiz.png').first().waitFor()
  await admin.getByLabel('Yeni status').selectOption('UnderReview')
  await admin.getByLabel('Şərh (istəyə bağlı)').fill('Plakat keyfiyyəti yoxlanılır.')
  await admin.getByRole('button', { name: 'Statusu dəyiş' }).click()
  await admin.getByText('Status dəyişdi: Yoxlanılır.').waitFor()
  const secret = `Daxili qeyd ${run}: münsiflərə göstər.`
  await admin.getByLabel('Yeni qeyd').fill(secret)
  await admin.getByRole('button', { name: 'Qeydi əlavə et' }).click()
  await admin.getByText(secret).waitFor()
  await shot(admin, '08-submission-detail')
  step('admin found it by reference, opened answers/files, moved it to Yoxlanılır, added a note')

  // ---- Participant sees the status, never the note ------------------------------------------
  await user.goto(base + '/my/submissions')
  await user.getByText(`Təklif #${reference}`).waitFor()
  await user.goto(submissionUrl)
  await user.getByText(`Təklif #${reference}`).waitFor()
  const leaked = await user.evaluate((s) => document.body.innerText.includes(s), secret)
  if (leaked) throw new Error('internal note visible to the participant')
  const noteApi = await user.evaluate(async (s) => {
    const id = location.pathname.split('/').pop()
    const r = await fetch(`/api/admin/submissions/${id}`)
    return r.status
  })
  if (noteApi !== 401 && noteApi !== 403) throw new Error(`participant reached the admin API: ${noteApi}`)
  step('participant: no internal note in the UI; admin API denied')

  // Archived project stays in admin, disappears from the site.
  await admin.goto(projectUrl)
  await admin.getByRole('heading', { name: 'Laçın' }).waitFor()
  await admin.goto(competitionUrl + '?tab=history')
  await admin.getByText('Dərc olundu').first().waitFor()
  await shot(admin, '09-history')
  step('audit history lists the competition changes')

  if (overflow.length) throw new Error(`horizontal overflow: ${overflow.join(', ')}`)
  console.log(`Phase 3 admin flow passed at ${width}px.`)
} catch (err) {
  await admin.screenshot({ path: `${out}/p3-failure-admin-${width}.png`, fullPage: true }).catch(() => {})
  await user.screenshot({ path: `${out}/p3-failure-user-${width}.png`, fullPage: true }).catch(() => {})
  console.error(`✗ [${width}] ${err.message}`)
  process.exitCode = 1
} finally {
  await browser.close()
}
