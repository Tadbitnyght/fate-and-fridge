# Fate & Fridge — Security Specification

## 1. Security Goals
- Safe default Docker deployment
- Strong local password handling
- Server-side authorization
- Protected historical data
- Secure recipe URL fetching
- Secure backup/restore
- No telemetry by default
- No dependence on obscurity

---

## 2. Authentication
Multiuser mode uses:
- local username/password
- Argon2id password hashing (libsodium binding; PHC-encoded parameters; rehash on login when parameters change)
- opaque DB-backed session cookies (`user_sessions` stores only a token hash)
- HttpOnly, SameSite=Lax cookies; `Secure` when served over HTTPS
- antiforgery tokens on unsafe methods
- 30-day idle / 90-day absolute session expiration
- rotation on login, password change, and privilege change
- all sessions revoked on password reset or user disable

Password policy (V1): minimum 12 characters, maximum 128, no composition rules, pasting allowed.

Never store plaintext passwords.

---

## 3. Login Protection
Track:
- successful login
- failed login

Apply (V1 defaults):
- 5 consecutive failures → progressive delay
- 10 failures → 15-minute account lock
- 5 lockouts within 24 hours → 24-hour lock
- 20 failures per source IP per 10 minutes → HTTP 429 with `Retry-After`
- generic `INVALID_CREDENTIALS` responses so login never reveals whether a username exists
- `account_locked` auth event whenever a lock engages

Lockout target:
- configurable 24 or 48 hour maximum lock window after severe repeated abuse

Avoid easy denial-of-service against arbitrary accounts; design thresholds carefully.

---

## 4. Authorization
Never rely on hidden buttons.

Backend checks permissions for every protected endpoint.

Admin-only:
- user management
- permission changes
- audit read
- historical correction
- hard recipe delete
- backup/restore
- global security settings

---

## 5. Single-User No-Login Mode
Allowed.

Setup should warn:
- use only on trusted network unless protected externally
- do not expose unauthenticated instance directly to internet

Single-user requests carry no session cookie and no antiforgery token: CSRF validation applies only to cookie-authenticated sessions (multiuser). In single-user mode any client that can reach the port acts as Admin, so network restriction (trusted LAN, IP allowlist) is the only protection; the wizard shows this warning.

Optional IP allowlist may be enabled.

V1 allowlist behavior:
- applies app-wide except `/api/v1/health`
- loopback (`127.0.0.1`, `::1`) is always allowed for the container healthcheck
- IPv4 and IPv6 CIDR ranges
- blocked clients receive HTTP 403
- `X-Forwarded-For` is honored only when the immediate peer is a configured trusted proxy (CIDR list)
- enabling requires explicit confirmation and a warning because a misconfigured allowlist can lock out the household

IP allowlist must not replace authentication when multiuser mode is active.

---

## 6. Recipe Import / SSRF
Recipe import is high-risk because server performs outbound HTTP requests.

Reject or tightly control:
- localhost
- loopback addresses
- RFC1918 private address ranges
- link-local ranges
- cloud metadata IPs
- Docker/internal hostnames
- file://
- ftp://
- gopher://
- other non-http(s) schemes

For DNS:
- resolve before request
- re-check redirect targets
- defend against DNS rebinding where practical

---

## 7. Import Fetch Limits
Enforce:
- HTTP/HTTPS only
- connection timeout
- read timeout
- maximum redirects
- maximum HTML response size
- maximum image size
- safe content types
- max total fetch count per import
- user-agent identification

Do not execute third-party JavaScript.

Do not run browser automation merely to parse recipes in V1.

---

## 8. Imported Content Sanitization
Treat all imported:
- HTML
- descriptions
- instructions
- author/source text

as untrusted.

Strip/sanitize dangerous markup.

Prefer plain text or restricted safe markup.

Never render arbitrary source HTML directly.

---

## 9. File Uploads
For recipe images:
- validate MIME type
- validate extension
- inspect actual file signature
- enforce maximum dimensions/filesize
- randomize stored filenames
- prevent path traversal
- serve from safe static path
- do not execute uploads

---

## 10. Historical Data
Historical immutability is server-enforced.

Admin correction:
- separate endpoint
- admin role required
- reason required
- confirmation required
- audit event required

Do not allow ordinary calendar update endpoints to accept past dates.

---

## 11. Backup Encryption
Backup archive must be encrypted.

Recovery key:
- generated during setup as 32 random bytes, base64url
- stored at `/data/config/recovery.key` with file mode `0600`
- returned to the browser exactly once at setup completion
- exportable separately by an Admin
- not displayed casually
- never logged

Do not use a key stored only inside ephemeral container data.

---

## 12. Restore Security
Restore:
- Admin only
- verify archive integrity
- verify expected format/version
- verify recovery key
- create safety backup first
- block path traversal in archive extraction
- extract into staging area
- validate before replacing active data

