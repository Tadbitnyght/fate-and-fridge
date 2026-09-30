import { useEffect, useRef, useState } from 'react'

/** One-time temporary password display (shown once, never retrievable again). */
export function OneTimePasswordPanel({
  username,
  password,
  onDismiss,
}: {
  username: string
  password: string
  onDismiss: () => void
}) {
  const headingRef = useRef<HTMLHeadingElement>(null)
  const [copied, setCopied] = useState(false)
  const [copyFailed, setCopyFailed] = useState(false)

  useEffect(() => {
    headingRef.current?.focus()
  }, [])

  async function handleCopy(): Promise<void> {
    setCopyFailed(false)
    try {
      await navigator.clipboard.writeText(password)
      setCopied(true)
    } catch {
      setCopied(false)
      setCopyFailed(true)
    }
  }

  return (
    <section className="one-time" aria-labelledby="one-time-title">
      <h4 id="one-time-title" tabIndex={-1} ref={headingRef}>
        Temporary password for {username}
      </h4>
      <p className="form__hint">
        Shown once. Copy it now — it cannot be retrieved again. The user must change it at first
        sign-in.
      </p>
      <div className="recovery-key__row">
        <input type="text" readOnly value={password} aria-label="Temporary password" />
        <button type="button" onClick={() => void handleCopy()}>
          Copy
        </button>
      </div>
      {copied && (
        <p className="form__hint" role="status">
          Copied to clipboard.
        </p>
      )}
      {copyFailed && (
        <p className="form__hint" role="status">
          Copying is not available in this browser — select the password and copy it manually.
        </p>
      )}
      <div className="modal__actions">
        <button type="button" className="button--primary" onClick={onDismiss}>
          Done
        </button>
      </div>
    </section>
  )
}
