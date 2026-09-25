/**
 * Thin HTTP client for the .NET compliance engine.
 *
 * Every call carries the API key of the tenant that owns the current MCP session,
 * so tenant isolation is enforced by the engine, not by this gateway.
 */

export const ENGINE_URL = process.env.COMPLIANCE_API_URL || 'http://localhost:5000';
const TIMEOUT_MS = Number(process.env.ENGINE_TIMEOUT_MS || 15000);

/** Engine responded with a non-2xx status. Carries the status so callers can map it. */
export class EngineError extends Error {
    constructor(status, body) {
        super(typeof body === 'string' ? body : body?.error || `Engine returned ${status}`);
        this.status = status;
        this.body = body;
    }
}

export async function engineRequest(method, path, { apiKey, body } = {}) {
    let response;
    try {
        response = await fetch(`${ENGINE_URL}${path}`, {
            method,
            headers: {
                'Content-Type': 'application/json',
                ...(apiKey ? { Authorization: `Bearer ${apiKey}` } : {})
            },
            body: body === undefined ? undefined : JSON.stringify(body),
            signal: AbortSignal.timeout(TIMEOUT_MS)
        });
    } catch (error) {
        // Network failure or timeout — the engine is down or unreachable.
        throw new EngineError(503, { error: `Compliance engine unreachable: ${error.message}` });
    }

    const text = await response.text();
    let payload = text;
    try {
        payload = text ? JSON.parse(text) : {};
    } catch {
        // Engine returned non-JSON (e.g. a plain-text 401); keep the raw string.
    }

    if (!response.ok) {
        throw new EngineError(response.status, payload);
    }
    return payload;
}
