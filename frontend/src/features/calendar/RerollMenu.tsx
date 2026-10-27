import { useEffect, useRef, useState, type KeyboardEvent } from 'react'
import { createPortal } from 'react-dom'

import type { BatchRerollMode } from '../../api/calendar'
import { BATCH_MODE_LABELS, BATCH_MODE_ORDER } from './batchModes'

const MODE_ORDER = BATCH_MODE_ORDER

/**
 * "Reroll…" toolbar menu (M7-09). A single trigger opens a portal popover with
 * the three batch ranges (7 days / Rest of month / Fill empty); choosing one
 * hands the mode to the page, which opens the preview wizard. It is a plain
 * button + menu so it works identically on desktop and mobile, is reachable by
 * keyboard (Enter/Space, Arrow keys, Escape), and is rendered only for actors
 * with `calendar.edit` (the page owns that visibility check).
 */
export function RerollMenu({ onChoose }: { onChoose: (mode: BatchRerollMode) => void }) {
  const [open, setOpen] = useState(false)
  const [rect, setRect] = useState<DOMRect | null>(null)
  const buttonRef = useRef<HTMLButtonElement>(null)
  const menuRef = useRef<HTMLDivElement>(null)
  const itemRefs = useRef(new Map<BatchRerollMode, HTMLButtonElement>())

  function openMenu(): void {
    const node = buttonRef.current
    if (!node) {
      return
    }
    setRect(node.getBoundingClientRect())
    setOpen(true)
  }

  function dismiss(restoreFocus: boolean): void {
    setOpen(false)
    if (restoreFocus) {
      buttonRef.current?.focus()
    }
  }

  useEffect(() => {
    if (!open) {
      return
    }

    itemRefs.current.get(MODE_ORDER[0])?.focus()

    function handlePointerDown(event: MouseEvent): void {
      const target = event.target as Node
      if (menuRef.current && !menuRef.current.contains(target)) {
        setOpen(false)
      }
    }

    document.addEventListener('mousedown', handlePointerDown)
    return () => document.removeEventListener('mousedown', handlePointerDown)
  }, [open])

  function handleMenuKeyDown(event: KeyboardEvent<HTMLDivElement>): void {
    if (event.key === 'Escape') {
      event.stopPropagation()
      dismiss(true)
      return
    }
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault()
      const current = MODE_ORDER.findIndex(
        (mode) => itemRefs.current.get(mode) === document.activeElement,
      )
      const delta = event.key === 'ArrowDown' ? 1 : -1
      const next = MODE_ORDER[(current + delta + MODE_ORDER.length) % MODE_ORDER.length]
      itemRefs.current.get(next)?.focus()
    }
  }

  const left = rect ? Math.max(8, Math.min(rect.left, window.innerWidth - 200)) : 8
  const top = rect ? Math.min(rect.bottom + 4, window.innerHeight - 190) : 8

  return (
    <div className="reroll-menu">
      <button
        ref={buttonRef}
        type="button"
        className="reroll-menu__trigger"
        aria-haspopup="menu"
        aria-expanded={open}
        onClick={() => (open ? dismiss(false) : openMenu())}
      >
        Reroll…
      </button>
      {open &&
        rect &&
        createPortal(
          <div
            ref={menuRef}
            className="reroll-menu__popover"
            role="menu"
            aria-label="Batch reroll options"
            onKeyDown={handleMenuKeyDown}
            style={{ position: 'fixed', top, left }}
          >
            <p className="reroll-menu__heading">Reroll range</p>
            {MODE_ORDER.map((mode) => (
              <button
                key={mode}
                ref={(node) => {
                  if (node) {
                    itemRefs.current.set(mode, node)
                  } else {
                    itemRefs.current.delete(mode)
                  }
                }}
                type="button"
                role="menuitem"
                onClick={() => {
                  dismiss(false)
                  onChoose(mode)
                }}
              >
                {BATCH_MODE_LABELS[mode]}
              </button>
            ))}
          </div>,
          document.body,
        )}
    </div>
  )
}
