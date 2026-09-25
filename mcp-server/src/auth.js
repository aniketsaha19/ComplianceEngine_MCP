/**
 * OAuth 2.1 authorization server for the MCP gateway.
 *
 * The user only ever puts a URL in their MCP client config. The client performs
 * Authorization Code + PKCE against this server; the user signs in with email and
 * password in a browser; this gateway exchanges those credentials for a tenant API
 * key at the engine and keeps that key server-side, bound to the issued access token.
 * The tenant API key never reaches the user or the MCP client.
 *
 * ponytail: issued tokens live in memory, so a restart makes clients re-authorize (one browser
 * sign-in) and a second instance would not share sessions. In-flight logins and client
 * registrations already survive restarts. Move the token store into the engine DB when this
 * needs to run more than one process.
 */

import { randomBytes, createHash, createHmac, timingSafeEqual } from 'node:crypto';
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { dirname } from 'node:path';
import { InvalidGrantError, InvalidTokenError } from '@modelcontextprotocol/sdk/server/auth/errors.js';
import { engineRequest, EngineError } from './engine.js';

const AUTH_CODE_TTL_MS = 60_000;
const ACCESS_TOKEN_TTL_S = 60 * 60;
const PENDING_LOGIN_TTL_MS = 10 * 60_000;
const CLIENTS_FILE = process.env.CLIENTS_FILE || '.data/clients.json';

const token = () => randomBytes(32).toString('base64url');
const hash = (value) => createHash('sha256').update(value).digest('hex');

/**
 * Secret for signing in-flight login requests. Set GATEWAY_SECRET in any deployment that
 * restarts or runs more than one process; a generated one invalidates logins already in flight.
 */
const SECRET = process.env.GATEWAY_SECRET || randomBytes(32).toString('hex');
if (!process.env.GATEWAY_SECRET) {
    console.warn('GATEWAY_SECRET not set — generated an ephemeral one. Set it to survive restarts.');
}

const sign = (payload) => createHmac('sha256', SECRET).update(payload).digest('base64url');

/**
 * Client registrations survive restarts: MCP clients cache their client_id on disk, and a
 * forgotten registration leaves them looping on invalid_client until their cache is cleared.
 */
class ClientStore {
    #clients = new Map();

    constructor() {
        try {
            for (const client of JSON.parse(readFileSync(CLIENTS_FILE, 'utf8'))) {
                this.#clients.set(client.client_id, client);
            }
        } catch {
            // No registrations yet — first run, or the file was removed.
        }
    }

    async getClient(clientId) {
        return this.#clients.get(clientId);
    }

    async registerClient(client) {
        this.#clients.set(client.client_id, client);
        try {
            mkdirSync(dirname(CLIENTS_FILE), { recursive: true });
            writeFileSync(CLIENTS_FILE, JSON.stringify([...this.#clients.values()], null, 2), { mode: 0o600 });
        } catch (error) {
            console.error(`Could not persist client registrations: ${error.message}`);
        }
        return client;
    }
}

export class ComplianceAuthProvider {
    #codes = new Map(); // code hash -> grant
    #tokens = new Map(); // access token hash -> grant
    #refresh = new Map(); // refresh token hash -> grant

    clientsStore = new ClientStore();

    /**
     * Step 1 of the flow: hand the browser a signed, self-contained description of the
     * authorization request and send it to our login page. Holding no server-side state here
     * means a restart mid-login cannot strand the user. Nothing is granted until credentials
     * check out at the engine.
     */
    async authorize(client, params, res) {
        const requestId = this.#sealRequest({
            clientId: client.client_id,
            redirectUri: params.redirectUri,
            codeChallenge: params.codeChallenge,
            state: params.state,
            scopes: params.scopes ?? [],
            resource: params.resource?.href,
            expiresAt: Date.now() + PENDING_LOGIN_TTL_MS
        });
        res.redirect(`/login?request_id=${encodeURIComponent(requestId)}`);
    }

    /** Called by the login page once the browser posts credentials. Returns the redirect URL. */
    async completeLogin(requestId, email, password) {
        const pending = this.#openRequest(requestId);

        // Credentials go straight to the engine; the gateway never stores them.
        const session = await engineRequest('POST', '/tenants/login', {
            body: { email, password, label: `mcp:${pending.clientId}` }
        });

        const code = token();
        this.#codes.set(hash(code), {
            ...pending,
            tenantId: session.tenantId,
            tenantName: session.tenantName,
            apiKey: session.apiKey,
            expiresAt: Date.now() + AUTH_CODE_TTL_MS
        });

