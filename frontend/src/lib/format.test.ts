import { describe, expect, it } from 'vitest'
import { formatDateTime } from './format'

describe('formatDateTime', () => {
  it('shows UTC timestamps in Halifax standard time during winter', () => {
    // 14:00 UTC in January is 10:00 AST (UTC-4).
    const text = formatDateTime('2026-01-15T14:00:00Z')
    expect(text).toContain('Jan 15, 2026')
    expect(text).toMatch(/10:00\sa\.m\./)
    expect(text).toContain('AST')
  })

  it('switches to Atlantic daylight time in summer', () => {
    // 14:00 UTC in July is 11:00 ADT (UTC-3).
    const text = formatDateTime('2026-07-15T14:00:00Z')
    expect(text).toMatch(/11:00\sa\.m\./)
    expect(text).toContain('ADT')
  })

  it('uses the Halifax calendar date, not the UTC date', () => {
    // 02:30 UTC on Jan 16 is still Jan 15 in Halifax.
    expect(formatDateTime('2026-01-16T02:30:00Z')).toContain('Jan 15, 2026')
  })
})
