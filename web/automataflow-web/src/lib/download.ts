import { ensureOk } from '../api/client';

type AuthFetch = (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>;

/**
 * Downloads an authorized resource (artifact, results export) with the bearer header and hands it to the user as an
 * object URL. A token is never put into a URL (ADR-076). The file name is the one the caller chose, never a header value.
 */
export async function downloadAuthorized(authFetch: AuthFetch, url: string, fileName: string): Promise<void> {
  const response = await ensureOk(await authFetch(url));
  const blob = await response.blob();
  const objectUrl = URL.createObjectURL(blob);
  try {
    const anchor = document.createElement('a');
    anchor.href = objectUrl;
    anchor.download = fileName;
    anchor.rel = 'noopener';
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
  } finally {
    setTimeout(() => URL.revokeObjectURL(objectUrl), 10_000);
  }
}

/** Fetches an authorized image (a screenshot) as an object URL; the caller revokes it. */
export async function authorizedObjectUrl(authFetch: AuthFetch, url: string, contentType: string): Promise<string> {
  const response = await ensureOk(await authFetch(url));
  // The Api serves artifacts as application/octet-stream (nosniff); re-type only allow-listed image types for display.
  const blob = new Blob([await response.arrayBuffer()], { type: contentType });
  return URL.createObjectURL(blob);
}

export const DisplayableImageTypes = new Set(['image/png', 'image/jpeg']);
