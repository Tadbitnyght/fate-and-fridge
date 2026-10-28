import { useEffect, useId, useRef } from 'react'
import { createPortal } from 'react-dom'

/**
 * Compact desktop affordance for a month-grid day.
 * The month grid is an ARIA grid whose `gridcell`s are themselves buttons with
 * roving-tabindex arrow navigation. Nesting an inline Reroll button inside
 * those cells would create invalid nested interactive elements and break the
 * grid semantics, so the grid exposes this small action menu instead
 * (right-click / `ContextMenu` key / `Shift+F10` on the focused cell). It is a
 * portal popover with a single Reroll item; Escape closes and returns focus to
 * the originating cell.
 */
export function DayActionMenu({
  dateLabel,
  rect,
  disabled,
  reason,
  onReroll,
  onClose,
  onRestoreFocus,
}: {
  dateLabel: string
  rect: DOMRect
  disabled: boolean
  reason: string | null
  onReroll: () => void
  onClose: () => void
  onRestoreFocus: () => void
}) {
  const menuRef = useRef<HTMLDivElement>(null)
  const itemRef = useRef<HTMLButtonElement>(null)
  const reasonId = useId()

  useEffect(() => {
    // A disabled item cannot receive focus, so focus the menu itself and let
    // its aria-describedby carry the explanation to assistive tech.
    if (disabled) {
      menuRef.current?.focus()
    } else {
      itemRef.current?.focus()
    }

    function handleKeyDown(event: KeyboardEvent): void {
      if (event.key === 'Escape') {
        event.stopPropagation()
        onClose()
        onRestoreFocus()
      }
    }

    function handlePointerDown(event: MouseEvent): void {
      if (menuRef.current && !menuRef.current.contains(event.target as Node)) {
        onClose()
      }
    }

    document.addEventListener('keydown', handleKeyDown)
    document.addEventListener('mousedown', handlePointerDown)
    return () => {
      document.removeEventListener('keydown', handleKeyDown)
      document.removeEventListener('mousedown', handlePointerDown)
    }
  }, [disabled, onClose, onRestoreFocus])

  const left = Math.max(8, Math.min(rect.left, window.innerWidth - 224))
  const top = Math.min(rect.bottom + 4, window.innerHeight - 72)

  return createPortal(
    <div
      ref={menuRef}
      className="day-action-menu"
      role="menu"
      aria-label={`Actions for ${dateLabel}`}
      aria-describedby={disabled && reason ? reasonId : undefined}
      tabIndex={-1}
      style={{ position: 'fixed', top, left }}
    >
      <button
        ref={itemRef}
        type="button"
        role="menuitem"
        disabled={disabled}
        title={reason ?? undefined}
        onClick={onReroll}
      >
        Reroll meal
      </button>
      {reason && (
        <p id={reasonId} className="day-action-menu__reason">
          {reason}
        </p>
      )}
    </div>,
    document.body,
  )
}
