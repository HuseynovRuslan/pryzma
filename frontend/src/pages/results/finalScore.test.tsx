import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it } from 'vitest'
import type { GalleryItem } from '../../api/types'
import { WinnerCard } from './shared'

const item: GalleryItem = {
  reference: 'SUB-2026-00111',
  title: 'Xilədən Əmircana',
  category: null,
  winnerName: 'Leyla Quliyeva',
  preview: null,
  previewState: 'None',
  place: 1,
} as unknown as GalleryItem

describe('a placed work from the jury final', () => {
  it('says how the place was reached — votes, ranks and points on both sides, and the sum — and names no juror', () => {
    render(
      <MemoryRouter>
        <ul>
          <WinnerCard
            item={item}
            slug="emircanin-simvolu"
            place={null}
            final={{
              juryMax: 60,
              score: {
                place: 1,
                reference: 'SUB-2026-00111',
                validVotes: 196,
                publicRank: 1,
                publicPoints: 10,
                juryTotal: 52.5,
                juryRank: 3,
                juryPoints: 8,
                finalScore: 18,
              },
            }}
          />
        </ul>
      </MemoryRouter>,
    )
    const why = screen.getByLabelText('Yekun xalın izahı')
    expect(why).toHaveTextContent('Xalq196 etibarlı səs · 1-ci yer · 10 xal')
    expect(why).toHaveTextContent('Münsiflər52,5 / 60 · 3-cü yer · 8 xal')
    expect(why).toHaveTextContent('Yekun18 xal')
    // The author is named as before; no juror is.
    expect(screen.getByText('Leyla Quliyeva')).toBeInTheDocument()
    expect(document.body.textContent).not.toMatch(/Münsif \d/)
  })

  it('shows nothing extra for a result that did not come from a final', () => {
    render(
      <MemoryRouter>
        <ul>
          <WinnerCard item={item} slug="emircanin-simvolu" place={null} />
        </ul>
      </MemoryRouter>,
    )
    expect(document.querySelector('.wcard__final')).toBeNull()
  })
})
