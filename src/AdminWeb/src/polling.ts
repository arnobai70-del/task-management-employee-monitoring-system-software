// Shared timing for background polls. Every Admin Web tab used to poll at a fixed rate, also while
// hidden and while the server answered 429/5xx, so many open tabs kept load on a struggling server.
// Polls now skip hidden tabs (and catch up as soon as the tab is visible again) and back off while
// the API is failing: the interval doubles per consecutive failure, up to 16x but never past 1 minute
// (or the poll's own interval if that is longer), and a Retry-After header is honoured. One successful
// API response resets the backoff for every poll.

const MAX_BACKOFF_STEPS = 4;
const MAX_DELAY_MS = 60_000;

let consecutiveFailures = 0;
let pausedUntil = 0;

function parseRetryAfter(value: string | null, now: number): number {
  if (!value) return 0;
  const seconds = Number(value);
  if (Number.isFinite(seconds)) return now + Math.max(0, seconds) * 1000;
  const date = Date.parse(value);
  return Number.isNaN(date) ? 0 : date;
}

/** Records an API response so background polls can back off while the server is struggling. */
export function recordServerResponse(status: number, retryAfter: string | null, now = Date.now()): void {
  if (status === 429 || status >= 500) {
    consecutiveFailures += 1;
    pausedUntil = Math.max(pausedUntil, parseRetryAfter(retryAfter, now));
    return;
  }
  consecutiveFailures = 0;
  pausedUntil = 0;
}

/** Records a request that never reached the server (offline, DNS, connection reset). */
export function recordServerUnreachable(): void {
  consecutiveFailures += 1;
}

/** Delay before the next background poll for a poll whose normal interval is intervalMs. */
export function nextPollDelay(intervalMs: number, now = Date.now()): number {
  const backoff = intervalMs * 2 ** Math.min(consecutiveFailures, MAX_BACKOFF_STEPS);
  const delay = Math.min(backoff, Math.max(intervalMs, MAX_DELAY_MS));
  return Math.max(delay, pausedUntil - now, intervalMs);
}

/**
 * Runs poll every intervalMs (adjusted by nextPollDelay) while the tab is visible. A tick that falls
 * in a hidden period is skipped and runs as soon as the tab becomes visible. Returns the stop function.
 */
export function startPolling(poll: () => void, intervalMs: number): () => void {
  let timer: number | undefined;
  let stopped = false;
  let waitingForVisibility = false;

  const schedule = () => {
    if (stopped) return;
    timer = window.setTimeout(tick, nextPollDelay(intervalMs));
  };

  const tick = () => {
    timer = undefined;
    if (stopped) return;
    if (document.hidden) {
      waitingForVisibility = true;
      return;
    }
    poll();
    schedule();
  };

  const onVisibilityChange = () => {
    if (!document.hidden && waitingForVisibility) {
      waitingForVisibility = false;
      tick();
    }
  };

  document.addEventListener('visibilitychange', onVisibilityChange);
  schedule();

  return () => {
    stopped = true;
    if (timer !== undefined) window.clearTimeout(timer);
    document.removeEventListener('visibilitychange', onVisibilityChange);
  };
}
