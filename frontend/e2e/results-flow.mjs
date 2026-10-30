// Results journey (Phase 7: one winner per settlement) against a THROWAWAY stack whose database was just seeded
// (Bakı kəndləri, 59 settlements): participants A (Maştağa) and B (Şüvəlan) submit through the UI, C (Əmircan) and
// D (a second Maştağa entry) through the API → admin makes the file + concept fields public → selects A as Maştağa's
// winner and B as Şüvəlan's (each in its own modal) → D shows Maştağa's existing-winner guard → the public site still
// exposes nothing → admin closes entries and publishes (modal with the settlement counts) → public results with the
// winners' names, gallery, tolerant category filter, the winning work, no non-winner identity → İşlərim.
// A second, generic competition (no categories) proves nothing depends on "Bakı kəndləri".
// Credentials: QA_ADMIN_EMAIL / QA_ADMIN_PASSWORD from the environment, never printed.
// Usage: BASE_URL=http://localhost:5174 node e2e/results-flow.mjs [screenshotDir] [width]
import { chromium } from '@playwright/test'
import { mkdirSync, readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'

const base = process.env.BASE_URL ?? 'http://localhost:5174'
const out = process.argv[2] ?? 'e2e/screenshots'
const width = Number(process.argv[3] ?? 1440)
const adminEmail = process.env.QA_ADMIN_EMAIL
const adminPassword = process.env.QA_ADMIN_PASSWORD
if (!adminEmail || !adminPassword) throw new Error('QA_ADMIN_EMAIL / QA_ADMIN_PASSWORD are not set')
const fixture = (name) => fileURLToPath(new URL(`./fixtures/${name}`, import.meta.url))
mkdirSync(out, { recursive: true })

const CHEVRON = 'baki-kendleri-sevron-dizayni'
const run = Date.now().toString(36)
const PRIVATE_NOTE = `Şəxsi əlavə qeyd ${run}`
const ADMIN_NOTE = `Daxili admin qeydi ${run}`

const browser = await chromium.launch({ channel: 'chromium' })
const newContext = () =>
  browser.newContext({
    viewport: { width, height: width < 768 ? 844 : 900 },
    deviceScaleFactor: width < 768 ? 2 : 1,
    reducedMotion: 'reduce',
  })
const contexts = []
const page = async () => {
  const ctx = await newContext()
  contexts.push(ctx)
  return ctx.newPage()
}
const step = (msg) => console.log(`✓ [${width}] ${msg}`)
const overflow = []
async function shot(p, name) {
  const wide = await p.evaluate(() => document.documentElement.scrollWidth - window.innerWidth)
  if (wide > 1) overflow.push(`${name}: +${wide}px`)
  await p.screenshot({ path: `${out}/p4-${name}-${width}.png`, fullPage: true })
}
const check = (cond, msg) => {
  if (!cond) throw new Error(msg)
}

/** Registers a participant and submits a chevron entry through the UI; returns { page, email, reference }. */
async function submitThroughUi(settlementQuery, settlementName, title) {
  const p = await page()
  await p.goto(`${base}/competitions/${CHEVRON}`)
  await p.getByRole('searchbox').fill(settlementQuery)
  await p.locator('.picker__option', { hasText: settlementName }).click()
  await p.locator('.action-bar button, .comp__panel .btn').filter({ visible: true }).first().click()
  await p.waitForURL(/\/login\?next=/)
  await p.getByRole('link', { name: 'Qeydiyyatdan keçin' }).click()
  const email = `p4-${settlementQuery}-${run}-${width}@meydan.test`
  await p.getByLabel('Ad', { exact: true }).fill('Günel')
  await p.getByLabel('Soyad').fill('Şükürova')
  await p.getByLabel('Telefon').fill('+994 51 111 22 33')
  await p.getByLabel('E-mail').fill(email)
  await p.getByLabel('Şifrə', { exact: true }).fill('meydan2026')
  await p.getByLabel('Şifrəni təkrar et').fill('meydan2026')
  await p.locator('input[name="acceptTerms"]').check()
  await p.getByRole('button', { name: 'Hesab yarat' }).click()
  await p.waitForURL(/\/submissions\/\d+$/)
  await p.getByLabel('İşin adı').fill(title)
  await p.getByLabel('Konseptin izahı').fill(`${title}: qəsəbənin bağları və Xəzər küləyi.`)
  await p.getByLabel('İstifadə olunan simvollar və onların mənası').fill('Nar — bolluq. Dalğa — Xəzər.')
  await p.getByLabel('Əlavə qeyd').fill(PRIVATE_NOTE)
  await p.getByRole('button', { name: 'Davam et' }).click()
  await p.waitForURL(/step=files/)
  await p.locator('input[type="file"]').setInputFiles([fixture('eskiz.png'), fixture('izah.pdf')])
  await p.locator('.filecard:not(.is-uploading):not(.is-error)').nth(1).waitFor()
  await p.getByRole('button', { name: 'Önizləməyə keç' }).click()
  await p.waitForURL(/step=preview/)
  await p.waitForFunction(() => !document.querySelector('.sub__actions--final .btn--primary')?.hasAttribute('disabled'))
  await p.getByRole('button', { name: 'Təklifi təqdim et' }).click()
  await p.getByRole('heading', { name: 'Təklifiniz qəbul edildi.' }).waitFor()
  const reference = (await p.locator('.receipt__ref-value').textContent()).trim()
  return { page: p, email, reference }
}

/** Plain API calls from inside a page (same origin, through the Vite proxy). */
const apiCall = (p, method, url, body, token) =>
  p.evaluate(
    async ({ method, url, body, token }) => {
      const r = await fetch(url, {
        method,
        headers: { ...(body instanceof Object ? { 'Content-Type': 'application/json' } : {}), ...(token ? { Authorization: `Bearer ${token}` } : {}) },
        body: body ? JSON.stringify(body) : undefined,
      })
      const text = await r.text()
      return { status: r.status, json: text && (text.startsWith('{') || text.startsWith('[')) ? JSON.parse(text) : null, text }
    },
    { method, url, body, token },
  )

try {
  // ---- Participants -------------------------------------------------------------------------
  const a = await submitThroughUi('mastaga', 'Maştağa', 'Maştağa: nar və külək')
  const b = await submitThroughUi('suvelan', 'Şüvəlan', 'Şüvəlan dalğası')
  step(`A (Maştağa) ${a.reference} and B (Şüvəlan) ${b.reference} submitted through the UI`)

  const helper = await page()
  await helper.goto(base + '/')
  const cReg = await apiCall(helper, 'POST', '/api/auth/register', {
    firstName: 'Rəşad', lastName: 'Əmirov', phone: '+994 55 222 33 44', email: `p4-c-${run}-${width}@meydan.test`,
    password: 'meydan2026', confirmPassword: 'meydan2026', acceptTerms: true,
  })
  const cToken = cReg.json.accessToken
  const cDraft = await apiCall(helper, 'POST', '/api/submissions', { competitionSlug: CHEVRON, categorySlug: 'emircan' }, cToken)
  await apiCall(helper, 'PUT', `/api/submissions/${cDraft.json.id}`, { answers: { title: 'Əmircan qapısı', concept: 'Qala qapısı', symbols: 'Qapı' } }, cToken)
  const cId = cDraft.json.id
  await helper.evaluate(async ({ id, token, b64 }) => {
    const bytes = Uint8Array.from(atob(b64), (ch) => ch.charCodeAt(0))
    const form = new FormData()
    form.append('fieldKey', 'files')
    form.append('file', new File([bytes], 'emircan.jpg', { type: 'image/jpeg' }))
    await fetch(`/api/submissions/${id}/files`, { method: 'POST', body: form, headers: { Authorization: `Bearer ${token}` } })
  }, { id: cId, token: cToken, b64: readFileSync(fixture('variant.jpg')).toString('base64') })
  const cSubmit = await apiCall(helper, 'POST', `/api/submissions/${cId}/submit`, null, cToken)
  check(cSubmit.status === 200, `C submit failed: ${cSubmit.text}`)
  step(`C (Əmircan) ${cSubmit.json.referenceNumber} submitted through the API`)

  const dHelper = await page()
  await dHelper.goto(base + '/')
  const dReg = await apiCall(dHelper, 'POST', '/api/auth/register', {
    firstName: 'Nərmin', lastName: 'Cəfərova', phone: '+994 55 333 44 55', email: `p4-d-${run}-${width}@meydan.test`,
    password: 'meydan2026', confirmPassword: 'meydan2026', acceptTerms: true,
  })
  const dToken = dReg.json.accessToken
  const dDraft = await apiCall(dHelper, 'POST', '/api/submissions', { competitionSlug: CHEVRON, categorySlug: 'mastaga' }, dToken)
  await apiCall(dHelper, 'PUT', `/api/submissions/${dDraft.json.id}`, { answers: { title: 'Maştağa — ikinci baxış', concept: 'Bağlar', symbols: 'Nar' } }, dToken)
  await dHelper.evaluate(async ({ id, token, b64 }) => {
    const bytes = Uint8Array.from(atob(b64), (ch) => ch.charCodeAt(0))
    const form = new FormData()
    form.append('fieldKey', 'files')
    form.append('file', new File([bytes], 'mastaga-2.jpg', { type: 'image/jpeg' }))
    await fetch(`/api/submissions/${id}/files`, { method: 'POST', body: form, headers: { Authorization: `Bearer ${token}` } })
  }, { id: dDraft.json.id, token: dToken, b64: readFileSync(fixture('variant.jpg')).toString('base64') })
  const dSubmit = await apiCall(dHelper, 'POST', `/api/submissions/${dDraft.json.id}/submit`, null, dToken)
  check(dSubmit.status === 200, `D submit failed: ${dSubmit.text}`)
  const d = { reference: dSubmit.json.referenceNumber }
  step(`D (a second Maştağa entry) ${d.reference} submitted through the API`)

  // ---- Admin: login, form visibility ---------------------------------------------------------
  const admin = await page()
  await admin.goto(base + '/admin')
  await admin.waitForURL(/\/login\?next=/)
  await admin.getByLabel('E-mail').fill(adminEmail)
  await admin.getByLabel('Şifrə', { exact: true }).fill(adminPassword)
  await admin.getByRole('button', { name: 'Daxil ol' }).click()
  await admin.waitForURL(/\/admin$/)
  const adminToken = (await apiCall(admin, 'POST', '/api/auth/login', { email: adminEmail, password: adminPassword, rememberMe: false })).json.accessToken
  const competitions = (await apiCall(admin, 'GET', '/api/admin/competitions', null, adminToken)).json
  const chevronId = competitions.find((c) => c.slug === CHEVRON).id

  await admin.goto(`${base}/admin/competitions/${chevronId}?tab=form`)
  for (const label of ['Fayllar', 'Konseptin izahı']) {
    await admin.locator('.a-fieldrow', { hasText: label }).getByRole('button', { name: 'Redaktə' }).click()
    await admin.getByLabel('Nəticələr dərc olunandan sonra qalereyada göstər').check()
    await admin.getByRole('button', { name: 'Yadda saxla' }).click()
    await admin.getByText('Sahə yeniləndi.').waitFor()
    await admin.getByRole('button', { name: 'Bağla' }).click()
  }
  check((await admin.locator('.a-tag--public').count()) === 3, 'title, concept and files should be public after results')
  await shot(admin, '01-form-visibility')
  step('admin: title (default), concept and files marked "public after results"; notes stay private')

  // ---- Admin: select the winner --------------------------------------------------------------
  await admin.goto(`${base}/admin/submissions?competitionId=${chevronId}`)
  await admin.getByLabel('Axtarış').fill(a.reference)
  await admin.getByText('1 təklif', { exact: true }).waitFor()
  await admin.getByRole('link', { name: a.reference }).click()
  await admin.waitForURL(/\/admin\/submissions\/\d+$/)
  await admin.getByLabel('Yeni qeyd').fill(ADMIN_NOTE)
  await admin.getByRole('button', { name: 'Qeydi əlavə et' }).click()
  await admin.getByText(ADMIN_NOTE).waitFor()
  check(!(await admin.getByLabel('Yeni status').locator('option', { hasText: 'Qalib' }).count()), 'winner must not be a moderation status')
  await admin.getByRole('button', { name: 'Maştağa üzrə qalib seç' }).click()
  const dialog = admin.getByRole('dialog', { name: 'Bu işi Maştağa üzrə qalibi seçmək istəyirsiniz?' })
  await dialog.waitFor()
  for (const text of [a.reference, 'Maştağa: nar və külək', 'Maştağa', 'Günel Şükürova']) await dialog.getByText(text, { exact: true }).first().waitFor()
  await shot(admin, '02-select-winner-modal')
  await dialog.getByRole('button', { name: 'Bəli, qalib seç' }).click()
  await admin.getByText('Qalib seçildi. Nəticə hələ dərc olunmayıb.').waitFor()
  await admin.getByText('Bu iş Maştağa üzrə qalibdir.').waitFor()
  await shot(admin, '03-winner-selected')
  step('admin: "Maştağa üzrə qalib seç" → confirmation modal with reference, title, settlement, participant → saved')

  // Another settlement is independent: Şüvəlan gets its own winner.
  await admin.goto(`${base}/admin/submissions?competitionId=${chevronId}&q=${encodeURIComponent(b.reference)}`)
  await admin.getByRole('link', { name: b.reference }).click()
  await admin.getByRole('button', { name: 'Şüvəlan üzrə qalib seç' }).click()
  await admin.getByRole('dialog', { name: 'Bu işi Şüvəlan üzrə qalibi seçmək istəyirsiniz?' }).getByRole('button', { name: 'Bəli, qalib seç' }).click()
  await admin.getByText('Bu iş Şüvəlan üzrə qalibdir.').waitFor()
  step('admin: Şüvəlan gets its own winner; Maştağa keeps its winner')

  // The same settlement is guarded: D (Maştağa) sees the existing winner; replacing needs its own confirmation.
  await admin.goto(`${base}/admin/submissions?competitionId=${chevronId}&q=${encodeURIComponent(d.reference)}`)
  await admin.getByRole('link', { name: d.reference }).click()
  await admin.getByText('Maştağa üzrə qalib artıq seçilib', { exact: false }).waitFor()
  check(!(await admin.getByRole('button', { name: 'Maştağa üzrə qalib seç' }).count()), 'no silent overwrite: "Maştağa üzrə qalib seç" must be gone')
  await admin.getByRole('button', { name: 'Qalibi dəyiş' }).click()
  await admin.getByRole('dialog', { name: 'Maştağa üzrə qalibi dəyişmək istəyirsiniz?' }).waitFor()
  await shot(admin, '04-change-winner-modal')
  await admin.getByRole('button', { name: 'Ləğv et' }).click()
  step('admin: a second Maştağa entry shows the existing winner; changing needs its own explicit confirmation (cancelled)')

  // ---- Public before publication ---------------------------------------------------------------
  const visitor = await page()
  await visitor.goto(`${base}/competitions/${CHEVRON}`)
  await visitor.getByRole('heading', { level: 1 }).waitFor()
  check(!(await visitor.getByText('Nəticələr açıqlanıb').count()), 'results must not be announced yet')
  for (const url of [`/api/competitions/${CHEVRON}/results`, `/api/competitions/${CHEVRON}/gallery`, `/api/competitions/${CHEVRON}/gallery/${a.reference}`]) {
    const r = await apiCall(visitor, 'GET', url)
    check(r.status === 404, `${url} must be 404 before publication, got ${r.status}`)
  }
  const winnerFiles = await apiCall(admin, 'GET', `/api/admin/submissions?competitionId=${chevronId}&winner=true`, null, adminToken)
  const winnerRefs = winnerFiles.json.items.map((i) => i.referenceNumber).sort()
  check(JSON.stringify(winnerRefs) === JSON.stringify([a.reference, b.reference].sort()), `admin winner filter: ${winnerRefs}`)
  const aRow = winnerFiles.json.items.find((i) => i.referenceNumber === a.reference)
  const detail = await apiCall(admin, 'GET', `/api/admin/submissions/${aRow.id}`, null, adminToken)
  const guessedFile = await apiCall(visitor, 'GET', `/api/competitions/${CHEVRON}/gallery/${a.reference}/files/${detail.json.files[0].id}`)
  check(guessedFile.status === 404, 'a guessed public file URL must be 404 before publication')
  await visitor.goto(`${base}/competitions/${CHEVRON}/results`)
  await visitor.getByRole('heading', { name: 'Nəticələr hələ açıqlanmayıb' }).waitFor()
  await shot(visitor, '05-results-before-publication')
  step('public before publication: no announcement, results/gallery/work/file all 404, results page says so')

  // ---- Admin: close entries, publish ---------------------------------------------------------
  await admin.goto(`${base}/admin/competitions/${chevronId}?tab=results`)
  await admin.getByText('qəbulu bağlayın').waitFor()
  check(await admin.getByRole('button', { name: 'Nəticəni dərc et' }).isDisabled(), 'publish must be disabled while entries are open')
  await shot(admin, '06-results-tab-prepared')
  await admin.getByRole('link', { name: 'Əsas məlumat' }).click()
  await admin.getByRole('button', { name: 'Qəbulu bağla', exact: true }).click()
  await admin.getByRole('button', { name: 'Bəli, bağla', exact: true }).click()
  await admin.getByText('Bağlanıb: saytda görünür, təklif qəbul etmir.').waitFor()

  await visitor.goto(`${base}/competitions/${CHEVRON}`)
  await visitor.getByRole('heading', { name: 'Qəbul başa çatıb' }).waitFor()
  await visitor.getByText('Nəticələr hazırlanır.', { exact: false }).first().waitFor()
  await shot(visitor, '07-competition-closed-preparing')

  await admin.getByRole('link', { name: 'Nəticələr' }).click()
  await admin.getByRole('button', { name: 'Nəticəni dərc et' }).click()
  const publish = admin.getByRole('dialog', { name: 'Mövcud nəticələri dərc etmək istəyirsiniz?' })
  await publish.getByText('59 qəsəbədən 2-si üzrə qalib seçilib.').waitFor()
  await publish.getByText('Qaliblərin adı və soyadı nəticə səhifəsində göstəriləcək.', { exact: false }).waitFor()
  await shot(admin, '08-publish-modal')
  await publish.getByRole('button', { name: 'Bəli, dərc et' }).click()
  await admin.locator('.a-badge').getByText('Dərc olunub', { exact: true }).waitFor()
  await shot(admin, '09-results-tab-published')
  step('admin: publish blocked while open → close entries → publish with "59 qəsəbədən 2-si üzrə qalib seçilib" in the modal')

  // ---- Public after publication ----------------------------------------------------------------
  await visitor.goto(`${base}/competitions/${CHEVRON}`)
  await visitor.getByRole('heading', { name: 'Nəticələr açıqlanıb' }).waitFor()
  await shot(visitor, '10-competition-results-out')
  await visitor.getByRole('link', { name: 'Nəticələrə bax' }).first().click()
  await visitor.waitForURL(/\/results$/)
  const mastagaCard = visitor.locator('.wcard', { hasText: 'Maştağa: nar və külək' })
  await mastagaCard.getByText('Qalib', { exact: true }).waitFor()
  await mastagaCard.getByText('Maştağa', { exact: true }).waitFor()
  await mastagaCard.getByText('Günel Şükürova', { exact: true }).waitFor()     // a winner's name is public now
  await visitor.locator('.wcard', { hasText: 'Şüvəlan dalğası' }).waitFor()
  await visitor.waitForFunction(() => [...document.querySelectorAll('.wcard img')].some((img) => img.complete && img.naturalWidth > 0))
  check((await visitor.locator('.wcard').count()) === 2, 'one winner per settlement: Maştağa and Şüvəlan')
  check(!(await visitor.evaluate(() => document.body.innerText)).includes('Rəşad Əmirov'), "a non-winner's name is never public")
  await shot(visitor, '11-results')
  step('public results: Maştağa and Şüvəlan winners with their names and the winning images; nobody else named')

  await visitor.getByRole('link', { name: 'İşlərə bax' }).last().click()
  await visitor.waitForURL(/\/gallery$/)
  await visitor.locator('.gcard').nth(3).waitFor()
  check((await visitor.locator('.gcard--winner').count()) === 2, 'two winner cards')
  await visitor.locator('.gcard--winner .winner-badge').first().waitFor()
  await visitor.getByRole('link', { name: 'Bütün qəsəbələr' }).waitFor()
  await shot(visitor, '12-gallery')
  for (const [q, name] of [['suvelan', 'Şüvəlan'], ['emircan', 'Əmircan'], ['mastaga', 'Maştağa']]) {
    const search = visitor.getByLabel('Qəsəbələr arasında axtar')
    if (await search.count()) await search.fill(q)
    await visitor.locator('.gchip', { hasText: name }).waitFor()
  }
  await visitor.locator('.gchip', { hasText: 'Maştağa' }).click()
  await visitor.waitForURL(/category=mastaga/)
  await visitor.getByText('Maştağa:', { exact: false }).first().waitFor()
  check((await visitor.locator('.gcard').count()) === 2, 'Maştağa filter should leave its two works')
  await shot(visitor, '13-gallery-mastaga')
  step('gallery: 4 works, 2 winner cards; search suvelan/emircan/mastaga finds Şüvəlan/Əmircan/Maştağa; filter → 2')

  await visitor.locator('.gcard--winner .gcard__link').first().click()
  await visitor.waitForURL(new RegExp(`/gallery/${a.reference}$`))
  await visitor.locator('.work .winner-badge').waitFor()
  await visitor.getByRole('heading', { name: 'Maştağa üzrə qalib', level: 1 }).waitFor()
  await visitor.locator('.work__facts').getByText('Günel Şükürova', { exact: true }).waitFor()
  await visitor.getByText('qəsəbənin bağları və Xəzər küləyi', { exact: false }).waitFor()
  await visitor.getByText('Sənədin birinci səhifəsi (önizləmə)', { exact: false }).first().waitFor() // PDF itself is never public
  const body = await visitor.evaluate(() => document.body.innerText)
  for (const secret of [a.email, '+994', PRIVATE_NOTE, ADMIN_NOTE, 'eskiz.png', 'izah.pdf', 'Yoxlanılır', 'Göndərilib'])
    check(!body.includes(secret), `private data visible on the public work page: ${secret}`)
  const pubApi = await apiCall(visitor, 'GET', `/api/competitions/${CHEVRON}/gallery/${a.reference}`)
  for (const secret of [a.email, PRIVATE_NOTE, ADMIN_NOTE, 'eskiz.png', 'userId', 'phone', 'email'])
    check(!pubApi.text.includes(secret), `private data in the public API: ${secret}`)
  const dApi = await apiCall(visitor, 'GET', `/api/competitions/${CHEVRON}/gallery/${d.reference}`)
  check(!dApi.text.includes('Nərmin') && !dApi.text.includes('Cəfərova'), "a non-winner's name is never in the public API")
  await shot(visitor, '14-winning-work')
  step("winning work: Maştağa üzrə qalib — the winner's name, image, concept, PDF page preview; no e-mail, phone, notes, file names; D anonymous")

  await visitor.goto(base + '/results')
  await visitor.locator('.rlist__item').first().waitFor()
  await shot(visitor, '15-results-list')

  // ---- Participants' İşlərim ---------------------------------------------------------------
  await a.page.goto(base + '/my/submissions')
  await a.page.locator('.subs__result .winner-badge').waitFor()
  await a.page.getByRole('link', { name: 'Nəticələr açıqlanıb — bax' }).waitFor()
  await shot(a.page, '16-islerim-winner')
  await b.page.goto(base + '/my/submissions')
  await b.page.locator('.subs__result .winner-badge').waitFor()             // B won Şüvəlan
  await helper.goto(base + '/my/submissions')
  await helper.getByRole('link', { name: 'Nəticələr açıqlanıb — bax' }).waitFor()
  check(!(await helper.locator('.winner-badge').count()), 'C is not a winner')
  await shot(helper, '17-islerim-neutral')
  step('İşlərim: A and B see "Qalib", C sees a neutral "Nəticələr açıqlanıb — bax"')

  await admin.goto(`${base}/admin/submissions?competitionId=${chevronId}`)
  await admin.locator('tr.is-winner .winner-badge').first().waitFor()
  check((await admin.locator('tr.is-winner').count()) === 2, 'admin list marks both settlement winners')
  await shot(admin, '18-admin-list-winner')

  // Persistence: a reload of the results page (new request) still shows the same winner.
  await visitor.goto(`${base}/competitions/${CHEVRON}/results`)
  await visitor.locator('.wcard', { hasText: 'Maştağa: nar və külək' }).waitFor()

  // ---- A generic competition: no categories, nothing Bakı-specific ---------------------------
  const projectId = competitions.find((c) => c.slug === CHEVRON).projectId
  const g = await apiCall(admin, 'POST', '/api/admin/competitions', {
    projectId, title: 'Laçın loqosu', slug: `lacin-loqosu-${run}`, shortDescription: 'Generic QA müsabiqəsi', hasCategories: false,
  }, adminToken)
  const gid = g.json.id
  await apiCall(admin, 'POST', `/api/admin/competitions/${gid}/fields`, {
    key: 'ad', label: 'İşin adı', fieldType: 'ShortText', isRequired: true, isSubmissionTitle: true, isActive: true, isPublicAfterResults: true,
  }, adminToken)
  await apiCall(admin, 'PUT', `/api/admin/competitions/${gid}/status`, { status: 'Published' }, adminToken)
  const ids = []
  for (const title of ['Qartal', 'Dağ çiçəyi']) {
    const d = await apiCall(helper, 'POST', '/api/submissions', { competitionSlug: `lacin-loqosu-${run}` }, cToken)
    await apiCall(helper, 'PUT', `/api/submissions/${d.json.id}`, { answers: { ad: title } }, cToken)
    await apiCall(helper, 'POST', `/api/submissions/${d.json.id}/submit`, null, cToken)
    ids.push(d.json.id)
  }
  await admin.goto(`${base}/admin/submissions/${ids[1]}`)
  await admin.getByRole('button', { name: 'Müsabiqə üzrə qalib seç' }).click()
  await admin.getByRole('button', { name: 'Bəli, qalib seç' }).click()
  await admin.getByText('Qalib seçildi.', { exact: false }).waitFor()
  await apiCall(admin, 'PUT', `/api/admin/competitions/${gid}/status`, { status: 'Closed' }, adminToken)
  await admin.goto(`${base}/admin/competitions/${gid}?tab=results`)
  await admin.getByRole('button', { name: 'Nəticəni dərc et' }).click()
  await admin.getByRole('button', { name: 'Bəli, dərc et' }).click()
  await admin.locator('.a-badge').getByText('Dərc olunub', { exact: true }).waitFor()
  await visitor.goto(`${base}/competitions/lacin-loqosu-${run}/results`)
  await visitor.getByRole('heading', { name: 'Qalib', level: 2 }).waitFor()
  await visitor.locator('.wcard', { hasText: 'Dağ çiçəyi' }).waitFor()
  check(!(await visitor.locator('.wcard__place').count()), 'no category on a competition without categories')
  await shot(visitor, '19-generic-results')
  await visitor.goto(`${base}/competitions/lacin-loqosu-${run}/gallery`)
  await visitor.locator('.gcard').nth(1).waitFor()
  check(!(await visitor.locator('.gfilter').count()), 'no category filter without categories')
  await shot(visitor, '20-generic-gallery')
  step('generic "Laçın loqosu" (no categories): winner chosen in the UI, published, results + gallery without filter')

  if (overflow.length) throw new Error(`horizontal overflow: ${overflow.join(', ')}`)
  console.log(`Results flow (one winner per settlement) passed at ${width}px.`)
} catch (err) {
  for (const [i, ctx] of contexts.entries())
    for (const p of ctx.pages()) await p.screenshot({ path: `${out}/p4-failure-${i}-${width}.png`, fullPage: true }).catch(() => {})
  console.error(`✗ [${width}] ${err.message}`)
  process.exitCode = 1
} finally {
  await browser.close()
}
