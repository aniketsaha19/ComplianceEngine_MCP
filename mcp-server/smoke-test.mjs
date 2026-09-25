// End-to-end probe: OAuth discovery -> DCR -> signup+authorize -> token -> MCP tools.
import { createHash, randomBytes } from 'node:crypto';

const BASE = 'http://localhost:3000';
const REDIRECT = 'http://localhost:9876/callback';
const b64 = (b) => b.toString('base64url');
const verifier = b64(randomBytes(32));
const challenge = b64(createHash('sha256').update(verifier).digest());

const j = async (label, url, opts) => {
    const r = await fetch(url, opts);
    const t = await r.text();
    let body = t; try { body = JSON.parse(t); } catch {}
    console.log(label, r.status);
    return { status: r.status, body, headers: r.headers };
};

// 1. Discovery
const prm = await j('protected-resource-metadata', `${BASE}/.well-known/oauth-protected-resource/mcp`);
console.log('   resource:', prm.body.resource, 'as:', prm.body.authorization_servers);
const asm = await j('auth-server-metadata', `${BASE}/.well-known/oauth-authorization-server`);
console.log('   endpoints:', asm.body.authorization_endpoint, asm.body.token_endpoint, asm.body.registration_endpoint);

// 2. Dynamic client registration
const reg = await j('register(DCR)', `${BASE}/register`, {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ client_name: 'probe', redirect_uris: [REDIRECT], grant_types: ['authorization_code', 'refresh_token'], response_types: ['code'], token_endpoint_auth_method: 'none' })
});
const clientId = reg.body.client_id;

// 3. Authorize -> redirected to login page
const authUrl = `${BASE}/authorize?response_type=code&client_id=${clientId}&redirect_uri=${encodeURIComponent(REDIRECT)}&code_challenge=${challenge}&code_challenge_method=S256&state=xyz&resource=${encodeURIComponent(BASE + '/mcp')}`;
const authRes = await fetch(authUrl, { redirect: 'manual' });
const loginLoc = authRes.headers.get('location');
console.log('authorize', authRes.status, '->', loginLoc);
const requestId = new URL(loginLoc, BASE).searchParams.get('request_id');

// 4. Unauthenticated MCP call must be refused
const unauth = await j('mcp without token', `${BASE}/mcp`, {
    method: 'POST', headers: { 'Content-Type': 'application/json', Accept: 'application/json, text/event-stream' },
    body: JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'tools/list', params: {} })
});
console.log('   www-authenticate:', unauth.headers.get('www-authenticate'));

// 5. Wrong password must fail
const email = `probe${Date.now()}@example.com`;
const badForm = new URLSearchParams({ request_id: requestId, email, password: 'wrong-password', mode: 'login' });
const bad = await fetch(`${BASE}/login`, { method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded' }, body: badForm, redirect: 'manual' });
console.log('login wrong password', bad.status);

// 6. Sign up + sign in through the browser form
const form = new URLSearchParams({ request_id: requestId, mode: 'signup', name: 'Probe Fund', email, password: 'correct-horse-battery' });
const login = await fetch(`${BASE}/login`, { method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded' }, body: form, redirect: 'manual' });
const cbUrl = new URL(login.headers.get('location'));
console.log('login', login.status, '-> code issued, state =', cbUrl.searchParams.get('state'));
const code = cbUrl.searchParams.get('code');

// 7. Token exchange with PKCE
const tok = await j('token (PKCE)', `${BASE}/token`, {
    method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({ grant_type: 'authorization_code', code, code_verifier: verifier, client_id: clientId, redirect_uri: REDIRECT, resource: `${BASE}/mcp` })
});
const accessToken = tok.body.access_token;
console.log('   access token issued, expires_in', tok.body.expires_in, '| refresh token:', !!tok.body.refresh_token);

// 8. Replaying the same code must fail (single use)
const replay = await j('token replay (must fail)', `${BASE}/token`, {
    method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({ grant_type: 'authorization_code', code, code_verifier: verifier, client_id: clientId, redirect_uri: REDIRECT })
});

// 9. MCP session
const H = { 'Content-Type': 'application/json', Accept: 'application/json, text/event-stream', Authorization: `Bearer ${accessToken}` };
const parse = (text) => { const line = text.split('\n').find((l) => l.startsWith('data:')); return JSON.parse(line ? line.slice(5) : text); };

const initRes = await fetch(`${BASE}/mcp`, { method: 'POST', headers: H, body: JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'initialize', params: { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'probe', version: '1' } } }) });
const sessionId = initRes.headers.get('mcp-session-id');
const init = parse(await initRes.text());
console.log('initialize', initRes.status, JSON.stringify(init.result.serverInfo), 'session:', !!sessionId);

const S = { ...H, 'mcp-session-id': sessionId, 'MCP-Protocol-Version': '2025-06-18' };
await fetch(`${BASE}/mcp`, { method: 'POST', headers: S, body: JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) });