Never blindly unpack untrusted archive paths.

---

## 13. Secrets
Secrets should come from:
- environment variables
- Docker secrets where practical
- mounted persistent config

Do not commit:
- passwords
- recovery keys
- secret keys
- API tokens

`.env.example` contains placeholders only.

---

## 14. Database
- PostgreSQL not exposed publicly by default
- app uses least-privileged DB account needed
- parameterized ORM queries
- migrations reviewed
- destructive migrations backed up first

---

## 15. Web Security
Require:
- CSRF defense if cookie sessions
- secure cookie flags
- SameSite policy
- CSP where practical
- X-Frame-Options/frame-ancestors policy
- content-type headers
- strict request validation
- output escaping

---

## 16. WebSocket Security
WebSocket connection must authenticate.

Enforce:
- household/user session
- authorization
- origin checks where appropriate
- message size limits
- rate limits for client-originated actions

Prefer sockets for notifications, not arbitrary remote procedure calls.

---

## 17. Audit Log
Audit log is Admin-only.

Never log:
- plaintext passwords
- password reset secrets
- backup recovery key
- full auth token

Log meaningful context:
- actor
- event
- target
- timestamp
- IP
- before/after when appropriate

---

## 18. Update Checker
No telemetry.

Do not send:
- household name
- database id
- recipe count
- users
- unique tracking identifier
- history

Only retrieve release metadata needed to determine availability.

---

## 19. Security Testing
Required:
- auth bypass tests
- permission matrix tests
- past-date mutation bypass tests
- SSRF tests
- malicious redirects
- malicious image upload
- path traversal
- rate limit behavior
- WebSocket auth
- restore archive path traversal
- corrupted backup handling
- dependency and container-image gates (§20)

---

## 20. Dependency and Image Gates

Three permanent CI gates (`.github/workflows/ci.yml`) run on every push and
pull request.

| Gate | Mechanism | Fails when |
|------|-----------|------------|
| NuGet | `dotnet restore` with `TreatWarningsAsErrors=true`; plus a `dotnet list package --vulnerable --include-transitive --format json` report artifact | any NuGet audit warning becomes an error (`NU1901`–`NU1905`): Low/Moderate/High/Critical, direct and transitive |
| npm | `npm audit --audit-level=high`; plus a JSON report artifact containing every severity | **any** High/Critical advisory, whether or not a fix exists (npm has no per-advisory ignore; clear it by upgrading) |
| Container image | pinned Trivy (tag **and** digest: `aquasec/trivy:0.76.1@sha256:790fd66d…`) over the built runtime image: a gate run `--severity HIGH,CRITICAL --ignore-unfixed --exit-code 1`, plus an **unfiltered all-severity** JSON report artifact | a High/Critical advisory in the shipped image that has a fix |

Policy:

- **High/Critical fails the build.** NuGet and npm fail on severity alone (fix
  or no fix; the NuGet gate is stricter still, see below), and the image gate
  fails on every High/Critical advisory that has a fix. Fix the dependency, the
  base image or the package; do not add suppression to make CI green.
- **Medium/Low are reported and tracked, never ignored.** The npm JSON artifact
  contains every severity, and the image job records an unfiltered all-severity
  report alongside the gated run; neither fails. The NuGet gate is stricter than
  the minimum policy: because the repo builds with
  `TreatWarningsAsErrors=true`, any audit warning — including Moderate/Low —
  fails `dotnet restore`.
- **High/Critical without a fix** may be accepted only as an explicit
  vulnerability exception recorded with the package or image, the advisory id,
  the severity, why there is no fix, and a review date. The acceptance must be
  explicit:
  - NuGet: a `NuGetAuditSuppress` item on the advisory URL in the affected
    project (never a blanket `NoWarn`).
  - npm: there is **no advisory-ignore mechanism**. Upgrade the package or pin a
    patched transitive through a root `overrides` entry. If no patched version
    exists, the gate cannot pass: the audit level must be changed explicitly in
    the same commit as the exception row, and that decision is re-reviewed when a
    fix ships.
  - Image: `--ignore-unfixed` keeps no-fix advisories out of the gate, so the
    exception row is the only record — it is mandatory.
  - Exceptions are re-reviewed when a fix ships and at every release review.
- **Never scan or run the scanner on the production host.** The image scan runs
  on a CI runner and scans the locally built image; `--insecure` registry
  options and production credentials are not used.
- **Nothing is added to the runtime image for scanning.** The scanner is a CI
  container, not a runtime dependency; the runtime image stays product-only
  (plus the approved `postgresql-client-17`). Test-only packages must never
  reach the final stage.
- Scanner version bumps are deliberate changes: update the pinned tag, record
  the new results, and re-run the gate.
