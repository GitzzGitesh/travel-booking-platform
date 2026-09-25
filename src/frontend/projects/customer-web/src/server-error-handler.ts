import type { ErrorRequestHandler } from 'express';

/**
 * Final error handler for the SSR server. Express's default handler renders the stack trace unless
 * NODE_ENV is 'production'; this one never exposes error details, whatever the environment.
 * It logs the error name and message with the request path only: the query string may carry personal data.
 */
export const serverErrorHandler: ErrorRequestHandler = (error, req, res, next) => {
  const name = error instanceof Error ? error.name : typeof error;
  const message = error instanceof Error ? error.message : '';
  console.error(`SSR request failed: ${req.method} ${req.path}: ${name}: ${message}`);

  if (res.headersSent) {
    // Streaming already started: let Express close the connection.
    next(error);
    return;
  }

  res.status(500).type('text/plain').send('Internal Server Error');
};
