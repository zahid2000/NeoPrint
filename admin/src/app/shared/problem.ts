import { HttpErrorResponse } from '@angular/common/http';

import { describeError } from '../features/login/login.component';

/** A failure as a page shows it: what went wrong, and the id support needs. */
export interface Problem {
  /** A translation key or the server's own sentence. */
  readonly message: string;

  /** The request's correlation id, for the "Ətraflı" part (docs/DESIGN_SYSTEM.md section 4). */
  readonly correlationId?: string;
}

export function toProblem(error: unknown): Problem {
  const body = error instanceof HttpErrorResponse
    ? (error.error as { correlationId?: string } | null | undefined)
    : undefined;

  return { message: describeError(error), correlationId: body?.correlationId };
}
