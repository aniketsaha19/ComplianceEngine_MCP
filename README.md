# Portfolio Compliance Engine

A deterministic rule engine that decides whether a portfolio breaches its compliance limits, with an
MCP server in front so an AI assistant can ask questions about it in plain language.

The split matters: **the engine computes every number, the model only narrates.** An LLM asked to
work out "is this position over 10%?" will sometimes get it wrong and always be unauditable. Here it
cannot compute anything — it calls a tool, receives figures from a .NET service backed by SQL Server,
and explains them. Every evaluation is also written to an audit table, so any answer the model gave
can be reconstructed later from the database alone.

```
engine/       .NET 10 Web API — the rule engine, and the only component that touches the database
mcp-server/   Node.js MCP gateway — OAuth 2.1 + Streamable HTTP, exposes the engine as MCP tools
docs/         Older design notes (superseded in places — read for rationale, not for schema)
```

---

## Contents

- [How it runs on Azure](#how-it-runs-on-azure)
- [Connecting an AI assistant](#connecting-an-ai-assistant)
- [How login works](#how-login-works)
- [Tools reference](#tools-reference)
- [Rule types](#rule-types)
- [Multi-tenancy and security](#multi-tenancy-and-security)
- [Engine HTTP API](#engine-http-api)
- [Running locally](#running-locally)
- [Configuration](#configuration)
- [Operating it](#operating-it)
- [Known gaps](#known-gaps)

---

## How it runs on Azure

```
                    ┌──────────────────────────────────────────────────────┐
   AI client        │  Azure Container Apps environment (cae-compliance)   │
   (Claude, etc.)   │                                                      │
        │           │   ┌────────────────────┐      ┌──────────────────┐   │
        │  HTTPS    │   │   mcp-gateway      │      │     engine       │   │
        └──────────▶│──▶│   external ingress │─────▶│ internal ingress │   │
                    │   │   Node 22, :30     │ http │ .NET 10, :80     │   │
                    │   └────────────────────┘      └────────┬─────────┘   │
                    │            │                           │             │
                    └────────────┼───────────────────────────┼─────────────┘
                                 │ secret ref                │ managed identity
                                 ▼                           ▼
                        ┌─────────────────┐      ┌────────────────────────┐
                        │   Key Vault     │      │  Azure SQL Database    │
                        │ GATEWAY_SECRET  │      │  serverless, Entra-only│
                        └─────────────────┘      └────────────────────────┘

        Container Registry ──▶ images        Log Analytics / App Insights ──▶ telemetry
        (built by GitHub Actions on push to main)
```

| Azure service | Role | Notes |
| --- | --- | --- |
| **Container Apps** (`mcp-gateway`) | Public entry point | External ingress, HTTPS terminated by the platform. Runs **one replica** — see [Known gaps](#known-gaps). |
| **Container Apps** (`engine`) | Rule engine | Ingress limited to the environment, so it is unreachable from the internet. Scales freely. |
| **Azure SQL Database** | All persistent state | Serverless with auto-pause; Entra-only authentication, so no password exists anywhere. |
| **Managed identity** | Engine → SQL auth | The engine authenticates as itself; the SQL login is a contained user created `FROM EXTERNAL PROVIDER`. |
| **Key Vault** | `GATEWAY_SECRET` | Referenced by the gateway as a secret, never baked into the image. |
| **Container Registry** | Images for both apps | Pushed by GitHub Actions on every commit to `main`. |
| **Log Analytics** | Logs from both apps | `ContainerAppConsoleLogs_CL` (app stdout) and `ContainerAppSystemLogs_CL` (platform events). |
| **Application Insights** | Traces and dependencies | Connection string is wired; the engine still needs the SDK package to emit. |

Why this shape: the gateway is the only thing exposed, so the engine's API — which trusts whatever
tenant key it is handed — never faces the internet. Tenant keys live in SQL and never leave the
gateway process. The database is reached by managed identity, so a leaked config file contains
nothing usable.

---

## Connecting an AI assistant

The deployment is live at `https://mcp-gateway.yellowsky-d48826eb.centralindia.azurecontainerapps.io`.

Add this to your MCP client's config — for Claude Desktop that is
`%APPDATA%\Claude\claude_desktop_config.json` (Windows) or
`~/Library/Application Support/Claude/claude_desktop_config.json` (macOS):

```json
{
  "mcpServers": {
    "compliance-engine": {
      "command": "npx",
      "args": [
        "-y",
        "mcp-remote",
        "https://mcp-gateway.yellowsky-d48826eb.centralindia.azurecontainerapps.io/mcp"
      ]
    }
  }
}
```

Then restart the client completely (quit from the tray or menu bar — closing the window is not
enough, the config is read only at startup).

`mcp-remote` is a small bridge: MCP clients that speak stdio use it to reach a remote
Streamable HTTP server, and it handles the browser-based OAuth flow. Clients that speak
Streamable HTTP natively can point at the same `/mcp` URL directly, with no `npx` wrapper.

**That URL is the only thing a user ever configures.** There is no API key to copy, paste, rotate or
accidentally commit.

On first use the client opens a browser for sign-in. After that, tokens are cached locally
(`~/.mcp-auth`), so restarting the client does not prompt again.

Once connected, try:

> *"What tenant am I signed in as?"* → calls `whoami`
> *"Create a portfolio called Growth Fund, then add a rule limiting any single position to 10%."*
> *"If I buy 500 shares of NVDA at $120, would that breach anything?"*

---

## How login works

The gateway is a full OAuth 2.1 authorization server, so the user authenticates *to the system*
rather than handing a credential *to the client*.

```
1. discovery      client GET /.well-known/oauth-protected-resource/mcp
                  → learns the authorization server for this resource

2. registration   client POST /register        (Dynamic Client Registration, RFC 7591)
                  → gets its own client_id; no pre-shared credentials

3. authorize      browser opens /authorize?...&code_challenge=...   (PKCE, S256)
                  → gateway signs the request and redirects to its own /login page

4. sign in        user submits email + password to /login
                  → gateway forwards them to the engine's POST /tenants/login
                  → engine verifies the password hash and mints a fresh tenant API key

5. code           gateway redirects back with a one-time authorization code

6. token          client POST /token with the code + PKCE verifier
                  → receives an access token (1 h) and a rotating refresh token

7. tool calls     every request carries the access token; the gateway maps it to the
                  tenant API key it is holding and calls the engine with it
```

What each party ends up holding:

| Party | Holds | Never sees |
| --- | --- | --- |
| User | Email + password | Any API key |
| MCP client | OAuth access + refresh tokens | The tenant API key, the password |
| Gateway | Tenant API key, bound to the access token, in memory | The password (forwarded, never stored) |
| Engine | SHA-256 hash of the API key, bcrypt-style hash of the password | Plaintext of either |

Details worth knowing:

- **Signing up** happens on the same page — "Create an account" collects an organization name,
  email and a 12+ character password. If the account already exists, it falls through to a normal
  sign-in rather than erroring.
- **Authorization requests are stateless.** The pending request is HMAC-signed into the login URL
  using `GATEWAY_SECRET`, so a gateway restart mid-login cannot strand the user, and a tampered
  request (say, a swapped `redirect_uri`) is rejected.
- **Each login mints a separate API key**, labelled with the client id. Revoking one MCP session
  does not disturb another.
- **Signing out** (OAuth token revocation) also retires the engine-side key, so a leaked token dies
  with it.
- **Token lifetimes**: access tokens last an hour and refresh silently. Restarting your AI client
  does not require a new sign-in; restarting the *gateway* does, because issued tokens are held in
  memory.

---

## Tools reference

Eleven tools, each a thin wrapper over one engine endpoint. Inputs are validated with zod before any
call is made, and engine 4xx responses come back as tool errors the model can read and correct,
rather than as protocol failures.

### `whoami`
No arguments. Returns the signed-in tenant: `id`, `name`, `email`, `isActive`, `createdAt`.
Use it to confirm which account a session is acting as.

### `create_portfolio`
| Arg | Type | Notes |
| --- | --- | --- |
| `name` | string, 1–200 chars | Must be unique within the tenant |

Returns `{ portfolioId, name }`.

### `list_portfolios`
No arguments. Returns every portfolio for the tenant with `id`, `name`, `createdAt` and
`holdingsCount`.

### `delete_portfolio`
| Arg | Type |
| --- | --- |
| `portfolio_id` | positive integer |

Deletes the portfolio and its holdings. Not reversible — annotated as destructive so clients can
prompt before calling.

### `add_rule`
| Arg | Type | Notes |
| --- | --- | --- |
| `name` | string | Unique within the tenant |
| `rule_type` | enum | One of the five in [Rule types](#rule-types) |
| `threshold` | number | **Fraction** for `*_pct` rules (`0.1` = 10%); a count for `min_holdings_count` |
| `description` | string, optional | Free text |

Rules are tenant-wide: every portfolio is evaluated against every active rule.

### `list_rules`
No arguments. Returns each rule with its type, threshold and whether it is active.

### `delete_rule`
| Arg | Type |
| --- | --- |
| `rule_id` | positive integer |

### `get_holdings`
| Arg | Type |
| --- | --- |
| `portfolio_id` | positive integer |

Returns each holding's ticker, sector, quantity, market value and last trade price.

### `check_compliance`
| Arg | Type |
| --- | --- |
| `portfolio_id` | positive integer |

Evaluates the portfolio against all active rules and returns `compliant` plus a per-rule breakdown:
rule name, `breached`, `currentValue`, `threshold` and a human-readable `detail` string such as
*"AAPL is 14.2% of the portfolio, exceeding the 10% single-position limit."*

This **writes an audit record** to `RuleEvaluations` — it is a point-in-time compliance check, not a
read-only query.

### `check_trade_compliance`
| Arg | Type | Notes |
| --- | --- | --- |
| `portfolio_id` | positive integer | |
| `ticker` | string, 1–20 chars | |
| `sector` | string, optional | Used by `max_sector_pct` |
| `action` | `BUY` or `SELL` | |
| `quantity` | positive number | |
| `price` | positive number | |

A **dry run**: applies the trade in memory, evaluates, and reports `tradeAllowed` with the rules that
would breach. Nothing is persisted — no trade record, no holdings change, no audit row.

### `record_trade`
Same arguments as `check_trade_compliance`.

Submits the trade for real. The engine evaluates it, writes a `Trade` row marked `EXECUTED` or
`BLOCKED`, records the rule evaluations against that trade, and **only updates holdings if the trade
was allowed**. A blocked trade still leaves an audit trail explaining why.

The natural pairing is `check_trade_compliance` first (to discuss), then `record_trade` (to commit) —
the server's instructions tell the model to do exactly that.

---

## Rule types

Holding weights are computed as `MarketValue / total portfolio MarketValue`, so weights are
fractions between 0 and 1 — **thresholds for percentage rules are fractions too**. A threshold of
`0.1` means 10%; a threshold of `10` would mean 1000% and could never breach.

| Rule type | Breaches when | Threshold |
| --- | --- | --- |
| `max_position_pct` | Any single holding's weight exceeds the threshold | Fraction, e.g. `0.1` |
| `max_sector_pct` | Any sector's combined weight exceeds the threshold | Fraction, e.g. `0.25` |
| `min_holdings_count` | Distinct securities held is **below** the threshold | Count, e.g. `20` |
| `aggregate_large_position_pct` | Positions of 5%+ together exceed the threshold | Fraction, e.g. `0.4` |
| `max_top_n_concentration` | The 10 largest holdings together exceed the threshold | Fraction, e.g. `0.6` |

The 5% trigger and the "top 10" window follow UCITS conventions (the 5/10/40 rule) and are constants
in `RuleEvaluationService`.

**Empty portfolios**: most rules report "not breached" when there are no holdings — there is nothing
to be concentrated in. `min_holdings_count` is the exception: zero holdings is genuinely below any
positive minimum, so it breaches.

Adding a new rule type means three edits in `Services/RuleEvaluationService.cs`: a `switch` case in
both `EvaluateHoldingsAsync` and `EvaluateHypotheticalHoldingsAsync`, an evaluator method, and a
branch in `CreateEmptyPortfolioOutcome`. Add the string to `RULE_TYPES` in `mcp-server/src/tools.js`
and to `validRuleTypes` in `RulesController` as well, or the tool will reject it before the engine
sees it.

---

## Multi-tenancy and security

Every domain table is scoped by `TenantId` — `Portfolios` and `Rules` directly, `Holdings`, `Trades`
and `RuleEvaluations` transitively through their portfolio.

- **Authentication**: `TenantAuthenticationMiddleware` reads `Bearer <apiKey>`, hashes it with
  SHA-256 and looks it up in `ApiKeys`, joining to an active tenant. The tenant is stashed in
  `HttpContext.Items["CurrentTenant"]`; controllers pull it out and return 401 if absent.
- **Public routes**: only `POST /tenants` (signup), `POST /tenants/login`, `/health` and `/swagger*`
  skip authentication.
- **Keys**: a tenant may hold many API keys — one per MCP login session — each individually
  revocable, stored only as a hash, with `LastUsedAt` recorded.
- **Passwords**: hashed with ASP.NET's `PasswordHasher`. The login path verifies even when no tenant
  matches, so a wrong email and a wrong password take the same time.
- **Ownership checks**: every portfolio-scoped endpoint verifies the portfolio belongs to the calling
  tenant before doing anything — including the pre-trade check, where a missing check would have let
  one tenant probe another's holdings by id.
- **The gateway holds no privileges of its own.** It has no master key and no way to address a tenant
  other than by presenting that tenant's key. Isolation is enforced by the engine, not by gateway
  logic.

The automated check in `mcp-server/smoke-test.mjs` covers this end to end: it signs up two tenants and
asserts that the second cannot read, evaluate or trade against the first's portfolio.

---

## Engine HTTP API

Internal — reachable only from inside the Container Apps environment. All routes except the public
ones need `Authorization: Bearer <apiKey>`.

| Method | Route | Purpose |
| --- | --- | --- |
| `POST` | `/tenants` | Sign up (name, email, password). Returns tenant id + first API key |
| `POST` | `/tenants/login` | Exchange credentials for a fresh API key |
| `POST` | `/tenants/logout` | Revoke the key used for the call |
| `GET` | `/tenants/profile` | Current tenant details |
| `GET` | `/health` | DB-backed liveness: `{ status, tenantCount, timestamp }` |
| `GET` / `POST` | `/portfolios` | List / create |
| `DELETE` | `/portfolios/{id}` | Delete with holdings |
| `GET` | `/portfolios/{id}/holdings` | Holdings for a portfolio |
| `GET` | `/portfolios/{id}/compliance-summary` | Evaluate against all active rules (writes audit rows) |
| `POST` | `/portfolios/{id}/trade-check` | Pre-trade check (alternative entry point) |
| `POST` | `/portfolios/{id}/trades` | Record a trade (alternative entry point) |
| `GET` / `POST` | `/rules` | List / create |
| `DELETE` | `/rules/{id}` | Delete |
| `POST` | `/trades` | Record a trade — what the gateway calls |
| `POST` | `/trades/compliance-check` | Pre-trade check — what the gateway calls |

The trade routes exist in two places (`PortfolioController` and `TradesController`) for historical
reasons; the gateway uses the `/trades` pair.

---

## Running locally

Requirements: .NET SDK 10, Node.js 20+, SQL Server.

**1. Database** — set the connection string in `engine/appsettings.Development.json` (gitignored), then:

```bash
cd engine
dotnet ef database update
```

**2. Engine** — `http://localhost:5000`:

```bash
cd engine
dotnet run
```

**3. Gateway** — `http://localhost:3000`:

```bash
cd mcp-server
npm install
npm start
```

Create `mcp-server/.env` first:

```
GATEWAY_SECRET=<32+ random bytes, hex>
COMPLIANCE_API_URL=http://localhost:5000
PORT=3000
```

Generate the secret with:

```bash
node -e "console.log(require('crypto').randomBytes(32).toString('hex'))"
```

Then point a client at `http://localhost:3000/mcp` using the same config shape as above.

**Testing:**

```bash
cd mcp-server && npm run smoke   # OAuth flow, all tools, tenant isolation — both services must be running
cd engine && dotnet test         # scaffold only, no real coverage yet
```

`npm run smoke` is the fastest way to tell whether a change broke anything: it performs discovery,
dynamic registration, a PKCE authorization, signup, token exchange, calls every tool, checks that
replayed authorization codes and forged login requests are rejected, and verifies cross-tenant
isolation.

---

## Configuration

Both services ship as containers. Build contexts differ — the engine's Dockerfile expects the
repository root, the gateway's expects `mcp-server/`:

```bash
docker build -f engine/Dockerfile -t engine .
docker build -f mcp-server/Dockerfile -t gateway mcp-server
```

| Variable | Service | Notes |
| --- | --- | --- |
| `ConnectionStrings__Default` | engine | Note the double underscore. Use `Authentication=Active Directory Managed Identity` so no password exists |
| `ASPNETCORE_ENVIRONMENT` | engine | `Production` — otherwise Swagger and developer exception pages are exposed |
| `ASPNETCORE_URLS` | engine | Must match the ingress target port |
| `PUBLIC_URL` | gateway | Public HTTPS URL. OAuth 2.1 requires an HTTPS issuer; this host is allowed automatically |
| `ALLOWED_HOSTS` | gateway | Comma-separated extra hostnames the MCP endpoint answers to. Anything not listed is rejected by DNS-rebinding protection |
| `COMPLIANCE_API_URL` | gateway | Internal address of the engine, e.g. `http://engine` |
| `PORT` | gateway | Must match the ingress target port |
| `GATEWAY_SECRET` | gateway | From Key Vault. Signs in-flight login requests; must be stable across restarts |

Both containers currently run as root, because the ingress ports in use (80 and 30) are privileged
and a non-root user cannot bind them.

Schema changes are applied as a script rather than migrating at startup, which would race across
replicas:

```bash
cd engine
dotnet ef migrations script --idempotent -o migrate.sql
```

Run the result in the Azure portal's SQL **Query editor**. It is idempotent, so re-running is safe.
The engine's managed identity needs a contained user in the database:

```sql
CREATE USER [engine] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [engine];
ALTER ROLE db_datawriter ADD MEMBER [engine];
```

---

## Operating it

**Health checks**

- `https://<gateway>/health` → `{"status":"ok","engine":"available","sessions":N}`
- The engine's own `/health` returns `{"status":"unhealthy","reason":"<ExceptionType>"}` when the
  database is unreachable; the full exception goes to the container logs

**Logs** — Container App → Monitoring → Log stream for live output, or Monitoring → Logs for history:

```kusto
ContainerAppConsoleLogs_CL
| where ContainerAppName_s == "engine"
| order by TimeGenerated desc
| take 100
| project TimeGenerated, RevisionName_s, Log_s
```

Swap to `ContainerAppSystemLogs_CL` for platform events — image pulls, probe failures, secret
resolution.

**Common failures**

| Symptom | Cause |
| --- | --- |
| `upstream connect error ... Connection refused` | Ingress target port does not match the port the app binds |
| `SocketException` from `/health` | Engine cannot reach SQL — wrong server name, or the serverless DB is paused |
| `Win32Exception` from `/health` | Managed identity failed; `DefaultAzureCredential` fell through to the Azure CLI. Pin the identity explicitly |
| `Login failed for user '<token-identified principal>'` | Token acquired, but the contained SQL user was never created |
| `Invalid object name 'Tenants'` | `migrate.sql` has not been run |
| 401 loops in the MCP client | Gateway restarted and lost its tokens; clear `~/.mcp-auth` and reconnect |

---

## Known gaps

- **The gateway must run as a single replica.** Issued tokens live in memory and DCR client
  registrations in a container-local `.data/`, so a second replica rejects tokens it did not issue,
  and every deploy forces users to sign in again. Moving grants into the database is the fix.
- **`PortfolioController`, `RulesController` and `TradesController` still return exception messages**
  to callers. `TenantsController` no longer does.
- **The gateway has no per-request logging**, so Application Insights shows only platform metrics for
  it, and OAuth failures are invisible unless they happen to print.
- **The engine has no Application Insights SDK** wired up yet, so the connection string it is given
  produces no telemetry.
- **Trade-check logic is duplicated** across `PortfolioController` and `TradesController`.
- **`ComplianceEngine.Tests` is an empty scaffold.** The real coverage today is
  `mcp-server/smoke-test.mjs`, which needs both services running.
