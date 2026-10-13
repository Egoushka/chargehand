/**
 * The policy the server sends on every /console/ response (WebConsole.ContentSecurityPolicy in Chargehand.Server; a .NET test
 * compares the two). `vite preview` sends it too, so the end-to-end smoke test fails on a violation. Scripts and styles come
 * from this origin only, none inline; the page talks to this origin only.
 */
export const CONTENT_SECURITY_POLICY =
  "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

/** The other headers the server adds (WebConsole.Harden). */
export const SECURITY_HEADERS = {
  'Content-Security-Policy': CONTENT_SECURITY_POLICY,
  'X-Content-Type-Options': 'nosniff',
  'Referrer-Policy': 'no-referrer',
  'X-Frame-Options': 'DENY',
};
