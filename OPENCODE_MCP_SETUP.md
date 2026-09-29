# OpenCode MCP Setup — Fate & Fridge

This configuration targets current OpenCode V2-style configuration.

## Files

Copy:

```text
opencode.jsonc
.opencode/agents/lead.md
.opencode/agents/implementer.md
.opencode/agents/reviewer.md
.opencode/agents/docker-admin.md
```

into the Fate & Fridge repository root.

## 1. GitHub MCP

The config uses GitHub's hosted MCP endpoint.

Export a GitHub PAT before launching OpenCode:

```bash
export GITHUB_PERSONAL_ACCESS_TOKEN='YOUR_TOKEN'
```

Do not place the token in the repository.

A fine-grained token should be limited to the Fate & Fridge repository and only the permissions required for the tasks you want agents to perform.

Verify:

```bash
opencode mcp list
```

## 2. Roslyn MCP

Requires .NET 10/11 SDK for the global tool install.

Install:

```bash
dotnet tool install --global MadQ.RoslynMcp --prerelease
```

Then:

```bash
madq-roslynmcp setup
```

The project config already includes `madq-roslynmcp`, so setup is mainly useful for validating installation/client detection.

Verify command:

```bash
madq-roslynmcp --help
```

## 3. Playwright MCP

The config launches it with:

```bash
npx -y @playwright/mcp@latest
```

You need Node/npm available.

Test indirectly with:

```bash
opencode mcp list
```

## 4. Docker MCP Gateway

Requires a Docker installation that includes the MCP CLI / Gateway.

Verify:

```bash
docker mcp gateway run --dry-run
```

The `docker-admin` agent is the only provided agent that can see Docker MCP tools.

Normal app development should prefer ordinary:

```bash
docker compose build
docker compose up -d
docker compose ps
docker compose logs
```

This avoids loading a broad MCP catalog into ordinary coding sessions.

## 5. Why MCPs are denied globally

MCP servers can expose many tool definitions. Those definitions consume model context.

The config therefore:
- connects the servers once
- globally denies their tool families
- re-enables only the MCP families required by each agent

This keeps a Reviewer from loading unrelated Docker tooling, and keeps the Lead from loading Playwright when architecture work does not need browser automation.

## 6. Agent MCP Matrix

| Agent | GitHub | Roslyn | Playwright | Docker MCP |
|---|---:|---:|---:|---:|
| Lead | Yes | Yes | No | No |
| Implementer | Yes | Yes | Yes | No |
| Reviewer | Yes | Yes | Yes | No |
| Docker Admin | No | No | No | Yes |

## 7. Further token savings

For GitHub MCP, restrict server-side toolsets when possible.

Suggested starting toolsets:

```text
context,repos,issues,pull_requests,actions
```

Do not enable every GitHub toolset unless needed.

Also:
- Prefer Roslyn member/symbol tools over whole-file reads for C#.
- Keep Docker MCP isolated.
- Do not add a generic filesystem MCP when OpenCode's native file tools already work.
- Do not add a PostgreSQL MCP initially; use EF Core migrations as schema authority and `psql`/integration tests when inspection is needed.
- Add specialized MCPs only when they solve a recurring problem.

## 8. GitHub Write Safety

The supplied configuration denies `git push` via shell.

GitHub MCP may itself expose write operations depending on your token/toolsets. If you want strict separation, use a read-only GitHub MCP server/profile for the Reviewer or use a GitHub token with read-only repository permissions.

## 9. OpenCode Version Note

OpenCode has changed configuration syntax between V1 and V2. This package uses V2-style:
- `mcp.servers`
- `agents`
- ordered `permissions`
- `shell`
- `subagent`

If your installed OpenCode only recognizes the older V1 configuration fields, update OpenCode before using this file.
