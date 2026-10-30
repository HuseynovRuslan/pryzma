// End-to-end check of the Phase 2 journey against a running dev stack (API :5080 + Vite :5173):
// new user → competition → search & pick Maştağa → Təklif ver → register → back to Maştağa's draft →
// form → files (incl. a rejected SVG) → preview → submit → reference → İşlərim → reload.
// Usage: node e2e/flow.mjs [screenshotDir] [width]
import { chromium } from '@playwright/test'
import { mkdirSync } from 'node:fs'
import { fileURLToPath } from 'node:url'

const base = process.env.BASE_URL ?? 'http://localhost:5173'
const out = process.argv[2] ?? 'e2e/screenshots'
const width = Number(process.argv[3] ?? 390)
const fixture = (name) => fileURLToPath(new URL(`./fixtures/${name}`, import.meta.url))
mkdirSync(out, { recursive: true })

const browser = await chromium.launch({ channel: 'chromium' })
const context = await browser.newContext({
  viewport: { width, height: width < 768 ? 844 : 900 },
  deviceScaleFactor: width < 768 ? 2 : 1,
  reducedMotion: 'reduce',
})
const page = await context.newPage()
const step = (msg) => console.log(`✓ ${msg}`)
const shot = (name) => page.screenshot({ path: `${out}/p2-${name}-${width}.png`, fullPage: true })
const cta = () => page.locator('.action-bar button, .comp__panel .btn').filter({ visible: true }).first()

try {
  await page.goto(base + '/competitions/baki-kendleri-sevron-dizayni')
  await page.getByText('Cəmi 59').waitFor()
  step('competition shows 59 settlements')

  await cta().click()
  await page.getByText(/əvvəlcə qəsəbələr siyahısından birini seçin/).waitFor()
  step('CTA without a settlement explains instead of navigating')

  const search = page.getByRole('searchbox')
  await search.fill('mastaga')
  if ((await page.locator('.picker__option').count()) !== 1) throw new Error('search "mastaga" should leave one result')
  await search.fill('pirshagi')
  await page.locator('.picker__option', { hasText: 'Pirşağı' }).waitFor()
  await search.fill('emircan')
  await page.locator('.picker__option', { hasText: 'Əmircan' }).waitFor()
  await search.fill('mastaga')
  await page.locator('.picker__option', { hasText: 'Maştağa' }).click()
  await page.waitForURL(/category=mastaga/)
  await page.locator('.action-bar__value, .panel__choice-value').filter({ hasText: 'Maştağa', visible: true }).first().waitFor()
  await shot('1-category-selected')
  step('diacritic search (mastaga, pirshagi, emircan) and selection of Maştağa')

  await cta().click()
  await page.waitForURL(/\/login\?next=/)
  await page.getByText('Seçiminiz saxlanılıb').waitFor()
  await page.getByRole('link', { name: 'Qeydiyyatdan keçin' }).click()
  await page.waitForURL(/\/register\?next=/)
  const email = `p2-${Date.now()}@meydan.test`
  await page.getByLabel('Ad', { exact: true }).fill('Nigar')
  await page.getByLabel('Soyad').fill('Əliyeva')
  await page.getByLabel('Telefon').fill('+994 50 000 00 00')
  await page.getByLabel('E-mail').fill(email)
  await page.getByLabel('Şifrə', { exact: true }).fill('meydan2026')
  await page.getByLabel('Şifrəni təkrar et').fill('meydan2026')
  await page.locator('input[name="acceptTerms"]').check()
  await page.getByRole('button', { name: 'Hesab yarat' }).click()
  await page.waitForURL(/\/submissions\/\d+$/)
  await page.locator('.sub-context strong', { hasText: 'Maştağa' }).waitFor()
  const submissionUrl = page.url()
  step('register → straight into the Maştağa draft')

  // Step 1: required-field validation, then fill in.
  await page.getByRole('button', { name: 'Davam et' }).click()
  await page.getByText('“İşin adı” sahəsini doldurun.').waitFor()
  await shot('2-details-errors')
  await page.getByLabel('İşin adı').fill('Maştağa: nar və külək')
  await page.getByLabel('Konseptin izahı').fill('Şevron qəsəbənin bağlarını və Xəzər küləyini birləşdirir.\n\nForma sadə, oxunaqlıdır.')
  await page.getByLabel('İstifadə olunan simvollar və onların mənası').fill('Nar — bolluq.\nDalğa — Xəzər.')
  await page.getByText(/Qaralama saxlanıldı/).waitFor({ timeout: 10_000 })
  step('details validated and autosaved')

  await page.getByRole('button', { name: 'Davam et' }).click()
  await page.waitForURL(/step=files/)
  const input = page.locator('input[type="file"]')
  await input.setInputFiles(fixture('zererli.svg'))
  await page.getByText(/Bu format qəbul olunmur/).waitFor()
  await page.getByRole('button', { name: 'Bağla' }).click()
  await input.setInputFiles([fixture('eskiz.png'), fixture('izah.pdf')])
  await page.locator('.filecard:not(.is-uploading):not(.is-error)').nth(1).waitFor()
  await page.locator('.filecard img.thumb').first().waitFor()
  await shot('3-files')
  step('SVG rejected with a reason; PNG (thumbnail) and PDF uploaded')

  // Leave and come back: the draft (answers + files) survives a reload.
  await page.reload()
  await page.locator('.filecard').nth(1).waitFor()
  step('draft survives reload on the files step')

  await page.getByRole('button', { name: 'Önizləməyə keç' }).click()
  await page.waitForURL(/step=preview/)
  await page.locator('.summary__category', { hasText: 'Maştağa' }).waitFor()
  await page.getByText('Maştağa: nar və külək').waitFor()
  await page.getByRole('button', { name: 'Təklifi təqdim et' }).waitFor({ state: 'visible' })
  await page.waitForFunction(() => !document.querySelector('.sub__actions--final .btn--primary')?.hasAttribute('disabled'))
  await shot('4-preview')
  step('preview shows project, competition, settlement, answers and files')

  await page.getByRole('button', { name: 'Təklifi təqdim et' }).click()
  await page.getByRole('heading', { name: 'Təklifiniz qəbul edildi.' }).waitFor()
  const reference = (await page.locator('.receipt__ref-value').textContent()).trim()
  if (!/^SUB-\d{4}-\d{5}$/.test(reference)) throw new Error(`unexpected reference ${reference}`)
  await shot('5-success')
  step(`submitted, reference ${reference}`)

  await page.getByRole('link', { name: 'İşlərimə keç' }).click()
  await page.waitForURL('**/my/submissions')
  await page.getByText(`Təklif #${reference}`).waitFor()
  await page.reload()
  await page.getByText(`Təklif #${reference}`).waitFor()
  await page.locator('.subs__status', { hasText: 'Göndərilib' }).waitFor()
  await shot('6-my-submissions')
  step('İşlərim lists it after a reload with status Göndərilib')

  // The submitted work is read-only now.
  await page.goto(submissionUrl)
  await page.getByText(`Təklif #${reference}`).waitFor()
  if (await page.getByRole('button', { name: 'Davam et' }).count()) throw new Error('submitted work must not be editable')
  step('submitted work opens read-only')

  console.log('Phase 2 flow passed.')
} catch (err) {
  await page.screenshot({ path: `${out}/p2-failure-${width}.png`, fullPage: true })
  console.error(err.message)
  process.exitCode = 1
} finally {
  await browser.close()
}
