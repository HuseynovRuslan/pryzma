import type { CSSProperties } from 'react'

const INK = '#10262b'
const PAPER = '#f3f4f0'
const HEX = /^#([0-9a-f]{6})$/i

function luminance(hex: string): number {
  const n = parseInt(hex.slice(1), 16)
  const channel = (v: number) => {
    const s = v / 255
    return s <= 0.03928 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4
  }
  return 0.2126 * channel((n >> 16) & 255) + 0.7152 * channel((n >> 8) & 255) + 0.0722 * channel(n & 255)
}

function contrast(a: string, b: string): number {
  const [l1, l2] = [luminance(a), luminance(b)].sort((x, y) => y - x)
  return (l1 + 0.05) / (l2 + 0.05)
}

/** Ink or paper — whichever reads better on the given colour. */
export function readableOn(hex: string): string {
  return contrast(hex, PAPER) >= contrast(hex, INK) ? PAPER : INK
}

/** CSS custom properties that let any subtree adopt a project's identity. */
export function accentVars(accent: string | null | undefined): CSSProperties {
  const color = accent && HEX.test(accent) ? accent : INK
  return { '--accent': color, '--on-accent': readableOn(color) } as CSSProperties
}
