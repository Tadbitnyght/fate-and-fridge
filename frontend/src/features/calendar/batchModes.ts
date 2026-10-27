import type { BatchRerollMode } from '../../api/calendar'

/** Menu labels for the three M7 batch reroll ranges. */
export const BATCH_MODE_LABELS: Record<BatchRerollMode, string> = {
  seven_days: '7 days',
  rest_of_month: 'Rest of month',
  fill_empty: 'Fill empty',
}

/** Display order of the batch reroll modes in the toolbar menu. */
export const BATCH_MODE_ORDER: BatchRerollMode[] = ['seven_days', 'rest_of_month', 'fill_empty']
