import test from 'node:test';
import assert from 'node:assert/strict';
import { loadArchive, mapRecord } from '../../src/data/archive.js';
const row = { id: 'ver_a', project: 'Project', projectRef: 'prj_a', asset: 'Asset', version: 1, lifecycle: 'Ready', processing: 'Succeeded', review: 'Approved', delivery: 'pending', thumbnail: '/api/v1/versions/ver_a/content?derivative=thumbnail' };
test('ready records use the authorised derivative', () => assert.equal(mapRecord(row).thumbnail, row.thumbnail));
test('quarantined records can never advertise a preview or success', () => {
  const result = mapRecord({ ...row, lifecycle: 'Quarantined', processing: 'DeadLettered' });
  assert.equal(result.processing, 'quarantined'); assert.equal(result.thumbnail, null);
});
test('delivery is mapped from durable state', () => assert.equal(mapRecord({ ...row, delivery: 'delivered' }).review, 'delivered'));
test('arbitrary preview URLs are refused', () => assert.equal(mapRecord({ ...row, thumbnail: 'https://untrusted.test/image.jpg' }).thumbnail, null));
test('unknown states fail visibly', () => assert.throws(() => mapRecord({ ...row, processing: 'Magic' })));
test('backend errors cannot produce demo success', async () => {
  await assert.rejects(loadArchive({ fetcher: async () => ({ ok: false, status: 503 }) }), /unavailable/);
  await assert.rejects(loadArchive({ fetcher: async () => ({ ok: false, status: 401 }) }), /Sign in/);
});
test('pagination loads and deduplicates actual version records', async () => {
  let call = 0;
  const result = await loadArchive({ fetcher: async () => ({ ok: true, json: async () => ({ items: [row], nextCursor: call++ === 0 ? 'next' : null }) }) });
  assert.equal(call, 2); assert.equal(result.length, 1);
});
test('a repeated pagination cursor is refused', async () => {
  await assert.rejects(loadArchive({ fetcher: async () => ({ ok: true, json: async () => ({ items: [row], nextCursor: 'same' }) }) }), /limit/);
});
