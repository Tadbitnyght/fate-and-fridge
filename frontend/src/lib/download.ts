import type { DownloadedFile } from '../api/client'

/**
 * Saves a downloaded file by pointing a temporary, hidden anchor at an object
 * URL. The URL is always revoked, so the blob is not retained in memory after
 * the browser starts the download.
 */
export function saveDownloadedFile(file: DownloadedFile): void {
  const url = URL.createObjectURL(file.blob)
  try {
    const anchor = document.createElement('a')
    anchor.href = url
    anchor.download = file.filename
    anchor.rel = 'noopener'
    anchor.style.display = 'none'
    document.body.appendChild(anchor)
    anchor.click()
    document.body.removeChild(anchor)
  } finally {
    URL.revokeObjectURL(url)
  }
}
