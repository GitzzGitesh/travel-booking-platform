// Minimal static server for a built SPA, used only by the E2E tests. It sends a strict CSP so the tests prove the
// app runs without inline scripts or eval (ADR 0009). The production CSP header belongs to the hosting story. The
// style-src 'unsafe-inline' here covers Angular's runtime component styles until that story decides on nonces.
import { createReadStream, existsSync, statSync } from 'node:fs';
import { createServer } from 'node:http';
import { extname, join, normalize, resolve, sep } from 'node:path';

const root = resolve(process.argv[2]);
const port = Number(process.argv[3]);
const types = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.ico': 'image/x-icon',
  '.json': 'application/json',
};
const strictCsp = [
  "default-src 'self'",
  "script-src 'self'",
  "style-src 'self' 'unsafe-inline'",
  "object-src 'none'",
  "base-uri 'self'",
  "frame-ancestors 'none'",
].join('; ');

createServer((req, res) => {
  const path = normalize(decodeURIComponent(new URL(req.url ?? '/', 'http://localhost').pathname));
  let file = join(root, path);
  if (!file.startsWith(root + sep) || !existsSync(file) || statSync(file).isDirectory()) {
    file = join(root, 'index.html'); // SPA fallback: the Angular router handles unknown paths.
  }
  res.writeHead(200, {
    'Content-Type': types[extname(file)] ?? 'application/octet-stream',
    'Content-Security-Policy': strictCsp,
    'X-Content-Type-Options': 'nosniff',
  });
  createReadStream(file).pipe(res);
}).listen(port, () => console.log(`Serving ${root} on http://localhost:${port}`));
