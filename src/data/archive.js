import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr';

export const ARCHIVE_SOURCE = 'api';
export const REVIEW_LABELS = { pending: 'Awaiting review', in_review: 'In review', changes_requested: 'Changes requested', approved: 'Approved', delivered: 'Delivered' };
export const PROCESSING_LABELS = { uploaded: 'Uploaded', quarantined: 'Quarantined', validating: 'Awaiting validation', processing: 'Processing', ready: 'Ready', failed: 'Processing failed' };

export function mapRecord(row) {
  if (!row || typeof row.id !== 'string' || typeof row.project !== 'string' || typeof row.asset !== 'string' || !Number.isInteger(row.version))
    throw new Error('The archive response is invalid.');
  const processing = row.lifecycle === 'Quarantined' ? 'quarantined' :
    ({ Pending: 'uploaded', Queued: 'validating', Processing: 'processing', Succeeded: 'ready', Failed: 'failed', DeadLettered: 'failed' })[row.processing];
  const review = ({ Draft: 'pending', InReview: 'in_review', ChangesRequested: 'changes_requested', Approved: 'approved' })[row.review];
  if (!processing || !review) throw new Error('The archive contains an unknown state.');
  // Only same-origin, authorised derivative URLs may enter the WebGL loader.
  const thumbnail = processing === 'ready' && row.thumbnail === `/api/v1/versions/${encodeURIComponent(row.id)}/content?derivative=thumbnail` ? row.thumbnail : null;
  return { ...row, processing, review: row.delivery === 'delivered' ? 'delivered' : review, thumbnail };
}

export async function loadArchive({ fetcher = fetch, signal } = {}) {
  const records = [];
  let cursor = null;
  const seen = new Set();
  do {
    const response = await fetcher(`/api/v1/archive?limit=100${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ''}`, {
      credentials: 'same-origin', cache: 'no-store', signal: signal ?? AbortSignal.timeout(10000),
    });
    if (!response.ok) {
      const error = new Error(response.status === 401 ? 'Sign in to view your project archive.' : 'The archive is unavailable. Retrying shortly.');
      error.status = response.status;
      throw error;
    }
    const page = await response.json();
    if (!Array.isArray(page.items)) throw new Error('The archive response is invalid.');
    records.push(...page.items.map(mapRecord));
    cursor = page.nextCursor;
    if (cursor && (seen.has(cursor) || records.length >= 2000)) throw new Error('The archive exceeds the viewer limit.');
    if (cursor) seen.add(cursor);
  } while (cursor);
  return [...new Map(records.map(row => [row.id, row])).values()];
}

// Events are invalidation hints. Polling also repairs missed events and reconnect gaps.
export function subscribeArchive(onChange, onError = () => {}) {
  let disposed = false;
  let busy = false;
  let pending = false;
  let joined = new Set();
  const connection = new HubConnectionBuilder().withUrl('/hubs/collaboration')
    .withAutomaticReconnect([0, 2000, 5000, 15000]).configureLogging(LogLevel.None).build();
  const controller = new AbortController();
  async function refresh() {
    if (disposed) return;
    if (busy) { pending = true; return; }
    busy = true;
    try {
      const rows = await loadArchive({ signal: AbortSignal.any([controller.signal, AbortSignal.timeout(15000)]) });
      if (disposed) return;
      onChange(rows);
      if (connection.state === 'Disconnected') {
        try { await connection.start(); } catch { /* Polling remains authoritative. */ }
      }
      if (connection.state === 'Connected') {
        try {
          const projects = new Set(rows.map(row => row.projectRef));
          for (const ref of joined) if (!projects.has(ref)) await connection.invoke('UnsubscribeFromProject', ref);
          for (const ref of projects) if (!joined.has(ref)) await connection.invoke('SubscribeToProject', ref);
          joined = projects;
        } catch { joined = new Set(); /* Reconcile subscriptions on the next HTTP refresh. */ }
      }
    } catch (error) {
      if (!disposed && error.name !== 'AbortError') onError(error.name === 'TypeError' || error.name === 'TimeoutError' ? new Error('The archive is unavailable. Retrying shortly.') : error);
    } finally {
      busy = false;
      if (pending && !disposed) { pending = false; queueMicrotask(refresh); }
    }
  }
  connection.on('ProjectEvent', refresh);
  connection.onreconnected(() => { joined = new Set(); refresh(); });
  connection.onclose(() => { joined = new Set(); });
  const interval = setInterval(refresh, 15000);
  refresh();
  return () => { disposed = true; clearInterval(interval); controller.abort(); void connection.stop(); };
}
