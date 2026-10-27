import {
  HubConnectionBuilder,
  HttpTransportType,
  LogLevel,
  type HubConnection,
  type IRetryPolicy,
  type RetryContext,
} from '@microsoft/signalr'

/** Hub path (docs/M9_PLAN.md §1.2). */
export const LIVE_SYNC_HUB_URL = '/hub/live'

/** Backoff ceiling for reconnects and initial-connect retries. */
export const LIVE_SYNC_MAX_RETRY_DELAY_MS = 30_000

/** Exponential backoff (1s, 2s, 4s … capped at 30s), retrying forever. */
export function retryDelayForAttempt(previousRetryCount: number): number {
  return Math.min(1000 * 2 ** Math.max(previousRetryCount - 1, 0), LIVE_SYNC_MAX_RETRY_DELAY_MS)
}

const reconnectPolicy: IRetryPolicy = {
  nextRetryDelayInMilliseconds: (context: RetryContext) =>
    retryDelayForAttempt(context.previousRetryCount + 1),
}

/** Builds the one-per-tab hub connection: WebSockets only, no negotiation. */
export function buildLiveSyncConnection(): HubConnection {
  return new HubConnectionBuilder()
    .withUrl(LIVE_SYNC_HUB_URL, {
      skipNegotiation: true,
      transport: HttpTransportType.WebSockets,
    })
    .withAutomaticReconnect(reconnectPolicy)
    .configureLogging(LogLevel.Warning)
    .build()
}

/**
 * The SignalR client resolves the relative hub URL through the DOM and throws
 * outside a real browser (jsdom unit tests, SSR). Live sync then simply stays
 * offline instead of breaking the page.
 */
export function safeBuildLiveSyncConnection(): HubConnection | null {
  try {
    return buildLiveSyncConnection()
  } catch {
    return null
  }
}
