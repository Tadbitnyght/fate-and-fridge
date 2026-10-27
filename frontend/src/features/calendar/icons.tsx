import type { JSX } from 'react'

import type { CalendarIconKey } from '../../api/calendar'

/**
 * Curated special-event icons (docs/M5_PLAN.md §1.14). A small inline-SVG set
 * keyed to the backend catalog; decorative (the entry text carries the meaning),
 * so each icon is `aria-hidden`.
 */
const PATHS: Record<CalendarIconKey, string> = {
  turkey: 'M12 3c3 0 5 2 5 5 0 4-3 6-5 10-2-4-5-6-5-10 0-3 2-5 5-5Zm0-2 1 2-2 0Z',
  cake: 'M4 20v-6h16v6Zm2-10h12l-1-3H7Zm0 0V7h12v3M8 12v4m4-4v4m4-4v4',
  heart: 'M12 20S4 14.5 4 9.5A4.5 4.5 0 0 1 12 6a4.5 4.5 0 0 1 8 3.5C20 14.5 12 20 12 20Z',
  star: 'M12 3l2.6 5.4 5.9.8-4.3 4.1 1 5.9L12 16.9 6.8 19.2l1-5.9L3.5 9.2l5.9-.8Z',
  pumpkin: 'M12 5c4 0 7 3 7 7s-3 8-7 8-7-4-7-8 3-7 7-7Zm0 0V3m-2 2 2 1 2-1',
  gift: 'M4 10h16v10H4Zm-1-4h18v4H3Zm9 0v14m-4-10a2 2 0 1 1 0-4c2 0 4 4 4 4m4-4a2 2 0 1 0 0 4c-2 0-4 4-4 4',
  snowflake: 'M12 2v20M3 7l18 10M21 7 3 17',
  flag: 'M5 21V4m0 0h11l-1.5 3L16 10H5',
  fireworks: 'M12 12l6-6M12 12 6 6m6 6v-8m0 8 6 6m-6-6-6 6M4 12H2m20 0h-2',
  clover: 'M12 12a3 3 0 1 1 3 3 3 3 0 1 1-3-3 3 3 0 1 1-3 3 3 3 0 1 1 3-3Z',
  leaf: 'M5 19C5 11 11 5 19 5c0 8-6 14-14 14Zm0 0 8-8',
  egg: 'M12 3c4 0 6 5 6 9a6 6 0 0 1-12 0c0-4 2-9 6-9Z',
}

export function EventIcon({
  iconKey,
  className,
}: {
  iconKey: string | null
  className?: string
}): JSX.Element | null {
  if (iconKey === null || !(iconKey in PATHS)) {
    return null
  }

  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      width="1em"
      height="1em"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.6"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      focusable="false"
    >
      <path d={PATHS[iconKey as CalendarIconKey]} />
    </svg>
  )
}
