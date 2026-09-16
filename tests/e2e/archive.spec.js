import { test, expect } from '@playwright/test';
import { readFile } from 'node:fs/promises';

async function register(context, suffix) {
  const email = `quire-${suffix}-${Date.now()}@example.test`;
  const response = await context.request.post('/api/v1/auth/register', { data: { email, password: 'test-only-long-password', displayName: suffix } });
  expect(response.ok()).toBeTruthy();
  const csrf = await (await context.request.get('/api/v1/auth/csrf')).json();
  return { email, headers: { [csrf.headerName]: csrf.token } };
}
async function decide(context, version, state, headers) {
  const response = await context.request.get(`/api/v1/versions/${version}`);
  return context.request.post(`/api/v1/versions/${version}/review`, { headers: { ...headers, 'If-Match': response.headers().etag }, data: { reviewState: state } });
}

test('backend failure is visible and never loads placeholder archive tiles', async ({ page }) => {
  await page.route('**/api/v1/archive?*', route => route.fulfill({ status: 503, body: '{}' }));
  await page.goto('/');
  await expect(page.getByRole('status')).toContainText('unavailable');
  await expect(page.locator('#archive-list li')).toHaveCount(0);
  await expect(page.locator('.hero canvas')).toHaveCount(0);
});

test('signed-out archive has a controlled message', async ({ page }) => {
  await page.goto('/');
  await expect(page.getByRole('status')).toContainText('Sign in');
  await expect(page.locator('#archive-list li')).toHaveCount(0);
});

test('real upload, processing, review, approval and delivery reach the approved archive', async ({ page, context, browser, baseURL }) => {
  const owner = await register(context, 'owner');
  const projectResponse = await context.request.post('/api/v1/projects', { headers: owner.headers, data: { name: 'Browser project', description: '' } });
  expect(projectResponse.ok()).toBeTruthy();
  const project = (await projectResponse.json()).ref;
  const bytes = await readFile(new URL('../../public/images/placeholder-01.jpg', import.meta.url));
  const upload = await context.request.post(`/api/v1/projects/${project}/assets`, { headers: { ...owner.headers, 'Idempotency-Key': `browser-${Date.now()}` }, multipart: { title: 'Browser asset', file: { name: 'frame.jpg', mimeType: 'image/jpeg', buffer: bytes } } });
  expect(upload.status()).toBe(201);
  const asset = await upload.json();
  const version = asset.versions[0].ref;
  await page.goto('/');
  await expect(page.locator(`#archive-${version}`)).toContainText('Browser asset');
  await expect.poll(async () => (await (await context.request.get(`/api/v1/versions/${version}`)).json()).processingState, { timeout: 60000 }).toBe('Succeeded');
  await expect(page.locator('.hero canvas')).toHaveCount(1, { timeout: 25000 });
  const reviewerContext = await browser.newContext({ baseURL });
  try {
    const reviewer = await register(reviewerContext, 'reviewer');
    expect((await context.request.post(`/api/v1/projects/${project}/members`, { headers: owner.headers, data: { email: reviewer.email, role: 'Reviewer' } })).ok()).toBeTruthy();
    expect((await decide(context, version, 'InReview', owner.headers)).ok()).toBeTruthy();
    const comment = await reviewerContext.request.post(`/api/v1/versions/${version}/comments`, { headers: reviewer.headers, data: { body: 'Review thread.' } });
    expect(comment.status()).toBe(201);
    const thread = await comment.json();
    expect((await decide(reviewerContext, version, 'Approved', reviewer.headers)).status()).toBe(422);
    expect((await reviewerContext.request.post(`/api/v1/comments/${thread.ref}/resolution`, { headers: reviewer.headers, data: { resolved: true } })).ok()).toBeTruthy();
    expect((await decide(context, version, 'Approved', owner.headers)).status()).toBe(422);
    expect((await decide(reviewerContext, version, 'Approved', reviewer.headers)).ok()).toBeTruthy();
    const snapshot = await context.request.get(`/api/v1/versions/${version}`);
    expect((await context.request.post(`/api/v1/versions/${version}/deliveries`, { headers: { ...owner.headers, 'If-Match': snapshot.headers().etag }, data: {} })).ok()).toBeTruthy();
    await expect(page.locator(`#archive-${version}`)).toContainText('Delivered', { timeout: 25000 });
    await expect(page.locator('.hero canvas')).toHaveCount(1);
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await page.setViewportSize({ width: 390, height: 844 });
    await page.reload();
    await expect(page.locator(`#archive-${version}`)).toContainText('Delivered');
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
  } finally { await reviewerContext.close(); }
});
