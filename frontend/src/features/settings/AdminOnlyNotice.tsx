/** Shown when a non-Admin reaches an Admin-only settings section. */
export function AdminOnlyNotice({ feature }: { feature: string }) {
  return (
    <div className="notice" role="note">
      <p>
        {feature} is limited to Admins. Ask an Admin if you need a change — the server enforces this
        regardless of what this page shows.
      </p>
    </div>
  )
}
