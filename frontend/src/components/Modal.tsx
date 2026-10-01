import { useEffect, useId, useRef, type ReactNode } from 'react'
import { createPortal } from 'react-dom'

const FOCUSABLE_SELECTOR = [
  'a[href]',
  'button:not([disabled])',
  'input:not([disabled])',
  'select:not([disabled])',
  'textarea:not([disabled])',
  '[tabindex]:not([tabindex="-1"])',
].join(', ')

function focusableWithin(panel: HTMLElement): HTMLElement[] {
  return Array.from(panel.querySelectorAll<HTMLElement>(FOCUSABLE_SELECTOR)).filter(
    (element) => element.tabIndex >= 0,
  )
}

/**
 * Accessible modal: rendered in a portal directly under `document.body`,
 * `role="dialog"` + `aria-modal`, Escape closes, focus moves into the panel on
 * open and returns to the trigger on close. The rest of the page is marked
 * `inert` (AT- and pointer-inert) while the dialog is open, and Tab/Shift+Tab
 * cycle within the panel.
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
  const backdropRef = useRef<HTMLDivElement>(null)
  const titleId = useId()
  const onCloseRef = useRef(onClose)

  useEffect(() => {
    onCloseRef.current = onClose
  })

  useEffect(() => {
    const previous = document.activeElement instanceof HTMLElement ? document.activeElement : null
    panelRef.current?.focus()

    // Inert every body child except this dialog's portal node, so background
    // content is unreachable by pointer, keyboard, and assistive technology.
    const backdrop = backdropRef.current
    const inerted: HTMLElement[] = []
    for (const child of Array.from(document.body.children)) {
      if (child instanceof HTMLElement && child !== backdrop) {
        child.setAttribute('inert', '')
        inerted.push(child)
      }
    }

    function handleKeyDown(event: KeyboardEvent): void {
      if (event.key === 'Escape') {
        onCloseRef.current()
        return
      }

      if (event.key !== 'Tab') {
        return
      }

      const panel = panelRef.current
      if (!panel) {
        return
      }

      const focusable = focusableWithin(panel)
      if (focusable.length === 0) {
        event.preventDefault()
        panel.focus()
        return
      }

      const first = focusable[0]
      const last = focusable[focusable.length - 1]
      const active = document.activeElement

      if (event.shiftKey && (active === first || active === panel || active === null)) {
        event.preventDefault()
        last.focus()
      } else if (!event.shiftKey && active === last) {
        event.preventDefault()
        first.focus()
      }
    }

    document.addEventListener('keydown', handleKeyDown)
    return () => {
      document.removeEventListener('keydown', handleKeyDown)
      for (const element of inerted) {
        element.removeAttribute('inert')
      }
      previous?.focus()
    }
  }, [])

  return createPortal(
    <div
      className="modal-backdrop"
      ref={backdropRef}
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
    </div>,
    document.body,
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
