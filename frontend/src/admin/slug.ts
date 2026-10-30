const MAP: Record<string, string> = { ə: 'e', ı: 'i', ş: 's', ç: 'c', ğ: 'g', ö: 'o', ü: 'u' }

/** Same transliteration as the server (Meydan.Domain.Common.Slug): "Bakı kəndləri" → "baki-kendleri". */
export function slugify(text: string): string {
  return text
    .trim()
    .toLocaleLowerCase('az')
    .replace(/[əışçğöü]/g, (c) => MAP[c])
    .normalize('NFD')
    .replace(/[̀-ͯ]/g, '')
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
}
