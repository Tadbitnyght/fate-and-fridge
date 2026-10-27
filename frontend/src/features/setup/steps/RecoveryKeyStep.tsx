import { useRef, useState } from 'react'

/**
 * One-time recovery key display. The key is returned exactly once by
 * `POST /setup/complete`; the operator must confirm it is saved before
 * entering the app.
 */
export function RecoveryKeyStep({
  recoveryKey,
  acknowledged,
  onAcknowledgedChange,
  onContinue,
}: {
  recoveryKey: string
  acknowledged: boolean
  onAcknowledgedChange: (acknowledged: boolean) => void
  onContinue: () => void
}) {
  const keyInput = useRef<HTMLInputElement>(null)
  const [copied, setCopied] = useState(false)
  const [copyFailed, setCopyFailed] = useState(false)

  async function handleCopy(): Promise<void> {
    setCopyFailed(false)
    try {
      await navigator.clipboard.writeText(recoveryKey)
      setCopied(true)
    } catch {
      setCopied(false)
      setCopyFailed(true)
      keyInput.current?.select()
    }
  }

  return (
    <div className="wizard__step">
      <p>
        This recovery key is shown <strong>once</strong>. Store it somewhere safe — a password
        manager or a printed note. The server keeps its own copy at{' '}
        <code>/data/config/recovery.key</code>; you need this key to recover access if you lose the
        Admin password.
      </p>

      <div className="recovery-key">
        <label htmlFor="recovery-key">Recovery key</label>
        <div className="recovery-key__row">
          <input
            id="recovery-key"
            ref={keyInput}
            type="text"
            readOnly
            spellCheck={false}
            onFocus={(event) => event.target.select()}
            value={recoveryKey}
          />
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
            Copying is not available in this browser — select the key and copy it manually.
          </p>
        )}
      </div>

      <label className="form__choice">
        <input
          type="checkbox"
          checked={acknowledged}
          onChange={(event) => onAcknowledgedChange(event.target.checked)}
        />
        <span>I have saved this recovery key.</span>
      </label>

      <button
        type="button"
        className="button--primary"
        disabled={!acknowledged}
        onClick={onContinue}
      >
        Continue to Fate &amp; Fridge
      </button>
    </div>
  )
}
