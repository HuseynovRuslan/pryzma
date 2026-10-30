// Responsive QA of the Phase 2 journey at every agreed width. For each width a fresh participant goes through
// competition → Maştağa → form → files → preview → success → İşlərim; every screen is captured and checked
// for horizontal overflow. Usage: node e2e/qa.mjs [outDir]
import { chromium } from '@playwright/test'
import { mkdirSync } from 'node:fs'
import { fileURLToPath } from 'node:url'

const base = process.env.BASE_URL ?? 'http://localhost:5173'
const out = process.argv[2] ?? 'e2e/screenshots/qa'
const widths = (process.env.WIDTHS ?? '375,390,430,768,1024,1440').split(',').map(Number)
const fixture = (name) => fileURLToPath(new URL(`./fixtures/${name}`, import.meta.url))
mkdirSync(out, { recursive: true })

const browser = await chromium.launch({ channel: 'chromium' })
let problems = 0

async function check(page, label) {
  const o = await page.evaluate(() => {
    const doc = document.documentElement
    const offenders = [...document.querySelectorAll('body *')]
      .filter((el) => el.getBoundingClientRect().right > doc.clientWidth + 1)
      .slice(0, 4)
      .map((el) => `${el.tagName.toLowerCase()}.${[...el.classList].join('.')}`)
    return { scroll: doc.scrollWidth, client: doc.clientWidth, offenders }
  })
  if (o.scroll > o.client) {
    problems++
    console.log(`OVERFLOW ${label}: ${o.scroll} > ${o.client}`, o.offenders)
  }
}

async function capture(page, width, name, { full = true } = {}) {
  await check(page, `${name}@${width}`)
  if (full) await page.evaluate(() => window.scrollTo(0, 0))
  await page.screenshot({ path: `${out}/${name}-${width}.png`, fullPage: full })
}

for (const width of widths) {
  const context = await browser.newContext({
    viewport: { width, height: width < 768 ? 844 : 900 },
    deviceScaleFactor: 1,
    reducedMotion: 'reduce',
  })
  const page = await context.newPage()
  const email = `qa-${width}-${Date.now()}@meydan.test`
  const reg = await page.request.post(`${base}/api/auth/register`, {
    data: {
      firstName: 'QA', lastName: 'Əliyev', phone: '+994500000000', email,
      password: 'meydan2026', confirmPassword: 'meydan2026', acceptTerms: true,
    },
  })
  if (!reg.ok()) throw new Error(`register failed ${reg.status()}`)

  // Competition, Maştağa selected, grid in view.
  await page.goto(`${base}/competitions/baki-kendleri-sevron-dizayni?category=mastaga`, { waitUntil: 'networkidle' })
  await page.locator('#categories').scrollIntoViewIfNeeded()
  await page.evaluate(() => window.scrollBy(0, -80))
  await capture(page, width, '1-competition-selected', { full: false })
  await capture(page, width, '1-competition-full')

  // Draft → step 1.
  await page.goto(`${base}/competitions/baki-kendleri-sevron-dizayni/submit?category=mastaga`)
  await page.waitForURL(/\/submissions\/\d+$/)
  await page.getByLabel('İşin adı').waitFor()
  await capture(page, width, '2-step-details')
  await page.getByLabel('İşin adı').fill('Maştağa: nar və külək — Xəzər sahilində bağlar')
  await page.getByLabel('Konseptin izahı').fill('Şevron qəsəbənin bağlarını, Xəzər küləyini və əkinçilik ənənəsini birləşdirir.\n\nKompozisiya simmetrikdir.')
  await page.getByLabel('İstifadə olunan simvollar və onların mənası').fill('Nar — bolluq.\nDalğa — Xəzər.\nKvadrat — meydan.')
  await page.getByRole('button', { name: 'Davam et' }).click()

  // Step 2: three files, one of each kind.
  await page.waitForURL(/step=files/)
  await page.locator('input[type="file"]').setInputFiles([fixture('eskiz.png'), fixture('variant.jpg'), fixture('izah.pdf')])
  await page.locator('.filecard:not(.is-uploading):not(.is-error)').nth(2).waitFor()
  await page.locator('.filecard img.thumb').nth(1).waitFor()
  await capture(page, width, '3-step-files')

  // Step 3: preview.
  await page.getByRole('button', { name: 'Önizləməyə keç' }).click()
  await page.waitForURL(/step=preview/)
  await page.waitForFunction(() => !document.querySelector('.sub__actions--final .btn--primary')?.hasAttribute('disabled'))
  await page.locator('.summary__files img.thumb').nth(1).waitFor()
  await capture(page, width, '4-step-preview')

  // Submit → success.
  await page.getByRole('button', { name: 'Təklifi təqdim et' }).click()
  await page.locator('.receipt__ref-value').waitFor()
  await capture(page, width, '5-success', { full: false })
  await capture(page, width, '5-success-full')

  // İşlərim.
  await page.goto(`${base}/my/submissions`, { waitUntil: 'networkidle' })
  await page.locator('.subs__row').first().waitFor()
  await capture(page, width, '6-my-submissions')

  console.log(`✓ ${width}px`)
  await context.close()
}

await browser.close()
console.log(problems ? `${problems} overflow problem(s)` : 'No horizontal overflow on any Phase 2 screen.')
process.exit(problems ? 1 : 0)
