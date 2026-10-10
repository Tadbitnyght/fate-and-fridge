import {
  FUTURE_FEATURES,
  FUTURE_STATUS_LABELS,
  type FutureFeature,
  type FutureFeatureStatus,
} from './futureFeatures'

/**
 * Display order for the status groups: the current V1 roadmap is first, then
 * deferred V2 work, then enhancements, research, and finally explicitly
 * not-planned items. `V1.x` items, when they exist, sit with V2.
 */
const STATUS_ORDER: FutureFeatureStatus[] = [
  'v2',
  'v1x',
  'enhancement',
  'research',
  'never_v1',
  'rejected',
]

function FutureFeatureCard({ feature }: { feature: FutureFeature }) {
  return (
    <li className="future-card">
      <div className="future-card__head">
        <h4>{feature.title}</h4>
        <span className={`status-chip status-chip--${feature.status}`}>
          {FUTURE_STATUS_LABELS[feature.status]}
        </span>
      </div>
      <p className="future-card__summary">{feature.summary}</p>
      <p className="future-card__source">Source: {feature.source}</p>
    </li>
  )
}

/**
 * "Quick upcoming planned features" overview for the `/progress` page's
 * "Future & ideas" tab. Renders the checked-in `futureFeatures` manifest
 * grouped by status; nothing here is approved or scheduled.
 */
export function FutureFeaturesPanel() {
  const groups = STATUS_ORDER.map((status) => ({
    status,
    items: FUTURE_FEATURES.filter((feature) => feature.status === status),
  })).filter((group) => group.items.length > 0)

  return (
    <div className="future-features">
      <p className="page-note">
        Ideas and possible future work collected in <code>docs/FUTURE_FEATURES.md</code>. Nothing
        here is approved or scheduled — an item becomes work only when the Lead classifies and
        prioritizes it into a milestone or a V1.x issue.
      </p>

      {groups.map((group) => {
        const headingId = `future-group-${group.status}`
        return (
          <section key={group.status} className="future-group" aria-labelledby={headingId}>
            <h3 id={headingId} className="future-group__title">
              {FUTURE_STATUS_LABELS[group.status]}
              <span className="future-group__count" aria-label={`${group.items.length} items`}>
                {group.items.length}
              </span>
            </h3>
            <ul className="future-list">
              {group.items.map((feature) => (
                <FutureFeatureCard key={feature.id} feature={feature} />
              ))}
            </ul>
          </section>
        )
      })}
    </div>
  )
}
