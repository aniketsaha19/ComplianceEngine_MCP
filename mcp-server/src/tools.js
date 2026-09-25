/**
 * MCP tool definitions. Each one is a thin, typed wrapper over one engine endpoint.
 * The engine computes every compliance number; nothing is calculated here.
 */

import { z } from 'zod';
import { engineRequest, EngineError } from './engine.js';

// Kept in step with validRuleTypes in engine/Controllers/RulesController.cs.
export const RULE_TYPES = [
    'max_position_pct',
    'max_sector_pct',
    'min_holdings_count',
    'aggregate_large_position_pct',
    'max_top_n_concentration'
];

// structuredContent must be a JSON object, so list responses travel under `items`.
const ok = (data) => ({
    content: [{ type: 'text', text: JSON.stringify(data, null, 2) }],
    structuredContent: Array.isArray(data) ? { items: data } : data
});

/**
 * Engine errors come back as tool results with isError, not as protocol errors, so the
 * model can read the validation message and correct itself instead of the call blowing up.
 */
const fail = (error) => ({
    content: [{ type: 'text', text: JSON.stringify({ error: error.message, details: error.body ?? null }, null, 2) }],
    isError: true
});

/** Every tool runs through here, so auth plumbing and error mapping live in one place. */
function handler(fn) {
    return async (args, extra) => {
        const apiKey = extra?.authInfo?.extra?.apiKey;
        if (!apiKey) {
            return fail(new EngineError(401, { error: 'Not authenticated. Sign in through your MCP client.' }));
        }
        try {
            return ok(await fn(args, (method, path, body) => engineRequest(method, path, { apiKey, body })));
        } catch (error) {
            if (error instanceof EngineError) return fail(error);
            throw error;
        }
    };
}

export function registerTools(server) {
    server.registerTool(
        'whoami',
        {
            title: 'Who am I',
            description: 'Return the signed-in tenant (id, name, email). Use to confirm which account the session is acting as.',
            inputSchema: {},
            annotations: { readOnlyHint: true, openWorldHint: false }
        },
        handler((_args, call) => call('GET', '/tenants/profile'))
    );

    server.registerTool(
        'create_portfolio',
        {
            title: 'Create portfolio',
            description: 'Create a new portfolio for the signed-in tenant. Portfolio names must be unique per tenant.',
            inputSchema: { name: z.string().min(1).max(200).describe('Portfolio name, unique within the tenant') },
            annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false }
        },
        handler(({ name }, call) => call('POST', '/portfolios', { name }))
    );

    server.registerTool(
        'list_portfolios',
        {
            title: 'List portfolios',
            description: "List the signed-in tenant's portfolios with their holdings counts.",
            inputSchema: {},
            annotations: { readOnlyHint: true, openWorldHint: false }
        },
        handler((_args, call) => call('GET', '/portfolios'))
    );

    server.registerTool(
        'delete_portfolio',
        {
            title: 'Delete portfolio',
            description: 'Permanently delete a portfolio and its holdings. Cannot be undone.',
            inputSchema: { portfolio_id: z.number().int().positive() },
            annotations: { readOnlyHint: false, destructiveHint: true, idempotentHint: true, openWorldHint: false }
        },
        handler(({ portfolio_id }, call) => call('DELETE', `/portfolios/${portfolio_id}`))
    );

    server.registerTool(
        'add_rule',
        {
            title: 'Add compliance rule',
            description:
                'Create a compliance rule for the tenant. Thresholds for the *_pct rule types are fractions ' +
                'of the portfolio (0.1 = 10%), matching how the engine computes holding weights. ' +
                'min_holdings_count takes a whole number of securities instead.',
            inputSchema: {
                name: z.string().min(1).max(200).describe('Rule name, unique within the tenant'),
                rule_type: z.enum(RULE_TYPES).describe('Which check the engine runs'),
                threshold: z.number().describe('Fraction for *_pct rules (0.1 = 10%), or a count for min_holdings_count'),
                description: z.string().max(1000).optional()
            },
            annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false }
        },
        handler(({ name, rule_type, threshold, description }, call) =>
            call('POST', '/rules', { name, ruleType: rule_type, threshold, description: description ?? '' })
        )
    );

    server.registerTool(
        'list_rules',
        {
            title: 'List compliance rules',
            description: "List the tenant's compliance rules and whether each is active.",
            inputSchema: {},
            annotations: { readOnlyHint: true, openWorldHint: false }
        },
        handler((_args, call) => call('GET', '/rules'))
    );

    server.registerTool(
        'delete_rule',
        {
            title: 'Delete compliance rule',
            description: 'Permanently delete a compliance rule. Cannot be undone.',
            inputSchema: { rule_id: z.number().int().positive() },
            annotations: { readOnlyHint: false, destructiveHint: true, idempotentHint: true, openWorldHint: false }
        },
        handler(({ rule_id }, call) => call('DELETE', `/rules/${rule_id}`))
    );

    server.registerTool(
        'get_holdings',
        {
            title: 'Get holdings',
            description: 'List a portfolio\'s holdings with quantities and market values.',
            inputSchema: { portfolio_id: z.number().int().positive() },
            annotations: { readOnlyHint: true, openWorldHint: false }
        },
        handler(({ portfolio_id }, call) => call('GET', `/portfolios/${portfolio_id}/holdings`))
    );

    server.registerTool(
        'check_compliance',
        {
            title: 'Check portfolio compliance',
            description:
                'Evaluate a portfolio against every active rule and return each rule outcome. ' +
                'This writes an audit record of the evaluation.',
            inputSchema: { portfolio_id: z.number().int().positive() },
            annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: true, openWorldHint: false }
        },
        handler(({ portfolio_id }, call) => call('GET', `/portfolios/${portfolio_id}/compliance-summary`))
    );

    const tradeShape = {
        portfolio_id: z.number().int().positive(),
        ticker: z.string().min(1).max(20),
        sector: z.string().max(50).optional(),
        action: z.enum(['BUY', 'SELL']),
        quantity: z.number().positive(),
        price: z.number().positive()
    };

    server.registerTool(
        'check_trade_compliance',
        {
            title: 'Pre-trade compliance check',
            description:
                'Apply a hypothetical trade in memory and report whether it would breach any rule. ' +
                'Changes nothing: no trade is recorded and no holdings are updated.',
            inputSchema: tradeShape,
            annotations: { readOnlyHint: true, openWorldHint: false }
        },
        handler((args, call) => call('POST', '/trades/compliance-check', toTradeBody(args)))
    );

    server.registerTool(
        'record_trade',
        {
            title: 'Record trade',
            description:
                'Submit a trade. The engine evaluates it, records it as EXECUTED or BLOCKED, and only ' +
                'updates holdings when it was allowed. Use check_trade_compliance first for a dry run.',
            inputSchema: tradeShape,
            annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false }
        },
        handler((args, call) => call('POST', '/trades', toTradeBody(args)))
    );
}

// The engine's trade DTO takes a snake_case portfolio_id alongside PascalCase fields.
const toTradeBody = ({ portfolio_id, ticker, sector, action, quantity, price }) => ({
    portfolio_id,
    ticker,
    sector: sector ?? null,
    action,
    quantity,
    price
});
