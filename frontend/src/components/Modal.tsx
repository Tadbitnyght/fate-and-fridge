import { useEffect, useId, useRef, type ReactNode } from 'react'

/**
 * Small accessible modal: `role="dialog"`, Escape closes, focus moves into the
 * panel on open and returns to the trigger on close. It does not implement a
 * full focus trap (documented limitation for M1).
 */
export function Modal({
  title,
  onClose,
  children,
}: {
  title: string
  onClose: () => void
  children: ReactNode
}) {
  const panelRef = useRef<HTMLDivElement>(null)
  const titleId = useId()
  const onCloseRef = useRef(onClose)

  useEffect(() => {
    onCloseRef.current = onClose
  })

  useEffect(() => {
    const previous = document.activeElement instanceof HTMLElement ? document.activeElement : null
    panelRef.current?.focus()

    function handleKeyDown(event: KeyboardEvent): void {
      if (event.key === 'Escape') {
        onCloseRef.current()
      }
    }

    document.addEventListener('keydown', handleKeyDown)
    return () => {
      document.removeEventListener('keydown', handleKeyDown)
      previous?.focus()
    }
  }, [])

  return (
    <div
      className="modal-backdrop"
      onMouseDown={(event) => {
        if (event.target === event.currentTarget) {
          onCloseRef.current()
        }
      }}
    >
      <div
        className="modal"
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        tabIndex={-1}
        ref={panelRef}
      >
        <h3 id={titleId} className="modal__title">
          {title}
        </h3>
        {children}
      </div>
    </div>
  )
}

export function ConfirmDialog({
  title,
  message,
  confirmLabel,
  pendingLabel = 'Working…',
  pending,
  error,
  onConfirm,
  onClose,
}: {
  title: string
  message: ReactNode
  confirmLabel: string
  pendingLabel?: string
  pending: boolean
  error?: string | null
  onConfirm: () => void
  onClose: () => void
}) {
  return (
    <Modal title={title} onClose={onClose}>
      <div className="modal__body">{message}</div>
      {error && (
        <p className="form__error" role="alert">
          {error}
        </p>
      )}
      <div className="modal__actions">
        <button type="button" onClick={onClose} disabled={pending}>
          Cancel
        </button>
        <button type="button" className="button--primary" onClick={onConfirm} disabled={pending}>
          {pending ? pendingLabel : confirmLabel}
        </button>
      </div>
    </Modal>
  )
}
