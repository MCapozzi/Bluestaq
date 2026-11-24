# Bluestaq

Note taking back-end service for sharing/collaborating on notes.

A RESTful ASP.NET Core (.NET 10) API that lets multiple users create, read, update and delete their own notes.
Data is persisted in MySQL via EF Core (Oracle's `MySql.EntityFrameworkCore` provider), and every request is
authorized against a server-side session stored in the database.

## Running

### Docker Compose (API + MySQL)

```bash
docker compose up --build
# API on http://localhost:8080, MySQL on localhost:3306
```

### Containerized / cloud deployment

`docker-compose.prod.yml` builds the published API image and runs it with MySQL. Secrets come from the environment
or a `.env` file (see `.env.example`); compose refuses to start if `MYSQL_ROOT_PASSWORD` or `MYSQL_PASSWORD` is unset.

```bash
cp .env.example .env            # set real passwords
docker compose -f docker-compose.prod.yml up -d --build
curl http://<host>:8080/health
```

- MySQL is only on the internal `backend` network (not published); data lives in the `mysql-data` volume.
- The API container runs as non-root with a read-only filesystem and `restart: unless-stopped`; migrations run at
  startup (`MIGRATE_ON_STARTUP`).
- To ship to a registry: set `API_IMAGE=registry.example.com/bluestaq-notes-api:1.0.0`, then
  `docker compose -f docker-compose.prod.yml build api && docker compose -f docker-compose.prod.yml push api`, and on
  the target host `pull` + `up -d`.
- Put the API behind a TLS-terminating reverse proxy / load balancer and set `FORWARDED_HEADERS_ENABLED=true` so
  rate limiting sees real client IPs and cookies are marked `Secure`.

### Locally

Requires the .NET 10 SDK and a MySQL 8 server. `appsettings.Development.json` points at
`Server=localhost;Database=notes;User=notes;Password=notespw`, which matches the compose `mysql` service:

```bash
docker compose up -d mysql
dotnet run --project src/Bluestaq.Notes.Api   # http://localhost:5188
```

The schema is applied by EF Core migrations on startup when `Database:MigrateOnStartup` is `true`
(on in Development and compose). To apply them manually instead: `dotnet tool restore && dotnet ef database update -p src/Bluestaq.Notes.Api`.

In Development the OpenAPI document is served at `/openapi/v1.json`. `src/Bluestaq.Notes.Api/Bluestaq.Notes.Api.http`
has ready-made requests for every endpoint.

### Configuration

| Key | Default | Purpose |
| --- | --- | --- |
| `ConnectionStrings:Notes` | _(required)_ | MySQL connection string |
| `Database:MigrateOnStartup` | `false` | Apply pending migrations at startup |
| `Sessions:IdleTimeout` | `08:00:00` | Session lifetime; slides forward on activity |
| `Sessions:CookieName` | `notes_session` | Name of the session cookie |
| `RateLimiting:Auth:PermitLimit` | `10` | Login/register requests allowed per client IP per window |
| `RateLimiting:Auth:Window` | `00:01:00` | Rate-limit window; excess requests get 429 |

Use environment variables in containers, e.g. `ConnectionStrings__Notes=...`.

## API

All endpoints except `register`, `login` and `/health` require a valid session. Errors use RFC 9457 problem details.

| Method | Path | Description | Success |
| --- | --- | --- | --- |
| POST | `/api/auth/register` | Create a user `{ username, password }` | 201 |
| POST | `/api/auth/login` | Start a session `{ username, password }` → `{ token, expiresAt, user }` + session cookie | 200 |
| POST | `/api/auth/logout` | Revoke the current session | 204 |
| GET | `/api/auth/me` | Current user | 200 |
| GET | `/api/notes?page=1&pageSize=20` | List own notes, newest first (`pageSize` ≤ 100) | 200 |
| GET | `/api/notes/shared?page=1&pageSize=20` | Other users' notes you can read (shared with you or public) | 200 |
| GET | `/api/notes/{id}` | Get a note you can read | 200 |
| POST | `/api/notes` | Create `{ title, content }` | 201 |
| PUT | `/api/notes/{id}` | Replace `{ title, content }` | 200 |
| DELETE | `/api/notes/{id}` | Delete a note | 204 |
| POST | `/api/notes/export` | Bulk export notes you can read (see below) | 200 |
| GET | `/api/notes/{id}/sharing` | Visibility and share list (owner only) | 200 |
| PUT | `/api/notes/{id}/visibility` | `{ "visibility": "public" \| "private" }` (owner only) | 200 |
| POST | `/api/notes/{id}/shares` | Share with a user `{ username }`; idempotent (owner only) | 200 |
| DELETE | `/api/notes/{id}/shares/{username}` | Revoke a user's access; idempotent (owner only) | 200 |

Validation: username 3–50 chars of `[A-Za-z0-9_.-]` (case-insensitive, stored lowercase); password 8–128 chars;
title 1–200 chars; content up to 100,000 chars.

### Audit trail

Every note carries `createdByUserId`, `createdAt`, `updatedByUserId` and `updatedAt` (UTC). They are stamped by an
EF Core `SaveChangesInterceptor` (`AuditInterceptor`) from the authenticated user and the server clock on every insert
and update, so clients can't set or forge them, and `created*` can never change after insert. All note responses
include them, along with `ownerId`.

### Sharing

Each note has a `visibility` (`private` by default, or `public`) and a list of users it is shared with
(`note_shares`). A user can **read** a note if they own it, it is public, or it is shared with them. Only the owner can
update, delete, or change sharing; a user who can read a note but doesn't own it gets **403** for those, and a user who
can't read it gets **404**. Deleting a note or user removes its shares.

### Bulk export

`POST /api/notes/export` takes exactly one selector:

| Body | Exports |
| --- | --- |
| `{ "noteIds": [1, 2, 3] }` | Those notes (up to 1,000 IDs). IDs that don't exist or you can't read are listed in `notFoundIds`. |
| `{ "userId": 42 }` | Notes owned by user 42 that you can read (all of them if 42 is you). |
| `{ "from": "2026-01-01T00:00:00Z", "to": "2026-01-31T23:59:59Z" }` | Notes you can read created in that range (inclusive; timestamps without an offset are UTC). |

Add `"format": "csv"` to get a `text/csv` download (`notes-export-<timestamp>.csv`) instead of JSON
`{ exportedAt, count, notes, notFoundIds }`. CSV cells starting with `= + - @` are prefixed with `'` to prevent
spreadsheet formula injection. Supplying zero or more than one selector, or an incomplete/inverted range, returns 400.

### Example

```bash
curl -s -X POST localhost:8080/api/auth/register -H 'Content-Type: application/json' \
  -d '{"username":"alice","password":"password123"}'
TOKEN=$(curl -s -X POST localhost:8080/api/auth/login -H 'Content-Type: application/json' \
  -d '{"username":"alice","password":"password123"}' | jq -r .token)
curl -s -X POST localhost:8080/api/notes -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"title":"Groceries","content":"Milk, eggs"}'
curl -s localhost:8080/api/notes -H "Authorization: Bearer $TOKEN"
```

## Authorization model

- **Login** verifies the password (ASP.NET Core Identity `PasswordHasher`, PBKDF2) and creates a row in `sessions`
  holding a SHA-256 hash of a random 256-bit token. Only the hash is stored, so a DB leak doesn't expose usable tokens.
- The token is returned in the response body (for API clients, sent as `Authorization: Bearer <token>`) and as an
  `HttpOnly`, `SameSite=Strict` cookie (for browsers; `Secure` when served over HTTPS).
- A custom authentication handler (`SessionAuthenticationHandler`) resolves the token to an unexpired session on every
  request and slides its expiry forward. A global fallback policy requires an authenticated user on every endpoint.
- **Logout** deletes the session row, so the token stops working immediately.
- Notes are always queried through `ReadableBy(userId)` (own, public, or shared). A note you can't read returns
  **404**, not 403, so note ids can't be probed.
- `login` and `register` are rate limited per client IP to slow password guessing.

## Project layout

```
src/Bluestaq.Notes.Api
  Auth/          session service, authentication handler, options
  Controllers/   AuthController, NotesController
  Contracts/     request/response DTOs and validation limits
  Data/          NotesDbContext, AuditInterceptor and EF Core migrations
  Domain/        User, Note, Session entities
  Export/        NoteExporter (query) and NoteCsvWriter
tests/Bluestaq.Notes.Api.UnitTests  unit tests (in-memory SQLite, fake clock), with a 70% line-coverage gate
tests/Bluestaq.Notes.Api.Tests      integration tests (WebApplicationFactory + Testcontainers MySQL)
```

## Tests

```bash
dotnet test
```

Integration tests run the real API in-process against a throwaway MySQL container, so Docker must be running.
Set `NOTES_TEST_MYSQL_IMAGE` to use a different MySQL image.

Unit tests need no Docker. To enforce the line-coverage gate (fails below 70%; migrations and generated code excluded):

```bash
dotnet test tests/Bluestaq.Notes.Api.UnitTests /p:CollectCoverage=true
```

## Possible next steps

- Edit permission for shared users (currently shares are read-only).
- Optimistic concurrency (`ETag`/`If-Match`) for simultaneous edits.
- Per-account lockout, and a background job to purge expired sessions.
- An admin role that may export other users' notes.
- TLS termination (reverse proxy or Kestrel certificates) for non-local deployments.
- Search and tagging.
