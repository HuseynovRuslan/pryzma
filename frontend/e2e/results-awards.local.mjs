// The two-choice results page (D18), local QA only.
// A — the real stack on the QA database: both podiums recorded through the admin API, the result private until it is
//     published, public after, private again after unpublishing.
// B — a preview with the announced names and numbers over the works' public pictures, served from a fixture by
//     route interception (PREVIEW_FIXTURE): nothing is published anywhere. Desktop, 390 px and 360 px pictures.
// Expects the API on :5080 (QA database) and Vite on :5173; admin credentials from QA_ADMIN_EMAIL / QA_ADMIN_PASSWORD.
import { chromium } from '@playwright/test'
import { mkdirSync, readFileSync } from 'node:fs'

const base = process.env.BASE_URL ?? 'http://localhost:5173'
const out = process.env.QA_OUT ?? '../design-review/results-two-choices'
const id = Number(process.env.QA_COMPETITION ?? 3)
const slug = process.env.QA_SLUG ?? 'emircanin-simvolu'
if (!/localhost|127\.0\.0\.1/.test(base)) throw new Error('local only')
mkdirSync(out, { recursive: true })

const browser = await chromium.launch({ channel: 'chrome' }).catch(() => chromium.launch())
const viewports = {
  desktop: { viewport: { width: 1440, height: 900 } },
  w390: { viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true },
  w360: { viewport: { width: 360, height: 780 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true },
}

/** Scrolls through the page so every lazy picture loads, then back to the top. */
async function loadAll(page) {
  await page.evaluate(async () => {
    for (let y = 0; y < document.body.scrollHeight; y += 500) {
      window.scrollTo(0, y)
      await new Promise((r) => setTimeout(r, 120))
    }
    window.scrollTo(0, 0)
  })
  await page.waitForTimeout(800)
}

/** What a page shows about itself: overflow, pictures, headings, words that must never appear. */
async function check(page, label) {
  await loadAll(page)
  const r = await page.evaluate(() => {
    const root = document.querySelector('#root')
    const text = root?.innerText ?? ''
    const imgs = [...document.querySelectorAll('.aw-place__visual img, .work__hero img')]
    return {
      overflow: document.documentElement.scrollWidth > document.documentElement.clientWidth,
      images: `${imgs.filter((i) => i.complete && i.naturalWidth > 0).length}/${imgs.length}`,
      h1: [...document.querySelectorAll('h1')].map((h) => h.textContent),
      h2: [...document.querySelectorAll('h2')].map((h) => h.textContent),
      h3: document.querySelectorAll('h3').length,
      forbidden: (text.match(/AZN|mükafat|Ümumi qalib|yekun xal|50\/50/gi) ?? []),
      cut: [...document.querySelectorAll('.aw-place__title, .aw-place__name, .aw-head__title')].filter((e) => e.scrollWidth > e.clientWidth + 1).length,
    }
  })
  console.log(`${label}: ${JSON.stringify(r)}`)
  if (r.overflow || r.forbidden.length > 0 || r.cut > 0) throw new Error(`${label}: layout or wording problem`)
}

// ---- A: the real stack ----------------------------------------------------------------------------------------
if (process.env.QA_ONLY !== 'preview') {
  const ctx = await browser.newContext(viewports.desktop)
  const page = await ctx.newPage()
  await page.goto(base + '/')
  const login = await page.request.post(base + '/api/auth/login', {
    data: { email: process.env.QA_ADMIN_EMAIL, password: process.env.QA_ADMIN_PASSWORD, rememberMe: false },
  })
  if (!login.ok()) throw new Error(`admin login ${login.status()}`)
  const token = (await login.json()).accessToken
  const admin = async (method, path, data) => {
    const res = await page.request.fetch(`${base}/api/admin/competitions/${id}/result${path}`, {
      method, data, headers: { Authorization: `Bearer ${token}` },
    })
    return { status: res.status(), body: res.headers()['content-type']?.includes('json') ? await res.json() : await res.text() }
  }
  const publicStatus = async () => (await page.request.get(`${base}/api/competitions/${slug}/results`)).status()

  let a = (await admin('GET', '/awards')).body
  if (a.resultPublished) await admin('POST', '/unpublish')
  console.log(`before: results ${await publicStatus()} (404 = private)`)
  const current = (p) => [1, 2, 3].map((n) => p.places.find((x) => x.place === n)?.submissionId ?? null)

  // The jury's podium from its (here: made-up QA) protocol, on three QA works with their account names.
  const c = a.candidates
  const jury = [[c[5], 30], [c[0], 26], [c[12], 16]].map(([w, points]) => ({ submissionId: w.submissionId, participantName: w.participantName, points }))
  let r = await admin('PUT', '/awards/jury', { places: jury, current: current(a.jury), source: 'Kağız protokol (QA)', confirmPublished: false })
  console.log(`jury podium: ${r.status}`)
  if (r.status !== 200) throw new Error(JSON.stringify(r.body))
  a = r.body

  // The public's podium: the valid vote's own top three — refused if the vote left them level.
  const top = c.slice(0, 3).map((w) => ({ submissionId: w.submissionId, participantName: w.participantName, points: w.voteCount }))
  r = await admin('PUT', '/awards/public', { places: top, current: current(a.public), source: null, confirmPublished: false })
  console.log(`public podium: ${r.status} ${r.status === 200 ? '' : JSON.stringify(r.body.errors ?? r.body)}`)
  if (r.status === 200) a = r.body

  const state = (await admin('GET', '')).body
  console.log(`result state ${state.state}; problems ${JSON.stringify(state.publishProblems)}; still private: ${await publicStatus()}`)
  await page.goto(`${base}/admin/competitions/${id}?tab=results`, { waitUntil: 'networkidle' })
  await page.getByRole('heading', { name: 'İki kateqoriyalı nəticə' }).scrollIntoViewIfNeeded()
  await page.waitForTimeout(400)
  await page.getByRole('heading', { name: 'İki kateqoriyalı nəticə' }).locator('xpath=ancestor::section[1]').screenshot({ path: `${out}/qa-admin-podiums.png` })

  r = await admin('POST', '/publish')
  console.log(`publish (local QA database): ${r.status}; public results now ${await publicStatus()}`)
  for (const [name, options] of Object.entries(viewports)) {
    const v = await browser.newContext({ ...options, reducedMotion: 'reduce' })
    const p = await v.newPage()
    await p.goto(`${base}/competitions/${slug}/results`, { waitUntil: 'networkidle' })
    await p.waitForTimeout(800)
    await check(p, `qa results ${name}`)
    await p.screenshot({ path: `${out}/qa-results-${name}.png`, fullPage: true })
    await v.close()
  }
  r = await admin('POST', '/unpublish')
  console.log(`unpublished: ${r.status}; public results ${await publicStatus()}`)
  await ctx.close()
}

// ---- B: the announcement over the works' public pictures (fixture) ---------------------------------------------
if (process.env.PREVIEW_FIXTURE) {
  const fx = JSON.parse(readFileSync(process.env.PREVIEW_FIXTURE, 'utf8'))
  const serve = async (ctx) => {
    // The works' public pictures (read-only GETs), handed to the page as if served here: the live site's
    // same-origin resource policy would keep them off a page served from localhost.
    await ctx.route('https://pryzma.az/api/**', async (route) => {
      const res = await route.fetch()
      await route.fulfill({ status: res.status(), body: await res.body(), contentType: res.headers()['content-type'] })
    })
    await ctx.route(`**/api/competitions/${slug}/results`, (route) => route.fulfill({ json: fx.result }))
    await ctx.route(`**/api/competitions/${slug}/gallery/SUB-2026-00061`, (route) => route.fulfill({ json: fx.work061 }))
    await ctx.route(`**/api/competitions/${slug}/gallery?*`, (route) => route.fulfill({ json: fx.gallery }))
    await ctx.route(`**/api/competitions/${slug}/gallery`, (route) => route.fulfill({ json: fx.gallery }))
  }
  for (const [name, options] of Object.entries(viewports)) {
    const ctx = await browser.newContext({ ...options, reducedMotion: 'reduce' })
    await serve(ctx)
    const page = await ctx.newPage()
    await page.goto(`${base}/competitions/${slug}/results`, { waitUntil: 'networkidle' })
    await page.waitForTimeout(1500)
    await check(page, `preview results ${name}`)
    await page.screenshot({ path: `${out}/preview-results-${name}.png`, fullPage: true })
    if (name === 'desktop') {
      await page.screenshot({ path: `${out}/preview-results-desktop-top.png` })
      // Keyboard: the first stops after the header are the section links, then each place's link.
      await page.keyboard.press('Tab')
      const stops = []
      for (let i = 0; i < 40; i++) {
        await page.keyboard.press('Tab')
        stops.push(await page.evaluate(() => document.activeElement?.getAttribute('aria-label') || document.activeElement?.textContent?.trim()))
      }
      console.log(`keyboard stops: ${JSON.stringify(stops.filter((s) => /seçimi|İşə bax|Münsif|Xalq/.test(s ?? '')))}`)
    }
    await page.goto(`${base}/competitions/${slug}/gallery/SUB-2026-00061`, { waitUntil: 'networkidle' })
    await page.waitForTimeout(1000)
    await check(page, `preview work ${name}`)
    await page.screenshot({ path: `${out}/preview-work-061-${name}.png`, fullPage: name !== 'desktop' })
    await ctx.close()
  }
}
await browser.close()
