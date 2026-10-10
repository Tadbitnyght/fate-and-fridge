import { useRef, useState, type KeyboardEvent } from 'react'
import { Link } from 'react-router-dom'

import { FutureFeaturesPanel } from './FutureFeaturesPanel'
import {
  MILESTONES,
  STATUS_LABELS,
  issueUrl,
  progressSummary,
  type Milestone,
  type MilestoneStatus,
} from './milestones'

type ProgressTab = 'milestones' | 'future'

const TABS: { id: ProgressTab; label: string }[] = [
  { id: 'milestones', label: 'Milestones' },
  { id: 'future', label: 'Future & ideas' },
]

function StatusChip({ status }: { status: MilestoneStatus }) {
  return <span className={`status-chip status-chip--${status}`}>{STATUS_LABELS[status]}</span>
}

function MilestoneCard({ milestone }: { milestone: Milestone }) {
  return (
    <li className="milestone-card">
      <div className="milestone-card__head">
        <h3>
          {milestone.id} — {milestone.name}
        </h3>
        <StatusChip status={milestone.status} />
      </div>

      <p className="milestone-card__dates">
        {milestone.completedOn
          ? `Completed ${milestone.completedOn}`
          : milestone.status === 'in_progress'
            ? 'In progress'
            : 'Planned'}
        {milestone.tag && <span className="milestone-card__meta"> · tag {milestone.tag}</span>}
        {milestone.commit && (
          <span className="milestone-card__meta"> · commit {milestone.commit}</span>
        )}
      </p>

      <p>{milestone.summary}</p>

      <h4>Features</h4>
      <ul className="milestone-features">
        {milestone.features.map((feature) => (
          <li key={feature}>{feature}</li>
        ))}
      </ul>

      {milestone.evidence && milestone.evidence.length > 0 && (
        <>
          <h4>Evidence</h4>
          <ul className="evidence-list">
            {milestone.evidence.map((item) => (
              <li key={item.label} className="evidence-chip">
                <span className="evidence-chip__label">{item.label}</span>
                <span className="evidence-chip__value">{item.value}</span>
              </li>
            ))}
          </ul>
        </>
      )}

      {milestone.issues.length > 0 && (
        <>
          <h4>GitHub issues</h4>
          <ul className="issue-links">
            {milestone.issues.map((issueNumber) => (
              <li key={issueNumber}>
                <a href={issueUrl(issueNumber)} target="_blank" rel="noreferrer">
                  #{issueNumber}
                </a>
              </li>
            ))}
          </ul>
        </>
      )}
      {milestone.issuesNote && <p className="page-note">{milestone.issuesNote}</p>}
    </li>
  )
}

/** The current milestone roadmap: summary bar, intro, and the manifest cards. */
function MilestonesPanel() {
  const summary = progressSummary()
  const percentLabel = `${summary.percent}%`

  return (
    <>
      <p className="progress-summary">
        {summary.completed} of {summary.total} milestones complete ({percentLabel}).
      </p>
      <div
        className="progress-bar"
        role="progressbar"
        aria-label="Milestones complete"
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuenow={summary.percent}
        aria-valuetext={percentLabel}
      >
        <span className="progress-bar__fill" style={{ width: percentLabel }} />
      </div>

      <p className="page-note">
        Each milestone lists its features, completion date, and the test evidence recorded when it
        was accepted. Links open the tracked GitHub issues.{' '}
        <Link to="/dashboard">Back to the dashboard</Link>.
      </p>

      <ol className="milestone-list">
        {MILESTONES.map((milestone) => (
          <MilestoneCard key={milestone.id} milestone={milestone} />
        ))}
      </ol>
    </>
  )
}

/**
 * Milestone roadmap with visual evidence (issue #22) plus a "Future & ideas"
 * tab backed by the checked-in future registry (issue #117). Both views render
 * offline from manifests — no runtime fetch, no external service.
 */
export function MilestonesPage() {
  const [activeTab, setActiveTab] = useState<ProgressTab>('milestones')
  const tabRefs = useRef<Array<HTMLButtonElement | null>>([])

  function activateTab(index: number) {
    const clamped = (index + TABS.length) % TABS.length
    setActiveTab(TABS[clamped].id)
    tabRefs.current[clamped]?.focus()
  }

  function handleTabKeyDown(event: KeyboardEvent<HTMLButtonElement>, index: number) {
    switch (event.key) {
      case 'ArrowRight':
        event.preventDefault()
        activateTab(index + 1)
        break
      case 'ArrowLeft':
        event.preventDefault()
        activateTab(index - 1)
        break
      case 'Home':
        event.preventDefault()
        activateTab(0)
        break
      case 'End':
        event.preventDefault()
        activateTab(TABS.length - 1)
        break
      default:
        break
    }
  }

  return (
    <section className="page progress-page" aria-labelledby="progress-heading">
      <h2 id="progress-heading" tabIndex={-1}>
        Progress
      </h2>

      <div className="progress-tabs" role="tablist" aria-label="Progress views">
        {TABS.map((tab, index) => {
          const selected = activeTab === tab.id
          return (
            <button
              key={tab.id}
              type="button"
              role="tab"
              id={`progress-tab-${tab.id}`}
              className={`progress-tab${selected ? ' progress-tab--active' : ''}`}
              aria-selected={selected}
              aria-controls={`progress-panel-${tab.id}`}
              tabIndex={selected ? 0 : -1}
              ref={(element) => {
                tabRefs.current[index] = element
              }}
              onClick={() => setActiveTab(tab.id)}
              onKeyDown={(event) => handleTabKeyDown(event, index)}
            >
              {tab.label}
            </button>
          )
        })}
      </div>

      {activeTab === 'milestones' ? (
        <div
          className="progress-tabpanel"
          id="progress-panel-milestones"
          role="tabpanel"
          aria-labelledby="progress-tab-milestones"
          tabIndex={0}
        >
          <MilestonesPanel />
        </div>
      ) : (
        <div
          className="progress-tabpanel"
          id="progress-panel-future"
          role="tabpanel"
          aria-labelledby="progress-tab-future"
          tabIndex={0}
        >
          <FutureFeaturesPanel />
        </div>
      )}
    </section>
  )
}
