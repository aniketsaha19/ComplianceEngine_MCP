/** The sign-in / sign-up page shown during the OAuth authorization step. */

const escape = (value = '') =>
    String(value).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);

export function loginPage({ requestId = '', error = '', email = '', mode = 'login' } = {}) {
    const signup = mode === 'signup';
    return `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Sign in — Compliance Engine</title>
<style>
  :root { color-scheme: light dark; --fg: #1a1a1a; --bg: #fafafa; --card: #fff; --line: #ddd; --accent: #2b5fd9; }
  @media (prefers-color-scheme: dark) {
    :root { --fg: #eee; --bg: #17181a; --card: #212226; --line: #3a3b40; --accent: #6b93ff; }
  }
  body { margin: 0; min-height: 100vh; display: grid; place-items: center; background: var(--bg); color: var(--fg);
         font: 15px/1.5 system-ui, -apple-system, Segoe UI, sans-serif; padding: 16px; }
  .card { background: var(--card); border: 1px solid var(--line); border-radius: 12px; padding: 28px; width: min(380px, 100%); }
  h1 { font-size: 19px; margin: 0 0 4px; }
  p.sub { margin: 0 0 20px; opacity: .7; font-size: 13px; }
  label { display: block; font-size: 13px; margin: 14px 0 4px; }
  input { width: 100%; box-sizing: border-box; padding: 9px 11px; border: 1px solid var(--line);
          border-radius: 7px; background: transparent; color: inherit; font-size: 14px; }
  button { width: 100%; margin-top: 20px; padding: 10px; border: 0; border-radius: 7px;
           background: var(--accent); color: #fff; font-size: 14px; font-weight: 600; cursor: pointer; }
  .err { margin: 0 0 14px; padding: 9px 11px; border-radius: 7px; background: #d92b2b1a; color: #d92b2b; font-size: 13px; }
  .alt { margin: 16px 0 0; text-align: center; font-size: 13px; }
  a { color: var(--accent); }
</style>
</head>
<body>
  <main class="card">
    <h1>${signup ? 'Create an account' : 'Sign in'}</h1>
    <p class="sub">Connect your MCP client to the portfolio compliance engine.</p>
    ${error ? `<p class="err">${escape(error)}</p>` : ''}
    <form method="post" action="/login">
      <input type="hidden" name="request_id" value="${escape(requestId)}">
      <input type="hidden" name="mode" value="${signup ? 'signup' : 'login'}">
      ${signup ? '<label for="name">Organization name</label><input id="name" name="name" required autocomplete="organization">' : ''}
      <label for="email">Email</label>
      <input id="email" name="email" type="email" value="${escape(email)}" required autocomplete="email" autofocus>
      <label for="password">Password</label>
      <input id="password" name="password" type="password" required minlength="${signup ? 12 : 1}"
             autocomplete="${signup ? 'new-password' : 'current-password'}">
      ${signup ? '<p class="sub" style="margin:6px 0 0">At least 12 characters.</p>' : ''}
      <button type="submit">${signup ? 'Create account and connect' : 'Sign in'}</button>
    </form>
    <p class="alt">
      ${signup
          ? `Already have an account? <a href="/login?request_id=${encodeURIComponent(requestId)}">Sign in</a>`
          : `New here? <a href="/login?request_id=${encodeURIComponent(requestId)}&amp;mode=signup">Create an account</a>`}
    </p>
  </main>
</body>
</html>`;
}
