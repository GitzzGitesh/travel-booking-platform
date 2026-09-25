// Fails if one Angular app imports another. Apps share code only through libraries (ADR 0009).
// An app is a project with src/main.ts; libraries such as api-client are shared and allowed.
import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { dirname, join, relative, resolve, sep } from 'node:path';

const projectsDir = resolve('projects');
const apps = readdirSync(projectsDir).filter((name) =>
  existsSync(join(projectsDir, name, 'src', 'main.ts')),
);

const sourceFiles = (dir) =>
  readdirSync(dir).flatMap((name) => {
    const path = join(dir, name);
    return statSync(path).isDirectory()
      ? sourceFiles(path)
      : /\.(ts|mts|html|css)$/.test(name)
        ? [path]
        : [];
  });

const violations = [];
for (const app of apps) {
  for (const file of sourceFiles(join(projectsDir, app, 'src'))) {
    const specifiers = [
      ...readFileSync(file, 'utf8').matchAll(
        /(?:from\s+|import\s*\(\s*|@import\s+|url\(\s*)['"]([^'"]+)['"]/g,
      ),
    ].map((m) => m[1]);
    for (const specifier of specifiers.filter((s) => s.startsWith('.'))) {
      const target = relative(projectsDir, resolve(dirname(file), specifier)).split(sep)[0];
      if (apps.includes(target) && target !== app) {
        violations.push(`${relative(projectsDir, file)} imports ${specifier} (app '${target}')`);
      }
    }
  }
}

if (violations.length > 0) {
  console.error(`Apps must not import each other (ADR 0009):\n- ${violations.join('\n- ')}`);
  process.exit(1);
}
console.log(`App boundaries OK: ${apps.join(', ')} do not import each other.`);
