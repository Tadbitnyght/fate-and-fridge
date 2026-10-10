/// <reference types="node" />
import { existsSync, readFileSync } from 'node:fs'
import { resolve } from 'node:path'

import { describe, expect, it } from 'vitest'

import { FUTURE_FEATURES, FUTURE_STATUS_LABELS, type FutureFeatureStatus } from './futureFeatures'

const VALID_STATUSES: FutureFeatureStatus[] = [
  'v2',
  'v1x',
  'enhancement',
  'research',
  'never_v1',
  'rejected',
]

/** Kebab-case: lowercase alphanumerics separated by single hyphens. */
const KEBAB_ID = /^[a-z0-9]+(?:-[a-z0-9]+)*$/

/** The two non-feature meta sections that must not appear as manifest items. */
const META_SECTIONS = new Set(['How to use this document', 'Summary'])

/** Maps a `Status: **…**` value in the registry to the manifest status. */
const DOC_STATUS_TO_MANIFEST: Record<string, FutureFeatureStatus> = {
  V2: 'v2',
  'V1.x': 'v1x',
  Enhancement: 'enhancement',
  Research: 'research',
  'Never-V1': 'never_v1',
  Rejected: 'rejected',
}

interface DocSection {
  heading: string
  status: string
}

/** Parses `## Heading` blocks and the first `Status: **X**` line in each. */
function parseFeatureSections(markdown: string): DocSection[] {
  const sections: DocSection[] = []
  let current: DocSection | null = null

  for (const line of markdown.split(/\r?\n/)) {
    const heading = /^##\s+(.+?)\s*$/.exec(line)
    if (heading) {
      if (current) sections.push(current)
      current = { heading: heading[1], status: '' }
      continue
    }
    if (current && current.status === '') {
      const status = /^Status:\s*\*\*(.+?)\*\*/.exec(line)
      if (status) current.status = status[1].trim()
    }
  }
  if (current) sections.push(current)

  return sections.filter((section) => !META_SECTIONS.has(section.heading))
}

const DOC_PATH = resolve(process.cwd(), '../docs/FUTURE_FEATURES.md')

describe('future feature manifest', () => {
  it('is non-empty with unique kebab-case ids', () => {
    expect(FUTURE_FEATURES.length).toBeGreaterThan(0)

    const ids = FUTURE_FEATURES.map((feature) => feature.id)
    expect(new Set(ids).size).toBe(ids.length)
    for (const id of ids) {
      expect(id, `id "${id}" is not kebab-case`).toMatch(KEBAB_ID)
    }
  })

  it('uses valid statuses with labels and non-empty required text', () => {
    for (const feature of FUTURE_FEATURES) {
      expect(VALID_STATUSES, `invalid status on ${feature.id}`).toContain(feature.status)
      expect(
        FUTURE_STATUS_LABELS[feature.status],
        `missing label for ${feature.status}`,
      ).toBeTruthy()
      expect(feature.title.trim().length, `empty title on ${feature.id}`).toBeGreaterThan(0)
      expect(feature.summary.trim().length, `empty summary on ${feature.id}`).toBeGreaterThan(0)
      expect(feature.source.trim().length, `empty source on ${feature.id}`).toBeGreaterThan(0)
    }
  })

  it('lists every docs/FUTURE_FEATURES.md feature section with a matching status', () => {
    if (!existsSync(DOC_PATH)) {
      console.warn(
        `[futureFeatures.test] Skipping registry consistency check: ${DOC_PATH} not found. ` +
          'Sync docs/ next to the frontend mirror to run it.',
      )
      return
    }

    const sections = parseFeatureSections(readFileSync(DOC_PATH, 'utf8'))
    expect(sections.length).toBeGreaterThan(0)

    for (const section of sections) {
      const expectedStatus = DOC_STATUS_TO_MANIFEST[section.status]
      expect(
        expectedStatus,
        `unknown Status "${section.status}" for section "${section.heading}"`,
      ).toBeDefined()

      const feature = FUTURE_FEATURES.find((candidate) => candidate.title === section.heading)
      expect(feature, `missing manifest entry for section "${section.heading}"`).toBeDefined()
      expect(feature?.status, `status mismatch for section "${section.heading}"`).toBe(
        expectedStatus,
      )
    }

    // The manifest must not carry entries the registry no longer documents.
    for (const feature of FUTURE_FEATURES) {
      expect(
        sections.some((section) => section.heading === feature.title),
        `manifest entry "${feature.title}" is not a docs/FUTURE_FEATURES.md section`,
      ).toBe(true)
    }
  })
})
