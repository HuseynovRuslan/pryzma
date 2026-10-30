import { useState, type FormEvent } from 'react'
import { PageError, PageLoading } from '../../components/PageState'
import { useApi } from '../../lib/useApi'
import { useTitle } from '../../lib/useTitle'
import { adminApi, type PlatformSettingsInput } from '../api'
import type { PlatformSettings } from '../../api/types'
import { CoverPanel } from '../CoverPanel'
import { readErrors, type FieldErrors } from '../errors'
import { AField } from '../forms'
import { Notice, PageHeader } from '../ui'

/**
 * The platform's own words. Everything a visitor reads that is about the site rather than about a competition —
 * its name, the home page's two lines, the line beside the sign-in form — is edited here and takes effect on the
 * next page load. The browser carries the same values as a fallback, so a mistake here never blanks the site.
 */
export default function SettingsPage() {
  useTitle('Parametrlər')
  const state = useApi((signal) => adminApi.settings(signal), [])
  if (state.status === 'loading') return <PageLoading />
  if (state.status === 'error') return <PageError error={state.error} onRetry={state.retry} />
  return <Editor initial={state.data} />
}

function Editor({ initial }: { initial: PlatformSettings }) {
  const [settings, setSettings] = useState(initial)
  const [values, setValues] = useState<PlatformSettingsInput>({
    platformName: initial.platformName,
    homeHeroTitle: initial.homeHeroTitle,
    homeHeroSubtitle: initial.homeHeroSubtitle,
    authPromoTitle: initial.authPromoTitle,
  })
  const [errors, setErrors] = useState<FieldErrors>({})
  const [message, setMessage] = useState<{ tone: 'error' | 'success'; text: string }>()
  const [busy, setBusy] = useState(false)

  const set = <K extends keyof PlatformSettingsInput>(key: K, value: PlatformSettingsInput[K]) =>
    setValues((v) => ({ ...v, [key]: value }))

  const applied = (next: PlatformSettings, text: string) => {
    setSettings(next)
    setMessage({ tone: 'success', text })
  }

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setErrors({})
    setMessage(undefined)
    try {
      applied(await adminApi.updateSettings(values), 'Yadda saxlanıldı. Səhifə yeniləndikdən sonra saytda görünür.')
    } catch (err) {
      const read = readErrors(err, 'Yadda saxlamaq alınmadı.')
      setErrors(read.fields)
      setMessage({ tone: 'error', text: read.message })
    } finally {
      setBusy(false)
    }
  }

  return (
    <>
      <PageHeader title="Brend və ana səhifə" />
      {message && <Notice tone={message.tone}>{message.text}</Notice>}

      <div className="a-split">
        <form className="a-panel a-form" onSubmit={submit} noValidate>
          <AField
            label="Platformanın adı"
            hint="Başlıqda, altlıqda və brauzerin başlığında görünür. 1–50 simvol."
            error={errors.platformName}
          >
            <input className="input" maxLength={50} value={values.platformName} onChange={(e) => set('platformName', e.target.value)} />
          </AField>
          <AField
            label="Ana səhifə başlığı"
            hint="Bir cümlə. Sətirlərə özü bölünür — burada sətir qurmağa ehtiyac yoxdur. 1–120 simvol."
            error={errors.homeHeroTitle}
          >
            <input className="input" maxLength={120} value={values.homeHeroTitle} onChange={(e) => set('homeHeroTitle', e.target.value)} />
          </AField>
          <AField
            label="Ana səhifə alt mətni"
            hint="Başlığın altında və altlıqda göstərilir. 1–300 simvol."
            error={errors.homeHeroSubtitle}
          >
            <textarea className="input" rows={3} maxLength={300} value={values.homeHeroSubtitle} onChange={(e) => set('homeHeroSubtitle', e.target.value)} />
          </AField>
          <AField
            label="Giriş səhifəsi başlığı"
            hint="Daxil ol və qeydiyyat səhifələrinin yanındakı sətir. 1–120 simvol."
            error={errors.authPromoTitle}
          >
            <input className="input" maxLength={120} value={values.authPromoTitle} onChange={(e) => set('authPromoTitle', e.target.value)} />
          </AField>
          <p className="a-muted">HTML işarələri (&lt; &gt;) qəbul olunmur — mətn olduğu kimi göstərilir.</p>
          <div className="a-actions">
            <button type="submit" className="btn btn--primary" disabled={busy}>
              {busy ? 'Gözləyin…' : 'Yadda saxla'}
            </button>
          </div>
        </form>

        <div className="a-form a-split__aside">
          <CoverPanel
            title="Giriş səhifəsi vizualı"
            hint="Daxil ol və qeydiyyat səhifələrinin yanında göstərilir. PNG, JPG və ya WEBP. Boş qalsa çəkilmiş buta naxışı qalır."
            image={settings.authPromoImage}
            preview={<img className="a-poster" src={settings.authPromoImage ?? undefined} alt="Hazırkı giriş vizualı" />}
            upload={(file, onProgress) => adminApi.uploadAuthImage(file, onProgress)}
            remove={() => adminApi.removeAuthImage()}
            onChange={(next) => applied(next, 'Şəkil yeniləndi.')}
          />
        </div>
      </div>
    </>
  )
}
