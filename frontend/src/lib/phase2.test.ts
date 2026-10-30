import { describe, expect, it } from 'vitest'
import type { FileRules, FormField } from '../api/types'
import { formatBytes } from './format'
import { describeFileRules, fileError, validateFields } from './formRules'
import { matchesSearch, singularAz } from './text'

const SETTLEMENTS = ['Maştağa', 'Əmircan', 'Şüvəlan', 'Kürdəxanı', 'Pirşağı', 'Bakıxanov', 'M.Ə. Rəsulzadə', '28 May', 'Çilov', 'Zığ']

describe('settlement search', () => {
  it.each([
    ['mastaga', 'Maştağa'],
    ['MAŞTAĞA', 'Maştağa'],
    ['emircan', 'Əmircan'],
    ['suvelan', 'Şüvəlan'],
    ['shuvelan', 'Şüvəlan'],
    ['kurdexani', 'Kürdəxanı'],
    ['pirshagi', 'Pirşağı'],
    ['pirsagi', 'Pirşağı'],
    ['bakikhanov', 'Bakıxanov'],
    ['chilov', 'Çilov'],
    ['resulzade', 'M.Ə. Rəsulzadə'],
    ['m.e. resul', 'M.Ə. Rəsulzadə'],
    ['28may', '28 May'],
    ['zig', 'Zığ'],
  ])('“%s” finds %s', (query, expected) => {
    expect(SETTLEMENTS.filter((s) => matchesSearch(s, query))).toContain(expected)
  })

  it('does not match unrelated names', () => {
    expect(SETTLEMENTS.filter((s) => matchesSearch(s, 'mastaga'))).toEqual(['Maştağa'])
    expect(SETTLEMENTS.filter((s) => matchesSearch(s, 'xyz'))).toEqual([])
  })

  it('treats an empty query as “show all”', () => {
    expect(SETTLEMENTS.every((s) => matchesSearch(s, '  '))).toBe(true)
  })
})

describe('singularAz', () => {
  it.each([
    ['Qəsəbələr', 'Qəsəbə'],
    ['Kəndlər', 'Kənd'],
    ['Janrlar', 'Janr'],
    ['Kateqoriyalar', 'Kateqoriya'],
    ['Nominasiya', 'Nominasiya'],
  ])('%s → %s', (plural, singular) => expect(singularAz(plural)).toBe(singular))
})

const field = (over: Partial<FormField>): FormField => ({
  id: 1,
  key: 'title',
  label: 'İşin adı',
  fieldType: 'ShortText',
  placeholder: null,
  helpText: null,
  isRequired: true,
  sortOrder: 1,
  options: null,
  maxLength: 150,
  files: null,
  ...over,
})

describe('dynamic form validation', () => {
  const fields = [
    field({}),
    field({ key: 'concept', label: 'Konseptin izahı', fieldType: 'LongText', maxLength: 5000 }),
    field({ key: 'notes', label: 'Əlavə qeyd', fieldType: 'LongText', isRequired: false }),
    field({ key: 'tech', label: 'Texnika', fieldType: 'Select', options: ['Vektor', 'Əl işi'], isRequired: false }),
    field({ key: 'files', label: 'Fayllar', fieldType: 'File' }),
  ]

  it('reports required fields with the field name, and skips optional ones', () => {
    const errors = validateFields(fields, {}, { files: 0 })
    expect(errors.title).toBe('“İşin adı” sahəsini doldurun.')
    expect(errors.concept).toBeDefined()
    expect(errors.files).toBe('“Fayllar” üçün ən azı bir fayl əlavə edin.')
    expect(errors.notes).toBeUndefined()
  })

  it('checks lengths and select options', () => {
    const errors = validateFields(fields, { title: 'ş'.repeat(151), concept: 'x', tech: 'Yağlı boya' }, { files: 1 })
    expect(errors.title).toContain('150')
    expect(errors.tech).toContain('siyahıdan')
    expect(errors.concept).toBeUndefined()
  })

  it('passes a complete answer set', () => {
    expect(validateFields(fields, { title: 'Maştağa', concept: 'İzah', tech: 'Əl işi' }, { files: 2 })).toEqual({})
  })
})

describe('file rules', () => {
  const rules: FileRules = {
    maxFiles: 5,
    maxFileSizeBytes: 10 * 1024 * 1024,
    accept: [
      { key: 'png', label: 'PNG', mimeType: 'image/png', extensions: ['.png'] },
      { key: 'jpeg', label: 'JPG', mimeType: 'image/jpeg', extensions: ['.jpg', '.jpeg'] },
      { key: 'webp', label: 'WEBP', mimeType: 'image/webp', extensions: ['.webp'] },
      { key: 'pdf', label: 'PDF', mimeType: 'application/pdf', extensions: ['.pdf'] },
    ],
  }
  const file = (name: string, size = 1000) => new File([new Uint8Array(size)], name)

  it('describes the policy from data', () => {
    expect(describeFileRules(rules)).toBe('PNG, JPG, WEBP və ya PDF. Hər fayl ən çox 10 MB, cəmi 5 fayl.')
  })

  it.each(['logo.svg', 'setup.exe', 'sened.docx', 'skript.png.js'])('rejects %s', (name) => {
    expect(fileError(file(name), rules, 0)).toContain('qəbul olunmur')
  })

  it('rejects oversized and surplus files, accepts the rest', () => {
    expect(fileError(file('boyuk.png', 10 * 1024 * 1024 + 1), rules, 0)).toContain('çox böyükdür')
    expect(fileError(file('artiq.png'), rules, 5)).toContain('5 fayl')
    expect(fileError(file('ESKIZ.JPEG'), rules, 0)).toBeUndefined()
  })

  it('formats sizes the Azerbaijani way', () => {
    expect(formatBytes(10 * 1024 * 1024)).toBe('10 MB')
    expect(formatBytes(2.5 * 1024 * 1024)).toBe('2,5 MB')
    expect(formatBytes(6 * 1024)).toBe('6 KB')
  })
})
