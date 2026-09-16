// ─────────────────────────────────────────────────────────────────────────
// Quire — the archive list (Chapter III).
//
// The accessible, non-WebGL reading of the same versions the spiral shows.
// Rendered from ArchiveRecord[] so both surfaces describe the same archive.
// ─────────────────────────────────────────────────────────────────────────

import { PROCESSING_LABELS, REVIEW_LABELS } from '../data/archive.js';

const dateFormat = new Intl.DateTimeFormat('en-GB', { day: '2-digit', month: 'short', year: 'numeric' });

function formatDate(iso) {
  const date = new Date(iso.length === 10 ? `${iso}T00:00:00` : iso);
  return Number.isNaN(date.getTime()) ? iso : dateFormat.format(date);
}

function stateFor(record) {
  // A version that has not finished processing cannot be reviewed yet, so the
  // processing state is the one worth reading.
  if (record.processing !== 'ready') {
    return { key: record.processing, label: PROCESSING_LABELS[record.processing] ?? record.processing };
  }
  return { key: record.review, label: REVIEW_LABELS[record.review] ?? record.review };
}

function renderRow(record, index) {
  const row = document.createElement('li');
  row.className = 'list__row field__row reveal-text';
  row.id = `archive-${record.id}`;
  row.dataset.recordId = record.id;

  const idx = document.createElement('span');
  idx.className = 'field__index';
  idx.textContent = String(index + 1).padStart(3, '0');

  const name = document.createElement('span');
  name.className = 'field__name';
  name.textContent = record.asset;

  const cat = document.createElement('span');
  cat.className = 'field__cat';
  const project = document.createElement('span');
  project.textContent = `${record.project} · v${String(record.version).padStart(2, '0')} · `;
  const state = stateFor(record);
  const stateEl = document.createElement('span');
  stateEl.className = 'field__state';
  stateEl.dataset.state = state.key;
  stateEl.textContent = state.label;
  cat.append(project, stateEl);

  const date = document.createElement('span');
  date.className = 'field__year';
  date.textContent = formatDate(record.updatedAt);

  row.append(idx, name, cat, date);
  return row;
}

/**
 * Replaces the contents of `host` with one row per record.
 *
 * @param {HTMLElement} host
 * @param {import('../data/archive.js').ArchiveRecord[]} records
 * @returns {HTMLElement[]} the rendered rows, in order
 */
export function renderArchiveList(host, records) {
  const rows = records.map(renderRow);
  host.replaceChildren(...rows);
  return rows;
}
