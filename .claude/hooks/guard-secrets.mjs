#!/usr/bin/env node
// PreToolUse hook for Write/Edit/MultiEdit.
// Blocks writing likely secrets or secret-bearing files into the repository.
// Read-only: it inspects the tool payload from stdin and never runs commands.
// Exit 0 = allow, exit 2 = block (stderr is shown to Claude as the reason).

import { basename } from 'node:path';

// Patterns are assembled from parts so this file never matches itself.
const SECRET_PATTERNS = [
  { name: 'Stripe live secret/restricted key', re: new RegExp('\\b[sr]k' + '_live_[0-9A-Za-z]{16,}') },
  { name: 'Stripe test secret/restricted key', re: new RegExp('\\b[sr]k' + '_test_[0-9A-Za-z]{16,}') },
  { name: 'Stripe webhook signing secret', re: new RegExp('\\bwh' + 'sec_[0-9A-Za-z]{20,}') },
  { name: 'Private key block', re: new RegExp('-----BEGIN (?:RSA |EC |DSA |OPENSSH |ENCRYPTED )?PRIVATE ' + 'KEY-----') },
  { name: 'Azure storage account key', re: new RegExp('Account' + 'Key=[A-Za-z0-9+/]{40,}={0,2}') },
  { name: 'Azure SAS signature', re: new RegExp('[?&]si' + 'g=[A-Za-z0-9%+/]{30,}') },
  { name: 'AWS access key ID', re: new RegExp('\\bAK' + 'IA[0-9A-Z]{16}\\b') },
  { name: 'GitHub token', re: new RegExp('\\bgh[pousr]' + '_[A-Za-z0-9]{36,}') },
  { name: 'Slack token', re: new RegExp('\\bxox[abprs]' + '-[A-Za-z0-9-]{10,}') },
];

// "Password=..." / "Pwd=..." in connection strings, unless the value is an obvious placeholder.
const PASSWORD_RE = /\b(?:Password|Pwd)\s*=\s*([^;"'\s]+)/gi;
const PLACEHOLDER_RE = /^(?:[<{$%*]|__|\.\.\.|x{3,}$|changeme$|placeholder$|your[-_]?password)/i;

const BLOCKED_FILE_RE = [
  /^\.env$/i,
  /^\.env\.(?!example$|sample$|template$).+/i,
  /\.(?:pfx|p12|pem|key)$/i,
  /^secrets\.json$/i,
];

function collectText(input) {
  const parts = [];
  if (typeof input.content === 'string') parts.push(input.content);
  if (typeof input.new_string === 'string') parts.push(input.new_string);
  if (Array.isArray(input.edits)) {
    for (const e of input.edits) if (e && typeof e.new_string === 'string') parts.push(e.new_string);
  }
  return parts.join('\n');
}

function findProblems(filePath, text) {
  const problems = [];
  const name = basename(filePath || '');
  if (BLOCKED_FILE_RE.some((re) => re.test(name))) {
    problems.push(`writing '${name}' is blocked (secret/key file; use user-secrets or Key Vault)`);
  }
  for (const { name: label, re } of SECRET_PATTERNS) {
    if (re.test(text)) problems.push(`content looks like a ${label}`);
  }
  for (const m of text.matchAll(PASSWORD_RE)) {
    if (!PLACEHOLDER_RE.test(m[1])) {
      problems.push('content contains a connection-string password (use a placeholder such as <password> or user-secrets)');
      break;
    }
  }
  return problems;
}

let raw = '';
process.stdin.setEncoding('utf8');
process.stdin.on('data', (chunk) => (raw += chunk));
process.stdin.on('end', () => {
  let payload;
  try {
    payload = JSON.parse(raw);
  } catch {
    process.exit(0); // Unparseable payload: fail open rather than block all edits.
  }
  const input = payload?.tool_input ?? {};
  const problems = findProblems(input.file_path, collectText(input));
  if (problems.length > 0) {
    process.stderr.write(
      `Blocked by .claude/hooks/guard-secrets.mjs for ${input.file_path ?? '(unknown file)'}:\n` +
        problems.map((p) => `  - ${p}`).join('\n') +
        '\nSecrets belong in user-secrets (local) or Azure Key Vault. See .claude/rules/security.md.\n',
    );
    process.exit(2);
  }
  process.exit(0);
});
