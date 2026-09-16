import { defineConfig } from '@playwright/test';
export default defineConfig({
  testDir: './tests/e2e', timeout: 90000, workers: 1,
  use: { baseURL: process.env.QUIRE_WEB_URL || 'http://127.0.0.1:5195', viewport: { width: 1440, height: 900 } },
  webServer: process.env.QUIRE_WEB_URL ? undefined : { command: 'npm run dev', url: 'http://127.0.0.1:5195', reuseExistingServer: !process.env.CI },
});
