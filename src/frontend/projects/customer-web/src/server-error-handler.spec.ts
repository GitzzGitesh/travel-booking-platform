import type { NextFunction, Request, Response } from 'express';
import { serverErrorHandler } from './server-error-handler';

function fakeResponse(headersSent = false) {
  const sent: { status?: number; type?: string; body?: string } = {};
  const res = {
    headersSent,
    status(code: number) {
      sent.status = code;
      return this;
    },
    type(value: string) {
      sent.type = value;
      return this;
    },
    send(body: string) {
      sent.body = body;
      return this;
    },
  };
  return { res: res as unknown as Response, sent };
}

describe('serverErrorHandler', () => {
  const req = { method: 'GET', path: '/trips' } as Request;

  beforeEach(() => vi.spyOn(console, 'error').mockImplementation(() => undefined));
  afterEach(() => vi.restoreAllMocks());

  it('returns a generic 500 without error details', () => {
    const { res, sent } = fakeResponse();
    const next = vi.fn();

    serverErrorHandler(
      new Error('secret detail at /internal/path'),
      req,
      res,
      next as unknown as NextFunction,
    );

    expect(sent).toEqual({ status: 500, type: 'text/plain', body: 'Internal Server Error' });
    expect(next).not.toHaveBeenCalled();
  });

  it('logs the path without the query string', () => {
    const { res } = fakeResponse();

    serverErrorHandler(
      new TypeError('boom'),
      { method: 'GET', path: '/trips', url: '/trips?email=x' } as Request,
      res,
      vi.fn(),
    );

    expect(console.error).toHaveBeenCalledWith('SSR request failed: GET /trips: TypeError: boom');
  });

  it('delegates to Express when the response has already started', () => {
    const { res, sent } = fakeResponse(true);
    const next = vi.fn();
    const error = new Error('late');

    serverErrorHandler(error, req, res, next as unknown as NextFunction);

    expect(next).toHaveBeenCalledWith(error);
    expect(sent).toEqual({});
  });
});