const call = async (method, params) => {
    const r = await fetch(`${BASE}/mcp`, { method: 'POST', headers: S, body: JSON.stringify({ jsonrpc: '2.0', id: Math.floor(Math.random() * 1e6), method, params }) });
    return parse(await r.text());
};

const list = await call('tools/list', {});
console.log('tools/list:', list.result.tools.map((t) => t.name).join(', '));

const tool = async (name, args) => {
    const r = await call('tools/call', { name, arguments: args });
    const text = r.result?.content?.[0]?.text ?? JSON.stringify(r.error);
    console.log(`  ${name}: ${r.result?.isError ? 'isError ' : ''}${text.replace(/\s+/g, ' ').slice(0, 150)}`);
    return r;
};

console.log('tool calls:');
await tool('whoami', {});
const created = await tool('create_portfolio', { name: 'Probe Portfolio' });
const pid = JSON.parse(created.result.content[0].text).portfolioId;
await tool('add_rule', { name: `MaxPos ${Date.now()}`, rule_type: 'max_position_pct', threshold: 25 });
await tool('check_compliance', { portfolio_id: pid });
await tool('check_trade_compliance', { portfolio_id: pid, ticker: 'AAPL', sector: 'Tech', action: 'BUY', quantity: 10, price: 100 });
await tool('record_trade', { portfolio_id: pid, ticker: 'AAPL', sector: 'Tech', action: 'BUY', quantity: 10, price: 100 });
await tool('get_holdings', { portfolio_id: pid });
console.log('negative cases:');
await tool('add_rule', { name: 'Bad', rule_type: 'nonsense_rule', threshold: 1 });
await tool('check_compliance', { portfolio_id: 999999 });

// 10. Cross-tenant isolation: second tenant must not see or probe the first's portfolio
const authRes2 = await fetch(authUrl.replace('state=xyz', 'state=abc'), { redirect: 'manual' });
const rid2 = new URL(authRes2.headers.get('location'), BASE).searchParams.get('request_id');
const email2 = `probe2-${Date.now()}@example.com`;
await fetch(`${BASE}/login`, { method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded' }, redirect: 'manual', body: new URLSearchParams({ request_id: rid2, mode: 'signup', name: 'Other Fund', email: email2, password: 'another-long-password' }) });
const v2 = b64(randomBytes(32));
// fresh PKCE for tenant 2
const authUrl2 = `${BASE}/authorize?response_type=code&client_id=${clientId}&redirect_uri=${encodeURIComponent(REDIRECT)}&code_challenge=${b64(createHash('sha256').update(v2).digest())}&code_challenge_method=S256&state=t2`;
const a2 = await fetch(authUrl2, { redirect: 'manual' });
const rid3 = new URL(a2.headers.get('location'), BASE).searchParams.get('request_id');
const l2 = await fetch(`${BASE}/login`, { method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded' }, redirect: 'manual', body: new URLSearchParams({ request_id: rid3, mode: 'login', email: email2, password: 'another-long-password' }) });
const code2 = new URL(l2.headers.get('location')).searchParams.get('code');
const tok2 = await fetch(`${BASE}/token`, { method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded' }, body: new URLSearchParams({ grant_type: 'authorization_code', code: code2, code_verifier: v2, client_id: clientId, redirect_uri: REDIRECT }) });
const token2 = (await tok2.json()).access_token;

const H2 = { 'Content-Type': 'application/json', Accept: 'application/json, text/event-stream', Authorization: `Bearer ${token2}` };
const i2 = await fetch(`${BASE}/mcp`, { method: 'POST', headers: H2, body: JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'initialize', params: { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'probe2', version: '1' } } }) });
const S2 = { ...H2, 'mcp-session-id': i2.headers.get('mcp-session-id'), 'MCP-Protocol-Version': '2025-06-18' };
const call2 = async (name, args) => {
    const r = await fetch(`${BASE}/mcp`, { method: 'POST', headers: S2, body: JSON.stringify({ jsonrpc: '2.0', id: 7, method: 'tools/call', params: { name, arguments: args } }) });
    const p = parse(await r.text());
    console.log(`  tenant2 ${name}: ${p.result?.isError ? 'isError ' : ''}${(p.result?.content?.[0]?.text ?? '').replace(/\s+/g, ' ').slice(0, 120)}`);
};
console.log('cross-tenant isolation:');
await call2('whoami', {});
await call2('list_portfolios', {});
await call2('get_holdings', { portfolio_id: pid });
await call2('check_trade_compliance', { portfolio_id: pid, ticker: 'AAPL', sector: 'Tech', action: 'BUY', quantity: 1, price: 1 });
