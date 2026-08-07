import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('..', import.meta.url));
const required = [
  'index.html',
  'package.json',
  'tsconfig.json',
  'src/main.ts',
  'src/store.ts',
  'src/styles.css',
  'public/manifest.webmanifest',
  'public/sw.js',
  'static/index.html',
];
const missing = required.filter((file) => !existsSync(join(root, file)));
if (missing.length) {
  console.error(`Missing web prototype files: ${missing.join(', ')}`);
  process.exit(1);
}
const manifest = JSON.parse(readFileSync(join(root, 'public/manifest.webmanifest'), 'utf8'));
for (const key of ['name', 'short_name', 'start_url', 'display']) {
  if (!manifest[key]) throw new Error(`Manifest field ${key} is required`);
}
if (!readFileSync(join(root, 'src/main.ts'), 'utf8').includes('createMockStore')) {
  throw new Error('The SPA must use the mock store for backend-independent preview');
}
console.log(`Static checks passed (${required.length} required files, no backend dependency).`);
