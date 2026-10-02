import { HttpErrorResponse } from '@angular/common/http';

/** The stable Problem Details type of a refused staff API call (api-design rules), or null. */
export function problemType(error: unknown): string | null {
  if (error instanceof HttpErrorResponse && error.error && typeof error.error === 'object') {
    const type = (error.error as { type?: unknown }).type;
    return typeof type === 'string' ? type : null;
  }
  return null;
}

/** A Problem Details extension value (e.g. the current status after a 409), or null. */
export function problemExtension(error: unknown, name: string): string | null {
  if (error instanceof HttpErrorResponse && error.error && typeof error.error === 'object') {
    const value = (error.error as Record<string, unknown>)[name];
    return typeof value === 'string' ? value : null;
  }
  return null;
}

/** Messages for the problem types staff actions share; the caller adds its own. */
export function describeProblem(error: unknown, specific: Record<string, string> = {}): string {
  const type = problemType(error);
  const known: Record<string, string> = {
    'invalid-request': 'The server refused the request: check the ticket reference.',
    'concurrency-conflict': 'It changed at the same time. Reload and check again.',
    'rate-limited': 'Too many requests. Wait a minute and try again.',
    ...specific,
  };
  if (type && known[type]) {
    return known[type];
  }
  if (error instanceof HttpErrorResponse && error.status === 403) {
    return 'Your account may not do this.';
  }
  return 'It did not work. Try again shortly.';
}
