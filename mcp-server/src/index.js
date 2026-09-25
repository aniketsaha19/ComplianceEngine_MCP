/**
 * Compliance Engine MCP Gateway.
 *
 * Exposes the .NET compliance engine as MCP tools over Streamable HTTP, with OAuth 2.1
 * (Authorization Code + PKCE + Dynamic Client Registration) in front. Users add a URL to
 * their MCP client config and sign in through the browser; API keys stay server-side.
 */

import { randomUUID } from 'node:crypto';
import express from 'express';
import rateLimit from 'express-rate-limit';
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { StreamableHTTPServerTransport } from '@modelcontextprotocol/sdk/server/streamableHttp.js';
import { mcpAuthRouter } from '@modelcontextprotocol/sdk/server/auth/router.js';
import { requireBearerAuth } from '@modelcontextprotocol/sdk/server/auth/middleware/bearerAuth.js';
import { ComplianceAuthProvider } from './auth.js';
import { registerTools } from './tools.js';
import { engineRequest, ENGINE_URL } from './engine.js';
import { loginPage } from './login-page.js';

const PORT = Number(process.env.PORT || 3000);
const PUBLIC_URL = new URL(process.env.PUBLIC_URL || `http://localhost:${PORT}`);
const SERVER_INFO = { name: 'compliance-engine', version: '3.0.0' };

/**
 * Hosts the MCP endpoint answers to. PUBLIC_URL's host is always allowed; ALLOWED_HOSTS adds
 * any others the same deployment is reached by (e.g. the platform-assigned name alongside a
 * custom domain). A host that is missing here is rejected by DNS-rebinding protection.
 */
const ALLOWED_HOSTS = [...new Set([
    PUBLIC_URL.host,
    `localhost:${PORT}`,
    `127.0.0.1:${PORT}`,
    ...(process.env.ALLOWED_HOSTS ?? '')
        .split(',')
        .map((host) => host.trim())
        .filter(Boolean)
])];

const provider = new ComplianceAuthProvider();
setInterval(() => provider.sweep(), 60_000).unref();

const app = express();
app.disable('x-powered-by');

// OAuth metadata, /authorize, /token, /register and /revoke. Rate limited by the SDK.
app.use(
    mcpAuthRouter({
        provider,
        issuerUrl: PUBLIC_URL,
        baseUrl: PUBLIC_URL,
        resourceServerUrl: new URL('/mcp', PUBLIC_URL),
        resourceName: 'Portfolio Compliance Engine',
        scopesSupported: ['compliance']
    })
);

// --- Browser login, the only place credentials are entered -------------------

const loginLimiter = rateLimit({
    windowMs: 15 * 60_000,
    limit: 20,
    standardHeaders: true,
    legacyHeaders: false,
    message: { error: 'Too many login attempts. Try again later.' }
});

app.get('/login', (req, res) => {
    const requestId = String(req.query.request_id || '');
    if (!requestId) {
        return res.status(400).type('html').send(loginPage({ error: 'Missing authorization request.' }));
    }
    res.type('html').send(loginPage({ requestId, mode: req.query.mode === 'signup' ? 'signup' : 'login' }));
});

app.post('/login', loginLimiter, express.urlencoded({ extended: false }), async (req, res) => {
    const { request_id: requestId, email, password, name, mode } = req.body ?? {};

    try {
        if (mode === 'signup') {
            try {
                await engineRequest('POST', '/tenants', { body: { name, email, password } });
            } catch (error) {
                // Account already there (or a double-submit): fall through to signing in, which
                // still checks the password. Anything else is a real signup failure.
                if (error.status !== 409) throw error;
            }
        }
        const redirectUrl = await provider.completeLogin(requestId, email, password);
        res.redirect(redirectUrl);
    } catch (error) {
        const message = error.body?.error || error.message || 'Sign-in failed.';
        res.status(401).type('html').send(loginPage({ requestId, error: message, email, mode }));
    }
});

// --- MCP endpoint -----------------------------------------------------------

/** One transport per MCP session, keyed by the session id the SDK issues. */
const transports = new Map();

function buildServer() {
    const server = new McpServer(SERVER_INFO, {
        capabilities: { tools: {} },
        instructions:
            'Portfolio compliance tools. The engine computes every compliance figure — call these tools ' +
            'and report what they return rather than calculating breaches yourself. Run check_trade_compliance ' +
            'before record_trade when the user is considering a trade.'
    });
    registerTools(server);
    return server;
}

const requireAuth = requireBearerAuth({
    verifier: provider,
    resourceMetadataUrl: new URL('/.well-known/oauth-protected-resource/mcp', PUBLIC_URL).href
});

app.all('/mcp', requireAuth, express.json({ limit: '1mb' }), async (req, res) => {
    const sessionId = req.headers['mcp-session-id'];
    let transport = sessionId ? transports.get(sessionId) : undefined;

    if (!transport) {
        if (req.method !== 'POST') {
            return res.status(400).json({
                jsonrpc: '2.0',
                error: { code: -32000, message: 'No valid session. Initialize with POST first.' },
                id: null
            });
        }

        transport = new StreamableHTTPServerTransport({
            sessionIdGenerator: () => randomUUID(),
            // Guards against DNS-rebinding by hostile web pages.
            enableDnsRebindingProtection: true,
            allowedHosts: ALLOWED_HOSTS,
            onsessioninitialized: (id) => transports.set(id, transport),
            onsessionclosed: (id) => transports.delete(id)
        });
        transport.onclose = () => {
            if (transport.sessionId) transports.delete(transport.sessionId);
        };
        await buildServer().connect(transport);
    }

    await transport.handleRequest(req, res, req.body);
});

app.get('/health', async (_req, res) => {
    try {
        await engineRequest('GET', '/health');
        res.json({ status: 'ok', engine: 'available', sessions: transports.size, engineUrl: ENGINE_URL });
    } catch (error) {
        res.status(503).json({ status: 'degraded', engine: 'unavailable', detail: error.message });
    }
});

app.listen(PORT, () => {
    console.log(`Compliance MCP gateway on ${PUBLIC_URL.href} (engine: ${ENGINE_URL})`);
    console.log(`MCP endpoint: ${new URL('/mcp', PUBLIC_URL).href}`);
    console.log(`Allowed hosts: ${ALLOWED_HOSTS.join(', ')}`);
});
