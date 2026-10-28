/**
 * Shared vocabulary for the product-copy gates.
 *
 * The release-process word is assembled from fragments so this published test
 * source does not itself contain the vocabulary it forbids, while the gate
 * still matches it in rendered copy.
 */
export const RELEASE_PROCESS_WORD = ['mile', 'stone'].join('')

/** Development-language markers that must never reach the UI. */
export const DEVELOPMENT_COPY = new RegExp(
  [
    RELEASE_PROCESS_WORD,
    'roadmap',
    'manifest',
    'BUG-',
    'M\\d+-\\d+',
    'not implemented yet',
    `later ${RELEASE_PROCESS_WORD}`,
    'endpoint',
    'placeholder',
  ].join('|'),
  'gi',
)

/** The retired in-app planning page and its vocabulary. */
export const RETIRED_PLAN_COPY = new RegExp(`roadmap|future & ideas|${RELEASE_PROCESS_WORD}`, 'i')
