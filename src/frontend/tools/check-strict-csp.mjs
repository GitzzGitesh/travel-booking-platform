// Fails if a built index.html would break a strict script-src (no 'unsafe-inline'): inline <script> blocks,
// inline event-handler attributes, or javascript: URLs (ADR 0009). Styles are out of scope: Angular adds
// component <style> elements at runtime, which need style-src 'unsafe-inline' or a nonce.
import { readFileSync } from 'node:fs';

const file = process.argv[2];
const html = readFileSync(file, 'utf8');

const violations = [
  ...[...html.matchAll(/<script\b(?![^>]*\bsrc=)[^>]*>/gi)].map((m) => `inline script: ${m[0]}`),
  ...[...html.matchAll(/\son[a-z]+\s*=/gi)].map((m) => `inline event handler:${m[0]}`),
  ...[...html.matchAll(/javascript:/gi)].map(() => 'javascript: URL'),
];

if (violations.length > 0) {
  console.error(`${file} is not strict-CSP compatible:\n- ${violations.join('\n- ')}`);
  process.exit(1);
}
console.log(`${file}: compatible with a strict script-src (no inline scripts or event handlers).`);
