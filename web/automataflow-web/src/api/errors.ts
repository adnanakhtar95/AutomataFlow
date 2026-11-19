/** One validation error as the Api reports it (ServiceError: path, code, message; never input values). */
export interface FieldError {
  path: string | null;
  code: string;
  message: string;
}

/**
 * An Api failure, normalized from RFC 7807 ProblemDetails (the Api's only error shape). Messages shown to users come
 * from the Api's titles and error codes; nothing is re-derived in the browser.
 */
export class ApiError extends Error {
  readonly status: number;
  readonly title: string;
  readonly detail: string | null;
  readonly errors: FieldError[];

  constructor(status: number, title: string, detail: string | null = null, errors: FieldError[] = []) {
    super(title);
    this.name = 'ApiError';
    this.status = status;
    this.title = title;
    this.detail = detail;
    this.errors = errors;
  }

  get isUnauthorized(): boolean {
    return this.status === 401;
  }

  /** Authenticated but not allowed: a role policy, or the caller is no longer a member of the workspace. */
  get isForbidden(): boolean {
    return this.status === 403;
  }

  get isNotFound(): boolean {
    return this.status === 404;
  }

  get isRateLimited(): boolean {
    return this.status === 429;
  }
}

const fallbackTitles: Record<number, string> = {
  400: 'The request is invalid.',
  401: 'Your session has ended. Please sign in again.',
  403: 'You do not have access to this.',
  404: 'Not found.',
  409: 'The request conflicts with the current state.',
  413: 'The file is too large.',
  415: 'This file type is not supported.',
  423: 'The account is temporarily locked.',
  429: 'Too many attempts. Please wait a minute and try again.',
};

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function toFieldErrors(value: unknown): FieldError[] {
  if (!Array.isArray(value)) {
    return [];
  }

  return value.filter(isRecord).map((e) => ({
    path: typeof e.path === 'string' ? e.path : null,
    code: typeof e.code === 'string' ? e.code : 'error',
    message: typeof e.message === 'string' ? e.message : '',
  }));
}

/** Builds an ApiError from a response status and its (possibly empty or non-JSON) body. */
export function toApiError(status: number, body: unknown): ApiError {
  const problem = isRecord(body) ? body : {};
  const title = typeof problem.title === 'string' && problem.title.length > 0 ? problem.title : (fallbackTitles[status] ?? `Request failed (${status}).`);
  const detail = typeof problem.detail === 'string' && problem.detail.length > 0 ? problem.detail : null;
  return new ApiError(status, title, detail, toFieldErrors(problem.errors));
}

/** A network-level failure (the Api was unreachable): shown as an error state, never as success. */
export function networkError(): ApiError {
  return new ApiError(0, 'The AutomataFlow service is unreachable. Check your connection and try again.');
}
