import { useId, useRef, useState, type FormEvent, type TextareaHTMLAttributes } from 'react'
import { ApiError } from '../api/client'
import { contactApi } from '../api/endpoints'
import { FieldError, SubmitButton, TextField } from '../components/Form'
import { Turnstile } from '../components/Turnstile'
import { usePublicConfig } from '../lib/publicConfig'
import { focusFirstInvalid, fromServer, hasErrors, validateContact, type Errors } from '../lib/validation'

const empty = { name: '', email: '', message: '' }
type Values = typeof empty

/**
 * Writing to the organiser from the site itself. The server mails the message at once (nothing is stored), so a
 * failure is shown here and the text stays in the form to send again. The robot check appears when the server
 * names a site key; a token works once, so it is asked afresh after every refused submit.
 */
export function ContactForm() {
  const config = usePublicConfig()
  const siteKey = config.captchaSiteKey ?? null
  const formRef = useRef<HTMLFormElement>(null)
  const [values, setValues] = useState<Values>(empty)
  const [errors, setErrors] = useState<Errors>({})
  const [formError, setFormError] = useState<string>()
  const [busy, setBusy] = useState(false)
  const [sent, setSent] = useState(false)
  const [captcha, setCaptcha] = useState<string | null>(null)
  const [captchaRound, setCaptchaRound] = useState(0)

  const set = (key: keyof Values, value: string) => {
    setValues((v) => ({ ...v, [key]: value }))
    // A field's error goes as soon as the person starts fixing it.
    if (errors[key]) setErrors((e) => ({ ...e, [key]: undefined }))
  }

  const onSubmit = async (e: FormEvent) => {
    e.preventDefault()
    setFormError(undefined)
    const found: Errors = {
      ...validateContact(values),
      captcha: siteKey && !captcha ? 'Robot olmadığınızı təsdiqləyin: yoxlamanı tamamlayın.' : undefined,
    }
    setErrors(found)
    if (hasErrors(found)) return focusFirstInvalid(formRef.current)

    setBusy(true)
    try {
      await contactApi.send({
        name: values.name.trim(),
        email: values.email.trim(),
        message: values.message.trim(),
        ...(siteKey && captcha ? { captchaToken: captcha } : {}),
      })
      setValues(empty)
      setSent(true)
    } catch (err) {
      const apiErr = err instanceof ApiError ? err : new ApiError(0, 'Mesaj göndərilmədi.')
      const serverErrors = fromServer(apiErr.fieldErrors)
      setErrors(serverErrors)
      setFormError(serverErrors[''] ?? apiErr.message)
      focusFirstInvalid(formRef.current)
    } finally {
      setBusy(false)
      setCaptcha(null)
      setCaptchaRound((n) => n + 1)
    }
  }

  if (sent)
    return (
      <div className="contact-sent" role="status">
        <h2>Mesajınız göndərildi</h2>
        <p>Təşəkkür edirik! Ən qısa zamanda sizə e-poçtla cavab yazacağıq.</p>
        <button type="button" className="btn" onClick={() => setSent(false)}>
          Yeni mesaj yaz
        </button>
      </div>
    )

  return (
    <form ref={formRef} className="contact-form" onSubmit={onSubmit} noValidate aria-labelledby="contact-form-title">
      <h2 id="contact-form-title">Bizə yazın</h2>
      {formError && (
        <div className="alert" role="alert">
          {formError}
        </div>
      )}
      <TextField
        label="Adınız"
        name="name"
        autoComplete="name"
        maxLength={100}
        value={values.name}
        onChange={(e) => set('name', e.target.value)}
        error={errors.name}
      />
      <TextField
        label="E-poçt"
        type="email"
        name="email"
        autoComplete="email"
        inputMode="email"
        autoCapitalize="none"
        spellCheck={false}
        maxLength={256}
        hint="Cavabı bu ünvana yazacağıq."
        value={values.email}
        onChange={(e) => set('email', e.target.value)}
        error={errors.email}
      />
      <TextAreaField
        label="Mesaj"
        name="message"
        rows={6}
        maxLength={4000}
        value={values.message}
        onChange={(e) => set('message', e.target.value)}
        error={errors.message}
      />
      {siteKey && (
        <div className="field">
          <Turnstile
            key={captchaRound}
            siteKey={siteKey}
            action="contact"
            onToken={(token) => {
              setCaptcha(token)
              if (token && errors.captcha) setErrors((e) => ({ ...e, captcha: undefined }))
            }}
          />
          {errors.captcha && <FieldError message={errors.captcha} />}
        </div>
      )}
      <SubmitButton busy={busy}>Göndər</SubmitButton>
    </form>
  )
}

/** A multi-line field with the same label, hint and error wiring as TextField. */
function TextAreaField({
  label,
  error,
  ...area
}: Omit<TextareaHTMLAttributes<HTMLTextAreaElement>, 'id'> & { label: string; error?: string }) {
  const id = useId()
  return (
    <div className="field">
      <label htmlFor={id} className="field__label">
        {label}
      </label>
      <textarea
        id={id}
        className="input contact-form__message"
        aria-invalid={error ? true : undefined}
        aria-describedby={error ? `${id}-err` : undefined}
        {...area}
      />
      {error && <FieldError id={`${id}-err`} message={error} />}
    </div>
  )
}
