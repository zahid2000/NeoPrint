import { HttpErrorResponse } from '@angular/common/http';

import { toProblem } from './problem';

describe('toProblem', () => {
  it('says the server broke only when it did', () => {
    expect(toProblem(new HttpErrorResponse({ status: 500 })).message).toBe('error.server');
  });

  it('says to wait, not that the server broke, when too many requests were sent (T-306)', () => {
    expect(toProblem(new HttpErrorResponse({ status: 429 })).message).toBe('error.tooManyRequests');
  });

  it("prefers the server's own sentence, which says how long to wait", () => {
    const problem = toProblem(
      new HttpErrorResponse({
        status: 429,
        error: { detail: '30 saniyədən sonra yenidən cəhd edin.', correlationId: 'abc' },
      }),
    );

    expect(problem.message).toBe('30 saniyədən sonra yenidən cəhd edin.');
    expect(problem.correlationId).toBe('abc');
  });

  it('says the network is down when nothing came back', () => {
    expect(toProblem(new HttpErrorResponse({ status: 0 })).message).toBe('error.network');
  });
});
