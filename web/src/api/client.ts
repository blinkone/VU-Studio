// Thin fetch wrapper. Every state-changing request carries the CSRF header the service requires.

export class ApiError extends Error {
  readonly status: number;
  readonly body: unknown;

  constructor(status: number, message: string, body: unknown) {
    super(message);
    this.status = status;
    this.body = body;
  }

  /** Validation errors returned by PUT /api/config. */
  get errors(): string[] {
    const b = this.body as { errors?: string[] } | null;
    return Array.isArray(b?.errors) ? b!.errors : [];
  }
}

async function request<T>(method: string, path: string, body?: unknown, headers?: Record<string, string>): Promise<T> {
  const init: RequestInit = {
    method,
    credentials: 'same-origin',
    headers: {
      Accept: 'application/json',
      ...(method !== 'GET' ? { 'X-VuStudio': '1' } : {}),
      ...(body !== undefined && !(body instanceof FormData) ? { 'Content-Type': 'application/json' } : {}),
      ...headers,
    },
  };
  if (body !== undefined) {
    init.body = body instanceof FormData ? body : JSON.stringify(body);
  }

  const response = await fetch(path, init);
  const text = await response.text();
  let parsed: unknown = undefined;
  if (text) {
    try {
      parsed = JSON.parse(text);
    } catch {
      parsed = text;
    }
  }

  if (!response.ok) {
    const message =
      (parsed as { error?: string } | undefined)?.error ??
      (parsed as { errors?: string[] } | undefined)?.errors?.join('\n') ??
      `${response.status} ${response.statusText}`;
    throw new ApiError(response.status, message, parsed);
  }

  return parsed as T;
}

export const api = {
  get: <T>(path: string) => request<T>('GET', path),
  post: <T = void>(path: string, body?: unknown) => request<T>('POST', path, body ?? {}),
  put: <T>(path: string, body: unknown, headers?: Record<string, string>) => request<T>('PUT', path, body, headers),
  delete: <T = void>(path: string) => request<T>('DELETE', path),
  upload: <T>(path: string, form: FormData) => request<T>('POST', path, form),

  /** Fetches a binary resource (e.g. a PNG preview) and returns an object URL. Revoke it when done. */
  async blobUrl(path: string, body?: unknown): Promise<string> {
    const response = await fetch(path, {
      method: body === undefined ? 'GET' : 'POST',
      credentials: 'same-origin',
      headers: body === undefined ? {} : { 'Content-Type': 'application/json', 'X-VuStudio': '1' },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    if (!response.ok) {
      throw new ApiError(response.status, `${response.status} ${response.statusText}`, null);
    }

    return URL.createObjectURL(await response.blob());
  },
};
