import { describe, expect, it } from 'vitest'

import {
  MILESTONES,
  REPOSITORY_URL,
  STATUS_LABELS,
  issueUrl,
  progressSummary,
  type MilestoneStatus,
} from './milestones'

const VALID_STATUSES: MilestoneStatus[] = ['complete', 'in_progress', 'planned']

describe('milestone manifest', () => {
  it('has unique ids and numbers covering M0 through M14', () => {
    const ids = MILESTONES.map((milestone) => milestone.id)
    expect(new Set(ids).size).toBe(ids.length)
    expect(ids).toEqual(Array.from({ length: 15 }, (_, index) => `M${index}`))
    expect(MILESTONES.map((milestone) => milestone.number)).toEqual(
      Array.from({ length: 15 }, (_, index) => index),
    )
  })

  it('uses valid statuses and non-empty required fields', () => {
    for (const milestone of MILESTONES) {
      expect(VALID_STATUSES).toContain(milestone.status)
      expect(milestone.name.length).toBeGreaterThan(0)
      expect(milestone.summary.length).toBeGreaterThan(0)
      expect(milestone.features.length).toBeGreaterThan(0)
      expect(STATUS_LABELS[milestone.status]).toBeTruthy()
    }
  })

  it('marks M0 and M1 complete with dates, evidence, and the M1 review tag', () => {
    const m0 = MILESTONES.find((milestone) => milestone.id === 'M0')
    const m1 = MILESTONES.find((milestone) => milestone.id === 'M1')

    expect(m0?.status).toBe('complete')
    expect(m0?.completedOn).toBe('2026-09-28')

    expect(m1?.status).toBe('complete')
    expect(m1?.completedOn).toBe('2026-09-30')
    expect(m1?.tag).toBe('m1-complete')
    expect(m1?.commit).toBe('f0c5dc7')
    expect(m1?.evidence?.map((item) => item.value)).toEqual(
      expect.arrayContaining(['643', '94', '11']),
    )
    expect(m1?.evidence?.some((item) => item.label.includes('Independent review'))).toBe(true)
  })

  it('links the M1 feature issues and the tracking issues', () => {
    const m1 = MILESTONES.find((milestone) => milestone.id === 'M1')
    for (const issue of [2, 11, 16, 21]) {
      expect(m1?.issues).toContain(issue)
    }
    expect(m1?.issuesNote).toMatch(/issue/i)
  })

  it('keeps planned milestones free of GitHub issue links', () => {
    const planned = MILESTONES.filter((milestone) => milestone.status === 'planned')
    expect(planned.length).toBeGreaterThan(0)
    for (const milestone of planned) {
      expect(milestone.issues).toEqual([])
      expect(milestone.issuesNote).toBeTruthy()
    }
    const inProgress = MILESTONES.filter((milestone) => milestone.status === 'in_progress')
    for (const milestone of inProgress) {
      expect(milestone.issues.length).toBeGreaterThan(0)
    }
  })

  it('marks M3 complete with its sub-issue links and verification evidence', () => {
    const m3 = MILESTONES.find((milestone) => milestone.id === 'M3')
    expect(m3?.status).toBe('complete')
    expect(m3?.completedOn).toBe('2026-10-03')
    expect(m3?.tag).toBe('m3-complete')
    for (const issue of [36, 40, 48, 37, 38, 39]) {
      expect(m3?.issues).toContain(issue)
    }
    expect(m3?.issuesNote).toMatch(/M3-01/)
    expect(m3?.evidence).toEqual(
      expect.arrayContaining([
        { label: 'Backend tests', value: '1066' },
        { label: 'Vitest', value: '180' },
        { label: 'Playwright (hermetic)', value: '24 passed / 2 pre-existing stack skips' },
        { label: 'M3 verification', value: 'independent review + full battery' },
        { label: 'Backend gates', value: 'format/build/EF clean' },
      ]),
    )
  })

  it('marks M2 complete with its verification evidence and issue links', () => {
    const m2 = MILESTONES.find((milestone) => milestone.id === 'M2')
    expect(m2?.status).toBe('complete')
    expect(m2?.completedOn).toBe('2026-10-01')
    expect(m2?.tag).toBe('m2-complete')
    expect(m2?.issues).toContain(23)
    expect(m2?.issues).toContain(32)
    expect(m2?.issuesNote).toContain('M2-12')
    expect(m2?.evidence).toEqual(
      expect.arrayContaining([
        { label: 'Backend tests', value: '976' },
        { label: 'Vitest', value: '165' },
        { label: 'Playwright (hermetic)', value: '20 passed / 2 pre-existing skips' },
        { label: 'M2 verification', value: 'independent review + full battery' },
        { label: 'Backend gates', value: 'format/build/EF clean' },
      ]),
    )
  })

  it('computes the progress summary from the manifest', () => {
    expect(progressSummary()).toEqual({
      total: 15,
      completed: 4,
      inProgress: 0,
      percent: Math.round((4 / 15) * 100),
    })
    expect(
      progressSummary([
        { ...MILESTONES[0], status: 'in_progress' },
        { ...MILESTONES[1], status: 'complete' },
      ]),
    ).toEqual({ total: 2, completed: 1, inProgress: 1, percent: 50 })
    expect(progressSummary([])).toEqual({ total: 0, completed: 0, inProgress: 0, percent: 0 })
  })

  it('builds GitHub issue urls from the repository base url', () => {
    expect(REPOSITORY_URL).toBe('https://github.com/Tadbitnyght/fate-and-fridge')
    expect(issueUrl(11)).toBe(`${REPOSITORY_URL}/issues/11`)
  })
})
