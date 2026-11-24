# Bluestaq

Note taking back-end service for sharing/collaborating on notes.

A RESTful ASP.NET Core (.NET 10) API that lets multiple users create, read, update and delete their own notes.
Data is persisted in MySQL via EF Core (Oracle's `MySql.EntityFrameworkCore` provider), and every request is
authorized against a server-side session stored in the database.

## Features

- **Notes CRUD** for many users, each seeing only the notes they may read.
- **Session authentication**: server-side sessions via cookie or `Authorization: Bearer` token, sliding expiry.
- **Sharing**: notes are private by default; make one public (any logged-in user) or share it read-only with users.
- **Bulk export** by note IDs, owner or creation date range, as JSON or CSV.
- **Bulk import** of an export file (JSON or CSV), e.g. to move notes between instances.
- **Audit trail** on every note: who created and last updated it, and when.
- **Administrators** can reach every note; all their actions go to a database table and a separate log file.
- **WebSocket alerts** when a note is shared with you or a shared note changes, at most one push per 30 minutes.
- **Daily email digests** at midnight Eastern (opt-in) of shared notes and/or public notes that are new or changed.
- **Docker Compose** files for local development and for deployment.
- **Tests**: unit tests with coverage gates and MySQL integration tests (see [Tests](#tests)).

## Running

### Docker Compose (API + MySQL)

```bash
docker compose up --build
# API on http://localhost:8080, MySQL on localhost:3306, digest emails in Mailpit at http://localhost:8025
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
| `Database:Password` | _(none)_ | If set, overrides the password in `ConnectionStrings:Notes` (safe for any characters) |
| `Admin:BootstrapUsername` / `Admin:BootstrapPassword` | _(empty)_ | Create (or promote) this administrator at startup |
| `AdminAudit:LogFilePath` | `logs/admin-audit-.log` | Admin audit JSON-lines file, rolled daily (date inserted before `.log`) |
| `AdminAudit:RetainedFileCount` | `90` | Number of daily admin audit files kept |
| `Alerts:PushInterval` | `00:30:00` | Minimum time between alert pushes to the same user |
| `Alerts:PollInterval` | `00:00:05` | How often connected users are checked for due alerts |
| `Alerts:SendTimeout` | `00:00:10` | A socket that doesn't accept an alert within this time is dropped |
| `Export:MaxNotes` | `10000` | Largest export allowed |
| `Import:MaxNotes` | `10000` | Largest import allowed |
| `Alerts:AllowedOrigins` | _(empty)_ | Extra browser origins allowed to open a cookie-authenticated alert socket |
| `Digest:Smtp:Host` | _(empty)_ | SMTP server for digest emails; digests are not sent while empty |
| `Digest:Smtp:Port` / `Digest:Smtp:Security` | `25` / `Auto` | SMTP port and TLS mode (`None`, `Auto`, `StartTls`, `SslOnConnect`) |
| `Digest:Smtp:Username` / `Digest:Smtp:Password` | _(empty)_ | SMTP credentials, if the server needs them |
| `Digest:From` | `notes@bluestaq.local` | Sender address of digest emails |
| `Digest:TimeZone` | `America/New_York` | Digests go out at midnight here and cover the day that just ended |
| `Digest:SendWindow` | `01:00:00` | How long after midnight a digest can still go out (restarts, retries) |
| `Digest:PollInterval` | `00:01:00` | How often the scheduler checks for digests to send |

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
| POST | `/api/notes/import` | Bulk import an export file as your own new notes (see below) | 201 |
| GET | `/api/notes/{id}/sharing` | Visibility and share list (owner only) | 200 |
| PUT | `/api/notes/{id}/visibility` | `{ "visibility": "public" \| "private" }` (owner only) | 200 |
| POST | `/api/notes/{id}/shares` | Share with a user `{ username }`; idempotent (owner only) | 200 |
| DELETE | `/api/notes/{id}/shares/{username}` | Revoke a user's access; idempotent (owner only) | 200 |
| GET | `/api/digest` | Your digest opt-ins and next digest time | 200 |
| PUT | `/api/digest` | `{ "sharedNotes": bool, "publicNotes": bool }` | 200 |
| GET | `/api/digest/preview` | Tonight's digest so far (today's activity), without sending it | 200 |
| GET (WebSocket) | `/ws/alerts` | Receive share/update alerts (see below) | 101 |
| GET | `/health` | Liveness check (anonymous) | 200 |
| GET | `/api/admin/users` | All users with their roles (admin only) | 200 |
| PUT | `/api/admin/users/{id}/role` | `{ "role": "Administrator" \| "User" }`; can't change your own role (admin only) | 200 |
| GET | `/api/admin/notes?userId=&page=&pageSize=` | All notes, optionally for one owner (admin only) | 200 |
| GET | `/api/admin/audit-log?adminUserId=&noteId=&page=&pageSize=` | Admin action log, newest first (admin only) | 200 |

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

### Alerts (WebSocket)

Connect to `/ws/alerts` with your session (the `notes_session` cookie, or `Authorization: Bearer <token>` for non-browser
clients). The server pushes an alert when a note is **shared with you**, or when a note **shared with you is
edited** (title or content). You are never alerted about your own changes.

Alerts are stored in `note_alerts` as they happen and pushed **at most once per 30 minutes per user**: if nothing was
pushed to you in the last 30 minutes, pending alerts go out within a few seconds; otherwise they are combined and sent
when the 30 minutes are up. Alerts wait in the database while you're offline and are delivered when you connect.
Alerts for notes you can no longer read are dropped. A cookie-authenticated handshake is rejected with 403 unless its
`Origin` is the API's own or listed in `Alerts:AllowedOrigins` (behind a reverse proxy that rewrites `Host`, list the
public origin there, e.g. `https://notes.example.com`). When your session ends (logout or expiry) the socket is closed
with status `1008`.

```json
{
  "type": "alerts",
  "sentAt": "2026-11-24T22:30:00Z",
  "alerts": [
    {
      "noteId": 42, "title": "Roadmap", "sharedBy": "alice", "updatedBy": "alice",
      "lastUpdatedAt": "2026-11-24T22:28:13Z", "events": ["Shared", "Updated"], "eventCount": 3,
      "firstOccurredAt": "2026-11-24T22:01:05Z", "lastOccurredAt": "2026-11-24T22:28:13Z"
    }
  ]
}
```

`sharedBy` is whoever shared the note with you (the owner, or an administrator acting on it); `updatedBy` and
`lastUpdatedAt` describe the note's latest edit. Each API instance pushes only to the sockets connected to it, and a
push to any of a user's sockets counts as that user's push for the interval. With several API replicas, use sticky
sessions so all of a user's sockets land on the same replica; otherwise a socket on another replica can miss a push.

### Email digests

Users are **opted out** by default. `PUT /api/digest { "sharedNotes": true, "publicNotes": true }` opts in to either
or both sections; set both to `false` to opt out. Once a day, at **midnight Eastern** (`America/New_York`, so it
follows daylight saving), each opted-in user is emailed what happened during the day that just ended (midnight to
midnight):

- `sharedNotes`:
  - **Shared with you**: notes shared with you that day.
  - **Changed since they were shared with you**: shared notes whose title or content someone else edited that day.
- `publicNotes`:
  - **New public notes**: other users' notes that became public that day.
  - **Changed public notes**: other users' public notes whose title or content someone else edited that day.

Visibility changes don't count as edits, and your own edits are left out. A public note that is also shared with you is
listed only under the shared sections when you get them. Each entry has the note's title and ID, who shared or owns it,
and when and by whom it was last edited; only a note's latest edit is known.

**If an opted-in user has nothing new, no email is sent.** Digests only go out within `Digest:SendWindow` (1 hour)
after midnight; a day missed because the service was down longer than that is skipped, not sent late.

**For this project every digest is sent to `TEST@TEST.TEST`**, not to the user (users have no email address); the
subject and greeting name the user it is for. Mail goes over SMTP (`Digest:Smtp:*`); `docker compose up` includes
[Mailpit](https://mailpit.axllent.org/), which catches the mail and shows it at http://localhost:8025. Each user is
claimed with a conditional update before sending, so several API replicas don't send the same digest twice; a failed
send is retried on the next poll within the send window.

### Administrators

Users have a `role`: `User` (default) or `Administrator`. Registration always creates `User`s. The first administrator
comes from `Admin:BootstrapUsername`/`Admin:BootstrapPassword` at startup (created if missing; an existing regular
account is promoted only if the configured password matches it, and startup fails otherwise; passwords are never
changed); after that, administrators promote or demote others via `/api/admin`. A role
change takes effect on the user's next request.

Administrators can read, update, delete, export and change sharing on **any** note through the normal `/api/notes`
endpoints, regardless of owner, visibility or shares.

Every request made by an administrator, including reads, failed requests and their login, is recorded in two places,
separate from the application logs:

- the `admin_audit_log` table (no foreign keys, so entries outlive deleted users and notes), queryable via
  `GET /api/admin/audit-log`;
- a JSON-lines file (`AdminAudit:LogFilePath`, rolled daily). In the containers it is `/var/log/bluestaq`, a named volume.

Each entry has the admin's id and username, time, action (`Controller.Action`), HTTP method, path and query, target
note/user id, response status, client IP and trace id.

### Bulk export

`POST /api/notes/export` takes exactly one selector:

| Body | Exports |
| --- | --- |
| `{ "noteIds": [1, 2, 3] }` | Those notes (up to 1,000 IDs). IDs that don't exist or you can't read are listed in `notFoundIds`. |
| `{ "userId": 42 }` | Notes owned by user 42 that you can read (all of them if 42 is you). |
| `{ "from": "2026-01-01T00:00:00Z", "to": "2026-01-31T23:59:59Z" }` | Notes you can read created in that range (inclusive; timestamps without an offset are UTC). |

Add `"format": "csv"` to get a `text/csv` download (`notes-export-<timestamp>.csv`) instead of JSON
`{ exportedAt, count, notes, notFoundIds }`. CSV cells starting with `= + - @` (or `'`) are prefixed with `'` to prevent
spreadsheet formula injection. Supplying zero or more than one selector, or an incomplete/inverted range, returns 400. A selection matching more than
`Export:MaxNotes` notes (default 10,000) is rejected with 400 rather than buffered.

### Bulk import

`POST /api/notes/import` takes a file produced by `POST /api/notes/export`, from this instance or another one:

- **JSON** (`Content-Type: application/json`): the export document as-is (`{ exportedAt, count, notes, notFoundIds }`).
  Only `notes` is read, so `{ "notes": [{ "title": "...", "content": "...", "visibility": "public" }] }` works too.
- **CSV** (`Content-Type: text/csv`): the CSV export. Columns are matched by header name; `Title` and `Content` are
  required, `Id` and `Visibility` optional, others ignored. The export's `'` formula guard is removed again.

What is imported:

- Each note becomes a **new note owned by you**, with a new ID, so IDs from another instance can't clash.
- `title`, `content` and `visibility` are kept (`private` if missing). Shares aren't part of the export, so imported
  notes have none.
- `ownerId` and the audit fields in the file are ignored, because those user IDs belong to the source instance. The
  audit fields are stamped as you creating the notes at import time.
- The response (201) is `{ importedAt, count, notes: [{ sourceId, note }] }`, where `sourceId` is the note's `id` in
  the file, so you can map old IDs to new ones.

An import is all-or-nothing: if any note is invalid (same limits as creating a note), or the file has more than
`Import:MaxNotes` notes (default 10,000), or the CSV is malformed, nothing is saved and you get 400 with errors keyed
by position, e.g. `Notes[3].Title`.

Moving notes between instances:

```bash
curl -s -X POST https://old.example/api/notes/export -H "Authorization: Bearer $OLD" \
  -H 'Content-Type: application/json' -d "{\"userId\": $MY_OLD_ID}" > notes.json
curl -s -X POST https://new.example/api/notes/import -H "Authorization: Bearer $NEW" \
  -H 'Content-Type: application/json' --data-binary @notes.json
```

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
- Notes are always queried through `VisibleTo(userId, isAdmin)`: everything for administrators, otherwise
  `ReadableBy(userId)` (own, public, or shared). A note you can't read returns
  **404**, not 403, so note ids can't be probed.
- `login` and `register` are rate limited per client IP to slow password guessing.

## Project layout

```
src/Bluestaq.Notes.Api
  Admin/         bootstrapper, admin audit middleware, DB + file audit loggers
  Alerts/        alert interceptor, aggregation service, WebSocket endpoint and dispatcher
  Auth/          session service, authentication handler, roles, options
  Controllers/   AuthController, NotesController, NoteSharingController, DigestController, AdminController
  Contracts/     request/response DTOs and validation limits
  Digest/        digest builder and scheduler, SMTP email sender
  Data/          NotesDbContext, AuditInterceptor and EF Core migrations
  Domain/        User, Note, NoteShare, NoteAlert, Session, AdminAuditEntry entities; NoteAccess.ReadableBy
  Export/        NoteExporter (query) and NoteCsvWriter
  Import/        NoteImporter and NoteCsvReader
scripts/         check-import-coverage.py (import coverage gate)
tests/Bluestaq.Notes.Api.UnitTests  unit tests (in-memory SQLite, fake clock), with line-coverage gates
tests/Bluestaq.Notes.Api.Tests      integration tests (WebApplicationFactory + Testcontainers MySQL)
```

## Tests

```bash
dotnet test
```

Integration tests run the real API in-process against a throwaway MySQL container, so Docker must be running.
Set `NOTES_TEST_MYSQL_IMAGE` to use a different MySQL image.

Unit tests need no Docker. Every endpoint has at least one happy-path integration test.

### Coverage targets

| Scope | Target (line coverage) | Enforced by |
| --- | --- | --- |
| Whole API (migrations and generated code excluded) | 70% | coverlet threshold in the unit test project |
| Bulk import (`Import/`, `ImportContracts.cs`, the import actions in `NotesController`) | 90% | `scripts/check-import-coverage.py` |

Both gates run in CI. To run them locally:

```bash
dotnet test tests/Bluestaq.Notes.Api.UnitTests /p:CollectCoverage=true
python3 scripts/check-import-coverage.py
```

## Possible next steps

- Edit permission for shared users (currently shares are read-only).
- Optimistic concurrency (`ETag`/`If-Match`) for simultaneous edits.
- Per-account lockout, and a background job to purge expired sessions.
- TLS termination (reverse proxy or Kestrel certificates) for non-local deployments.
- Search and tagging.
