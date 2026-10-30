import type { FieldErrors } from '../api/client'
import type { ContactPayload, LoginPayload, RegisterPayload } from '../api/types'

export type Errors = Record<string, string | undefined>

const EMAIL = /^[^\s@]+@[^\s@]+\.[^\s@]+$/
const PHONE = /^\+?[0-9 ()-]{7,20}$/
/** Azerbaijani mobile operator codes: Azercell 10 50 51, Bakcell 55 99, Nar 70 77, Naxtel 60. */
const MOBILE_CODES = new Set(['10', '50', '51', '55', '60', '70', '77', '99'])

// Mirrors the server rules so most mistakes are caught before a round trip; the server stays authoritative.

export function validateEmail(email: string): string | undefined {
  if (!email.trim()) return 'E-mail ünvanınızı yazın.'
  if (!EMAIL.test(email.trim())) return 'E-mail ünvanı düzgün deyil. Məsələn: ad@example.com'
  return undefined
}

export function validatePassword(password: string): string | undefined {
  if (!password) return 'Şifrə yazın.'
  if (password.length < 8) return 'Şifrə ən azı 8 simvol olmalıdır.'
  if (!/[0-9]/.test(password)) return 'Şifrədə ən azı bir rəqəm olmalıdır.'
  if (!/\p{Ll}/u.test(password)) return 'Şifrədə ən azı bir kiçik hərf olmalıdır.'
  return undefined
}

/** Only the digits of a value, in order. */
export function phoneDigits(phone: string | null | undefined): string {
  return (phone ?? '').replace(/\D/g, '')
}

/**
 * The nine-digit national number of an Azerbaijani mobile, or null. Same shapes as the server (PhoneNumbers.AzMobile):
 * 501234567, 0501234567, +994 501234567, +994 0501234567, 00994 501234567, with any spaces, dashes or brackets.
 */
export function azMobile(phone: string | null | undefined): string | null {
  const d = phoneDigits(phone)
  const national =
    d.length === 9
      ? d
      : d.length === 10 && d.startsWith('0')
        ? d.slice(1)
        : d.length === 12 && d.startsWith('994')
          ? d.slice(3)
          : d.length === 13 && d.startsWith('9940')
            ? d.slice(4)
            : d.length === 14 && d.startsWith('00994')
              ? d.slice(5)
              : null
  return national !== null && MOBILE_CODES.has(national.slice(0, 2)) ? national : null
}

export function validatePhone(phone: string): string | undefined {
  if (!phone.trim()) return 'Telefon nömrənizi yazın.'
  if (!PHONE.test(phone.trim())) return 'Telefon nömrəsini rəqəmlərlə yazın, məsələn +994 50 123 45 67.'
  if (!azMobile(phone)) return 'Azərbaycan mobil nömrəsini yazın, məsələn 050 123 45 67 və ya +994 50 123 45 67.'
  return undefined
}

export function validateRegister(v: RegisterPayload): Errors {
  return {
    firstName: v.firstName.trim() ? undefined : 'Adınızı yazın.',
    lastName: v.lastName.trim() ? undefined : 'Soyadınızı yazın.',
    phone: validatePhone(v.phone),
    email: validateEmail(v.email),
    password: validatePassword(v.password),
    confirmPassword: !v.confirmPassword
      ? 'Şifrəni təkrar yazın.'
      : v.confirmPassword !== v.password
        ? 'Şifrələr üst-üstə düşmür.'
        : undefined,
    acceptTerms: v.acceptTerms ? undefined : 'Davam etmək üçün şərtlərlə razılaşın.',
  }
}

export function validateLogin(v: LoginPayload): Errors {
  return {
    email: validateEmail(v.email),
    password: v.password ? undefined : 'Şifrənizi yazın.',
  }
}

/** The contact form, by the server's rules (Features/Contact/ContactController.cs). */
export function validateContact(v: ContactPayload): Errors {
  const name = v.name.trim()
  const message = v.message.trim()
  return {
    name: !name ? 'Adınızı yazın.' : name.length < 2 || name.length > 100 ? 'Ad 2–100 simvol olmalıdır.' : undefined,
    email: !v.email.trim()
      ? 'E-poçt ünvanınızı yazın.'
      : !EMAIL.test(v.email.trim())
        ? 'E-poçt ünvanı düzgün deyil. Məsələn: ad@example.com'
        : undefined,
    message: !message
      ? 'Mesajınızı yazın.'
      : message.length < 10 || message.length > 4000
        ? 'Mesaj 10–4000 simvol olmalıdır.'
        : undefined,
  }
}

export function hasErrors(errors: Errors): boolean {
  return Object.values(errors).some(Boolean)
}

/** First server message per field, flattened for display. */
export function fromServer(fieldErrors: FieldErrors): Errors {
  const out: Errors = {}
  for (const [key, messages] of Object.entries(fieldErrors)) out[key] = messages[0]
  return out
}

/** Move focus to the first invalid field so keyboard and screen-reader users land on the problem. */
export function focusFirstInvalid(container: HTMLElement | null) {
  requestAnimationFrame(() => {
    const el = container?.querySelector<HTMLElement>('[aria-invalid="true"]')
    el?.focus()
    el?.scrollIntoView({ block: 'center', behavior: 'smooth' })
  })
}
