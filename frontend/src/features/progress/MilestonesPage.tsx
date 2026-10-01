import { Link } from 'react-router-dom'

import {
  MILESTONES,
  STATUS_LABELS,
  issueUrl,
  progressSummary,
  type Milestone,
  type MilestoneStatus,
} from './milestones'

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

/** Milestone roadmap with visual evidence (issue #22); rendered from the manifest. */
export function MilestonesPage() {
  const summary = progressSummary()
  const percentLabel = `${summary.percent}%`

  return (
    <section className="page progress-page" aria-labelledby="progress-heading">
      <h2 id="progress-heading" tabIndex={-1}>
        Progress
      </h2>

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
    </section>
  )
}