        const redirect = new URL(pending.redirectUri);
        redirect.searchParams.set('code', code);
        if (pending.state !== undefined) {
            redirect.searchParams.set('state', pending.state);
        }
        return redirect.href;
    }

    async challengeForAuthorizationCode(client, authorizationCode) {
        const grant = this.#codes.get(hash(authorizationCode));
        if (!grant || grant.clientId !== client.client_id || grant.expiresAt < Date.now()) {
            throw new InvalidGrantError('Invalid or expired authorization code');
        }
        return grant.codeChallenge;
    }

    async exchangeAuthorizationCode(client, authorizationCode, _codeVerifier, redirectUri) {
        const key = hash(authorizationCode);
        const grant = this.#codes.get(key);
        // Authorization codes are single use.
        this.#codes.delete(key);

        if (!grant || grant.clientId !== client.client_id || grant.expiresAt < Date.now()) {
            throw new InvalidGrantError('Invalid or expired authorization code');
        }
        if (redirectUri && redirectUri !== grant.redirectUri) {
            throw new InvalidGrantError('redirect_uri does not match the authorization request');
        }

        return this.#issueTokens(grant);
    }

    async exchangeRefreshToken(client, refreshToken, scopes) {
        const key = hash(refreshToken);
        const grant = this.#refresh.get(key);
        // Refresh tokens rotate on every use.
        this.#refresh.delete(key);

        if (!grant || grant.clientId !== client.client_id) {
            throw new InvalidGrantError('Invalid refresh token');
        }
        return this.#issueTokens({ ...grant, scopes: scopes?.length ? scopes : grant.scopes });
    }

    async verifyAccessToken(accessToken) {
        const grant = this.#tokens.get(hash(accessToken));
        if (!grant) {
            throw new InvalidTokenError('Invalid access token');
        }
        if (grant.expiresAt < Date.now()) {
            this.#tokens.delete(hash(accessToken));
            throw new InvalidTokenError('Access token expired');
        }

        return {
            token: accessToken,
            clientId: grant.clientId,
            scopes: grant.scopes,
            expiresAt: Math.floor(grant.expiresAt / 1000),
            ...(grant.resource ? { resource: new URL(grant.resource) } : {}),
            // The tenant API key rides here and never leaves the server.
            extra: { tenantId: grant.tenantId, tenantName: grant.tenantName, apiKey: grant.apiKey }
        };
    }

    async revokeToken(client, request) {
        const key = hash(request.token);
        const grant = this.#tokens.get(key) ?? this.#refresh.get(key);
        this.#tokens.delete(key);
        this.#refresh.delete(key);

        if (grant?.apiKey) {
            // Best effort: retire the engine-side key too, so a leaked key dies with the token.
            await engineRequest('POST', '/tenants/logout', { apiKey: grant.apiKey }).catch(() => {});
        }
    }

    /** Signs an authorization request so it can round-trip through the user's browser untampered. */
    #sealRequest(request) {
        const payload = Buffer.from(JSON.stringify(request)).toString('base64url');
        return `${payload}.${sign(payload)}`;
    }

    #openRequest(requestId) {
        const [payload, signature] = String(requestId).split('.');
        const expected = payload ? sign(payload) : '';
        const given = Buffer.from(signature ?? '');
        const want = Buffer.from(expected);

        if (!payload || given.length !== want.length || !timingSafeEqual(given, want)) {
            throw new Error('This login link is not valid. Start again from your MCP client.');
        }

        const request = JSON.parse(Buffer.from(payload, 'base64url').toString('utf8'));
        if (request.expiresAt < Date.now()) {
            throw new Error('This login request expired. Start again from your MCP client.');
        }
        return request;
    }

    #issueTokens(grant) {
        const accessToken = token();
        const refreshToken = token();
        const expiresAt = Date.now() + ACCESS_TOKEN_TTL_S * 1000;

        this.#tokens.set(hash(accessToken), { ...grant, expiresAt });
        this.#refresh.set(hash(refreshToken), { ...grant, expiresAt: undefined });

        return {
            access_token: accessToken,
            token_type: 'Bearer',
            expires_in: ACCESS_TOKEN_TTL_S,
            refresh_token: refreshToken,
            scope: grant.scopes.join(' ')
        };
    }

    /** Drops expired grants. Called on a timer so abandoned logins do not accumulate. */
    sweep(now = Date.now()) {
        for (const [key, value] of this.#codes) {
            if (value.expiresAt < now) this.#codes.delete(key);
        }
        for (const [key, value] of this.#tokens) {
            if (value.expiresAt < now) this.#tokens.delete(key);
        }
    }
}

export { EngineError };
