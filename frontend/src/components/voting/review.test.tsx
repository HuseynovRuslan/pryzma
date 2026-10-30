import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import type { VotingAudit } from '../../api/types'
import { auditLine } from '../../pages/voting/lib'
import { AuditNotice, AuditUpdates, ReviewStatus } from './VotingReview'

const audit = (over: Partial<VotingAudit> = {}): VotingAudit => ({
  state: 'Closed',
  validVotes: 1370,
  invalidatedVotes: 256,
  totalVotes: 1626,
  entries: [],
  updatedAt: '2026-10-07T08:00:00Z',
  ...over,
})

describe('the provisional stage', () => {
  it('says the votes are checked and shows the real totals — and promises nothing of the jury (D16)', () => {
    render(<ReviewStatus audit={audit()} />)
    expect(screen.getByRole('heading', { name: 'Səslər yoxlanılır' })).toBeInTheDocument()
    expect(screen.getByText('İlkin nəticələr')).toBeInTheDocument()
    // The owner's three sentences (9 October 2026), word for word, in their order.
    for (const sentence of ['Səsvermə başa çatıb.', 'Göstərilən sıralama etibarlı səslərə əsaslanan ilkin nəticədir.', 'Yekun nəticələr ayrıca elan ediləcək.'])
      expect(screen.getByText(sentence)).toBeInTheDocument()
    // The jury's evaluation is internal: no public word of it, nor of a jury deciding places.
    expect(document.body.textContent).not.toMatch(/münsif|dəyərləndir|qiymətləndir/i)
    expect(screen.getByText('Etibarlı səslər').nextSibling).toHaveTextContent('1370')
    expect(screen.getByText('Ümumi səslər').nextSibling).toHaveTextContent('1626')
    // Nothing claims a result.
    expect(screen.queryByText(/qalib/i)).not.toBeInTheDocument()
  })

  it('draws no counters before the server has answered', () => {
    render(<ReviewStatus audit={null} />)
    expect(screen.queryByText('Etibarlı səslər')).not.toBeInTheDocument()
  })

  it('says so when no vote has been set aside', () => {
    render(<AuditUpdates audit={audit({ invalidatedVotes: 0, totalVotes: 1370 })} />)
    expect(screen.getByText('Hələlik etibarsız sayılmış səs yoxdur.')).toBeInTheDocument()
  })

  it('words each decision by its rule, never by anybody’s own text', () => {
    expect(auditLine({ votes: 2, reason: 'CoordinatedAccounts' })).toBe(
      '2 səs koordinasiyalı hesab klasteri qaydasına əsasən etibarsız sayıldı.',
    )
    expect(auditLine({ votes: 3, reason: 'DuplicatePhone' })).toBe('3 səs təkrarlanan telefon məlumatlarına görə etibarsız sayıldı.')
  })

  it('words a disposable-e-mail decision by its rule', () => {
    expect(auditLine({ votes: 244, reason: 'DisposableEmail' })).toBe('244 səs müvəqqəti e-poçt qaydasına əsasən etibarsız sayıldı.')
  })
})

describe('the audit after the disposable-e-mail rule (D14)', () => {
  const after = audit({
    validVotes: 1126,
    invalidatedVotes: 500,
    totalVotes: 1626,
    entries: [
      { at: '2026-10-07T14:30:00Z', votes: 244, reason: 'DisposableEmail' },
      { at: '2026-10-06T10:00:00Z', votes: 125, reason: 'CoordinatedAccounts' },
      { at: '2026-10-05T10:00:00Z', votes: 131, reason: 'DuplicatePhone' },
    ],
    rules: [
      { reason: 'DuplicatePhone', votes: 131 },
      { reason: 'CoordinatedAccounts', votes: 125 },
      { reason: 'DisposableEmail', votes: 244 },
    ],
  })

  it('says publicly what was detected and how the ranking changed — never who, never "deleted"', () => {
    const { container } = render(<AuditNotice audit={after} />)
    expect(screen.getByRole('heading', { name: 'Səsvermə auditi yeniləndi' })).toBeInTheDocument()
    expect(container).toHaveTextContent(
      'Səsvermə bağlandıqdan sonra aparılan yekun yoxlama zamanı müvəqqəti (disposable) e-poçt xidmətləri ilə səsvermə ' +
        'müddətində yaradılmış hesablardan verilən 244 səs müəyyən edilib. Bu səslər silinməyib — audit izi saxlanılmaqla ' +
        'etibarsız sayılıb və sıralama yenidən hesablanıb.',
    )
    expect(screen.getByText('Ümumi səs').nextSibling).toHaveTextContent('1626')
    expect(screen.getByText('Etibarlı səs').nextSibling).toHaveTextContent('1126')
    expect(screen.getByText('Etibarsız sayılmış səs').nextSibling).toHaveTextContent('500')
    expect(container).toHaveTextContent('Müvəqqəti e-poçt qaydası: 244 səs')
    expect(screen.getByText('Qayda bütün iştirakçılara eyni şəkildə tətbiq olunub.')).toBeInTheDocument()
    expect(container.textContent).not.toMatch(/saxta|bot|fırıldaq|fraud|@/i)
  })

  it('stays away until votes have been set aside under the rule', () => {
    const { container } = render(<AuditNotice audit={audit()} />)
    expect(container).toBeEmptyDOMElement()
  })

  it('breaks the set-aside votes down by rule, explains each, and adds them up', () => {
    const { container } = render(<AuditUpdates audit={after} />)
    expect(screen.getByRole('heading', { name: 'Etibarsız sayılmış səslər — 500' })).toBeInTheDocument()
    expect(screen.getByText(/Telefon təkrarı —/)).toHaveTextContent('Telefon təkrarı — 131')
    expect(screen.getByText(/R2 qaydası —/)).toHaveTextContent('R2 qaydası — 125')
    expect(screen.getByText(/Müvəqqəti e-poçt —/)).toHaveTextContent('Müvəqqəti e-poçt — 244')
    expect(
      screen.getByText('Səsvermə müddətində yaradılmış və müvəqqəti e-poçt xidmətindən istifadə etmiş hesabların səsləri.'),
    ).toBeInTheDocument()
    expect(container).toHaveTextContent('131 + 125 + 244 = 500')
    expect(screen.getByText('244 səs müvəqqəti e-poçt qaydasına əsasən etibarsız sayıldı.')).toBeInTheDocument()
  })
})
