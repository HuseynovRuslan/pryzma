import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useState } from 'react'
import { describe, expect, it } from 'vitest'
import type { Category } from '../api/types'
import { CategoryPicker } from './CategoryPicker'

const NAMES = ['Biləcəri', 'Binəqədi', 'Xocəsən', 'Maştağa', 'Əmircan', 'Şüvəlan', 'Kürdəxanı', 'Pirşağı', 'Bülbülə', 'Zığ']
const categories: Category[] = NAMES.map((name, i) => ({ id: i + 1, slug: `c${i + 1}`, name, description: null, image: null }))

function Harness({ list = categories }: { list?: Category[] }) {
  const [value, setValue] = useState<string | null>(null)
  return (
    <>
      <CategoryPicker label="Qəsəbələr" categories={list} value={value} onChange={setValue} />
      <output data-testid="value">{value ?? ''}</output>
    </>
  )
}

describe('CategoryPicker', () => {
  it('shows compact numbered cards in the official order without empty descriptions', () => {
    render(<Harness />)
    const options = screen.getAllByRole('radio')
    expect(options).toHaveLength(10)
    expect(screen.getByText('01')).toBeInTheDocument()
    expect(screen.getByText('10')).toBeInTheDocument()
    expect(document.querySelector('.picker__desc')).toBeNull()
  })

  it('selects exactly one settlement as a radio group', async () => {
    render(<Harness />)
    await userEvent.click(screen.getByLabelText(/Maştağa/))
    expect(screen.getByTestId('value')).toHaveTextContent('c4')
    expect(screen.getByLabelText(/Maştağa/)).toBeChecked()
    expect(screen.getByLabelText(/Zığ/)).not.toBeChecked()
  })

  it.each([
    ['mastaga', 'Maştağa'],
    ['emircan', 'Əmircan'],
    ['suvelan', 'Şüvəlan'],
    ['kurdexani', 'Kürdəxanı'],
    ['pirshagi', 'Pirşağı'],
  ])('search “%s” narrows to %s', async (query, name) => {
    render(<Harness />)
    await userEvent.type(screen.getByRole('searchbox'), query)
    const radios = screen.getAllByRole('radio')
    expect(radios).toHaveLength(1)
    expect(screen.getByLabelText(new RegExp(name))).toBeInTheDocument()
    expect(screen.getByText('1 nəticə')).toBeInTheDocument()
  })

  it('keeps the official number when filtered', async () => {
    render(<Harness />)
    await userEvent.type(screen.getByRole('searchbox'), 'zig')
    expect(screen.getByText('10')).toBeInTheDocument()
  })

  it('explains an empty result and clears the search', async () => {
    render(<Harness />)
    await userEvent.type(screen.getByRole('searchbox'), 'qqq')
    expect(screen.getByText(/nəticə tapılmadı/)).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Axtarışı təmizlə' }))
    expect(screen.getAllByRole('radio')).toHaveLength(10)
  })

  it('hides search for short lists', () => {
    render(<Harness list={categories.slice(0, 4)} />)
    expect(screen.queryByRole('searchbox')).not.toBeInTheDocument()
  })
})
