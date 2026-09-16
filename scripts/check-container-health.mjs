import { execFileSync } from 'node:child_process';
const rows = execFileSync('docker', ['compose', 'ps', '--all', '--format', 'json'], { encoding: 'utf8' });
const containers = rows.trim().split('\n').flatMap(row => JSON.parse(row));
for (const service of ['postgres', 'rabbitmq', 'api', 'worker', 'frontend']) {
  const container = containers.find(c => c.Service === service);
  if (container?.State !== 'running' || container.Health !== 'healthy') throw new Error(`${service} is not healthy`);
}
for (const service of ['migrate', 'storage-init']) {
  const container = containers.find(c => c.Service === service);
  if (container?.State !== 'exited' || container.ExitCode !== 0) throw new Error(`${service} did not finish successfully`);
}
for (const endpoint of ['/api/v1/health/live', '/api/v1/health/ready']) {
  const response = await fetch(`http://127.0.0.1:5195${endpoint}`);
  if (!response.ok) throw new Error(`${endpoint}: ${response.status}`);
}
console.log('All runtime containers healthy; migrations and storage setup completed.');
