// Runs after `vite build`. Fails the build when the output would break the server's Content-Security-Policy or caching rules (ADR 0056):
// the page must load its scripts and styles from files of this origin only, and every asset must carry a content hash in its name.
import { readdirSync, readFileSync } from 'node:fs';
import { join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const dist = fileURLToPath(new URL('../dist/', import.meta.url));
const base = '/console/';
const problems = [];

const walk = (dir) =>
  readdirSync(dir, { withFileTypes: true }).flatMap((e) =>
    e.isDirectory() ? walk(join(dir, e.name)) : [join(dir, e.name)],
  );
const files = walk(dist).map((f) => relative(dist, f).replaceAll('\\', '/'));

const html = readFileSync(join(dist, 'index.html'), 'utf8');
for (const tag of html.match(/<script\b[^>]*>/gi) ?? [])
  if (!/\ssrc="/i.test(tag)) problems.push(`inline script: ${tag}`);
if (/<style\b/i.test(html)) problems.push('index.html has a <style> element');
if (/\sstyle\s*=/i.test(html)) problems.push('index.html has a style attribute');
if (/\son\w+\s*=/i.test(html)) problems.push('index.html has an event handler attribute');
if (/javascript:/i.test(html)) problems.push('index.html has a javascript: URL');
for (const [, url] of html.matchAll(/\s(?:src|href)="([^"]*)"/gi))
  if (!url.startsWith(base) && !url.startsWith('data:'))
    problems.push(`index.html names ${url}, not a file under ${base}`);

for (const file of files) {
  if (file === 'index.html') continue;
  if (file.endsWith('.map')) problems.push(`${file}: a source map is not shipped`);
  else if (!file.startsWith('assets/') || !/-[\w-]{8,}\.[a-z0-9]+$/i.test(file))
    problems.push(`${file}: not a hashed file under assets/, so it cannot be cached for a year`);
}
if (!files.some((f) => f.endsWith('.js'))) problems.push('no script was built');

if (problems.length > 0) {
  console.error(`verify-dist: ${problems.length} problem(s)\n- ${problems.join('\n- ')}`);
  process.exit(1);
}
console.log(`verify-dist: ${files.length} files, no inline script or style, every asset hashed`);
