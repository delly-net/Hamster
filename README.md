# Hamster

<div align="center">

A personal accounting assistant built with modern web technologies.

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![Vue.js](https://img.shields.io/badge/Vue.js-3.5-4FC08D.svg)](https://vuejs.org)
[![TypeScript](https://img.shields.io/badge/TypeScript-6.0-3178C6.svg)](https://www.typescriptlang.org)

[English](README.md) | [中文](README.zh-CN.md)

</div>

## Overview

Hamster is a full-stack personal accounting assistant designed to help you manage your finances with ease. The project features a modern, responsive frontend built with Vue 3 and TypeScript, backed by a high-performance ASP.NET Core API.

## Project Structure

```
Hamster/
├── api/     # ASP.NET Core backend (.NET 10 + Minimal API)
├── ui/      # Vue 3 frontend (TypeScript + Vite)
└── doc/     # Logo and icon assets
```

## Tech Stack

### Backend ([api/](api/))
- **Framework**: ASP.NET Core 10.0 (.NET 10)
- **API Style**: Minimal APIs, with endpoints auto-registered via the `IEndpoint` convention
- **OpenAPI**: `AddOpenApi()` / `MapOpenApi()` in development (`/openapi/v1.json`)
- **Data Access**: SqlSugar ORM over **SQLite** (default) or **PostgreSQL**
- **Auth**: JWT bearer tokens (1-day lifetime), passwords hashed with PBKDF2
- **Layered as**: `Config/` · `Data/` · `Security/` · `Services/` · `Endpoints/`

### Frontend ([ui/](ui/))
- **Framework**: Vue 3.5 with Composition API
- **Language**: TypeScript 6.0
- **Build Tool**: Vite 8
- **State Management**: Pinia 4
- **Routing**: Vue Router 5
- **Linting / Formatting**: ESLint + oxlint + Prettier
- **Package Manager**: pnpm

## Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Node.js](https://nodejs.org/) ^22.18.0 || >=24.12.0
- [pnpm](https://pnpm.io/) (recommended package manager)
- [PostgreSQL](https://www.postgresql.org/) (optional — SQLite is the default and needs no setup)

## Getting Started

### Backend Setup

```bash
cd api

# Run the development server (SQLite by default, no setup needed)
dotnet run

# Build the project
dotnet build

# Publish for production
dotnet publish -c Release
```

The API listens on `http://localhost:5004` by default. On startup it prints the active database
type, the (masked) connection string, the SQLite file path, and the JWT signing key.

#### Database

SQLite is the default and needs no setup — the file `hamster.db` is created in the **current
working directory** on first start. Tables are created automatically via SqlSugar CodeFirst.

```bash
# Use PostgreSQL instead (connection string is optional if it matches the default)
export HAMSTER_DB_TYPE=PostgreSql
export HAMSTER_DB_CONNECTION="Host=localhost;Port=5432;Database=hamster;Username=postgres;Password=postgres"
```

| Environment variable | Default | Description |
|---|---|---|
| `HAMSTER_DB_TYPE` | `Sqlite` | `Sqlite` or `PostgreSql` |
| `HAMSTER_DB_CONNECTION` | *(empty)* | Connection string; when empty, a default is derived from the DB type |
| `HAMSTER_DB_AUTOMIGRATE` | `true` | Run CodeFirst table creation at startup |

Environment variables always take precedence over `appsettings.json`. If auto-migration fails
(e.g. the database is unreachable) the API logs a warning and keeps starting.

Health probes: `GET /health` (liveness, no database access) and `GET /health/db` (database
connectivity, returns `503` when unavailable).

#### Authentication

Login uses JWT bearer tokens with a **fixed 1-day lifetime**. Passwords are stored as PBKDF2
(HMAC-SHA256) hashes with a random per-user salt.

```bash
# Optional: pin the signing key (otherwise a random key is generated at startup)
export HAMSTER_JWT_KEY="<at least 32 bytes of random data>"
```

Without `HAMSTER_JWT_KEY`, a random key is generated on every start and printed to the console —
tokens issued before a restart become invalid. Set the variable in any long-running deployment.

##### Default administrator

The first start seeds a system administrator so a fresh checkout is usable right away:

> [!WARNING]
> The default credentials **`admin` / `admin123`** are for out-of-the-box use only. Before
> exposing an instance to anyone else, either override them at first start (variables below) or
> log in and change the password. Seeding only ever *creates* a missing `admin` account — it
> never overwrites an existing password, so changing it once is enough.

| Environment variable | Default | Description |
|---|---|---|
| `HAMSTER_ADMIN_SEED_ENABLED` | `true` | Seed the default administrator at startup |
| `HAMSTER_ADMIN_USERNAME` | `admin` | Username to seed |
| `HAMSTER_ADMIN_PASSWORD` | `admin123` | Password to seed (used only when the account is created) |
| `HAMSTER_PUBLIC_BASE_URL` | `http://localhost:5173` | Frontend origin used to build password-reset links |

##### Account lifecycle

1. **Register** — `POST /api/auth/register` creates the account **inactive** and returns no token.
2. **Activate** — an administrator activates it from `/admin/users`. Every login before that is rejected with `403` and the message "account not activated".
3. **Log in** — `POST /api/auth/login` returns a token. Deactivating an account blocks further logins; tokens already issued remain valid until they expire (1 day).
4. **Reset a password** — an administrator generates a **15-minute, single-use** link (`POST /api/admin/users/{id}/reset-link`) and hands it to the user. It opens `/reset-password?username=…&token=…`, which requires **both** the username and the token. Only a SHA-256 hash of the token is stored, and it is cleared as soon as it is used. Every failure returns the same `400` message, so the endpoint cannot be used to probe for usernames.
5. **Delete** — `DELETE /api/admin/users/{id}` removes the account and invalidates its tokens immediately.

Administrators cannot deactivate or delete their own account. Administrator status is **never**
carried in the JWT: every admin endpoint re-reads it from the database, so revocation takes
effect at once. The same password-then-activation order applies at login — a wrong password is
rejected with `401` before the account status is even considered, so anonymous requests cannot
learn whether a username exists but is merely inactive.

| Endpoint | Auth | Description |
|---|---|---|
| `POST /api/auth/register` | — | Register (201, no token); 409 if the username is taken |
| `POST /api/auth/login` | — | Log in (200, returns a token); 401 on bad credentials, 403 when inactive |
| `GET /api/auth/me` | Bearer | Current user |
| `POST /api/auth/reset-password` | — | Set a new password from `{ username, token, newPassword }`; 400 for any invalid, expired or used link |
| `GET /api/admin/users` | Admin | List users |
| `POST /api/admin/users/{id}/activate` | Admin | Activate a user (204) |
| `POST /api/admin/users/{id}/deactivate` | Admin | Deactivate a user (204); 400 for your own account |
| `POST /api/admin/users/{id}/reset-link` | Admin | Generate a 15-minute single-use reset link |
| `DELETE /api/admin/users/{id}` | Admin | Delete a user (204); 400 for your own account |

##### Account sets

An **account set** groups business data (for example "Home" vs "The company"). Account sets and
users are **many-to-many**: an administrator links users to account sets from
`/admin/account-sets`. Administrators themselves need no link — they can see and switch between
**every** account set.

A regular user's login experience follows how many account sets they can reach:

| Reachable account sets | On login |
|---|---|
| none | The header shows "no account set available" instead of a name and a switch button |
| exactly one | Selected automatically, no prompt |
| two or more | A **selection dialog opens first** and cannot be dismissed until an account set is chosen (it offers "log out" as an escape) |

Once selected, the header shows the current account set plus a **Switch** button, placed just
before **Log out**.

The current account set travels on every request as the `X-Account-Set-Id` header and is
validated per request against the user's links — it is deliberately **not** part of the JWT.
Putting it in the token would mean an administrator's change to a user's links only takes effect
after the token expires (1 day); validating per request makes it apply to the very next request.

`hamster_account` (see below) is the **first business table actually scoped by account set**;
the sample table `sample_account` and the other tables still have no account-set column. New
business tables should follow the same contract: add an `account_set_id` column and resolve the
current account set through `CurrentAccountSetExtensions.ResolveCurrentAccountSetAsync` (which
resolves and authorises it from the header), **rejecting the request when none resolves** —
otherwise the endpoint degrades into "query the whole table".

| Endpoint | Auth | Description |
|---|---|---|
| `GET /api/account-sets/mine` | Bearer | Account sets you can reach (all of them for an administrator) |
| `GET /api/account-sets/current` | Bearer | Resolve `X-Account-Set-Id`: `null` without the header, 400 when malformed, 403 when unknown or not yours |
| `GET /api/admin/account-sets` | Admin | List account sets with their linked-user counts |
| `POST /api/admin/account-sets` | Admin | Create (201); 409 on a duplicate name |
| `PUT /api/admin/account-sets/{id}` | Admin | Rename / re-describe (204) |
| `DELETE /api/admin/account-sets/{id}` | Admin | Delete, dropping its links (204) |
| `GET /api/admin/account-sets/{id}/members` | Admin | Linked user ids |
| `PUT /api/admin/account-sets/{id}/members` | Admin | **Replace** the linked-user set with `{ userIds }` (204) |

##### Accounts

An **account** is what money and debts hang off in the bookkeeping model. Every account belongs
to exactly one account set: the list is filtered by the current account set, so switching account
sets switches the set of accounts (`hamster_account` is the first business table actually scoped
by account set).

An account's scope decides who can see and use it; the backend evaluates that on every request:

| Scope | Who can see and change it |
|---|---|
| Public | **Every member** of the account set — for accounts the household shares |
| Personal | **Only its owner** (the owner is the creator and cannot be reassigned) |

A system administrator is the sole exception: any existing account set is reachable (the header
is still required), and every account in it — including other people's personal accounts — is
visible and editable. Visibility and editability coincide here, so there is no read-only state.

Four account types exist and users cannot extend them:

| Type | Value | Meaning | User-selectable |
|---|---|---|---|
| Ledger | `Ledger` | A summary account for bookkeeping; holds no money itself | **No** — the system creates it, and it never appears in an account list |
| Fund | `Fund` | Actual money (cash, bank cards, e-wallets) | Yes |
| Liability | `Liability` | Money owed (credit cards, loans); the opening balance may be negative | Yes |
| Contact | `Contact` | Receivables and payables (lending, borrowing, pending reimbursements) | Yes |

The **ledger account is a purely internal system account**: it exists only as the double-entry
counterparty of opening balances. It **cannot be created or assigned by hand** (`POST` / `PUT`
with `type: "Ledger"` always answers 400) and it **appears in no account list** — an
administrator's list excludes it too. There is no exception and no toggle, so there is exactly
one visibility rule. It still counts toward balances and takes part in double-entry balancing;
only its presentation is hidden. Which types a user may assign is decided in exactly one place —
`AccountTypeExtensions.IsUserAssignable` — and the endpoint's validation and error text derive
from it.

The **opening balance** is what the account already held when it was created (at most two decimal
places). It is written as an opening transaction at creation time (see the next section) and is
**immutable afterwards** — to change a balance, post a balance-adjustment transaction rather than
rewriting the opening. The **balance** is a **read-only derived value**: the signed sum of every
transaction entry on the account. The database holds **no balance column** — keeping a single
derivation path is what prevents a stale column from silently producing wrong totals.

Accounts are never physically deleted, only deactivated and reactivated (soft delete): accounts
are what transactions hang off, so deleting one would orphan historical rows. A deactivated
account is hidden from the default list; `includeInactive=true` shows it and lets you reactivate.

`isSystem` marks accounts the system created for itself (today, the opening ledger account). It
**cannot be created by a user and never appears in an account list** (the list filters on the
`Ledger` type — see the table above; "not creatable by hand" and "not listed" close the loop on
each other). The marker itself only serves "reuse the same row when creating on demand", so the
account **never loses its system identity by being renamed or retyped** — the identity lives on a
marker column rather than being reverse-engineered from a name or type.

| Endpoint | Auth | Description |
|---|---|---|
| `GET /api/accounts?includeInactive=false` | Bearer | Accounts you can see in the current account set (all of them for an administrator); `balance` is the derived figure. **Never includes the ledger account** |
| `POST /api/accounts` | Bearer | Create (201); body `{ name, scope, type, initialBalance }` where `type` is `Fund` / `Liability` / `Contact`. A personal account's owner is forced to the caller. A non-zero opening balance also posts an opening transaction |
| `PUT /api/accounts/{id}` | Bearer | Rename / retype (204), `type` again excluding `Ledger`; scope, owner, account set and the **opening balance** are all immutable |
| `POST /api/accounts/{id}/deactivate` | Bearer | Deactivate — soft delete (204) |
| `POST /api/accounts/{id}/activate` | Bearer | Reactivate (204) |

Failure contract: 400 without the account-set header (accounts always live inside one); 403 when
the account set is unknown or not yours; **404** when the account does not exist, belongs to
another account set, or is invisible to the caller (all three answer alike so the endpoint cannot
be used to probe for other people's accounts); 409 when the name repeats within the same
"account set + scope"; 400 for a numeric or unknown `scope` / `type`, since both travel as
**strings**.

##### Transactions and entries

Bookkeeping is **double-entry**: **every transaction carries two entries, one debit and one
credit**, and total debits always equal total credits. The data lives in two tables:

| Table | Contents |
|---|---|
| `hamster_transaction` | Transaction header: account set, type, timestamp, summary, remark, who posted it |
| `hamster_transaction_entry` | Transaction entry: parent transaction, account, direction, amount |

An entry's direction is a **`direction` enum plus a positive amount**, not a signed amount:

| Direction | Value | Effect on that account's balance |
|---|---|---|
| Debit | `Debit` (1) | Increase |
| Credit | `Credit` (2) | Decrease |

The sign conversion happens in **exactly one place** (`TransactionService.SumSignedAmountsAsync`:
debits positive, credits negative); everywhere else only positive amounts are moved around. The
enum is deliberately **not flipped per account type** (as in "a credit increases a liability") —
that would give one `direction` opposite meanings on different accounts, and once the sign logic is
scattered it stops lining up.

Balance is guaranteed on the **write side**: entries are always written in pairs of "target
account + counterparty account", and there is no path that writes a single side.

###### Opening balances

When an account is created, its opening balance **is posted as an opening transaction** rather than
only stored in the `initial_balance` column:

```
Opening balance of 500 on "Cash":
  Debit   Cash            500
  Credit  Opening Ledger  500
```

The **opening ledger account** (named "期初账本", type `Ledger`, public, `isSystem = true`) is the
counterparty. The system **creates it on demand**: once per account set, reused thereafter, and
**an account set always has exactly one**. It **never appears in an account list**, and it cannot
be created or retyped by hand (see the type table under "Accounts"). An account with an opening
balance of 0 **posts nothing** (its entry sum is 0, matching the opening balance). A negative opening balance (a
liability) flips the direction automatically — the target account takes the credit (balance
decreases) and the ledger account takes the debit.

The **balance is therefore the signed sum of all entries**: the opening entry already carries
"+opening balance", so `initial_balance` is **not added on top** — that would count it twice.

Accounts that predate this version get their missing opening transactions written by an **opening
balance backfill** at startup: it is idempotent, an account that already has an opening entry is
left alone, backfilled transactions carry no poster, and a failure only logs a warning rather than
blocking startup.

##### Upgrading an existing database

SqlSugar appends new columns as **nullable** and does not fill them in for pre-existing rows.
On startup the API therefore backfills the `is_admin` / `is_active` flags of any older user row
to `false` — meaning **accounts created before this version start out as "not an administrator,
not activated"** and need an administrator to activate them. For the same reason it backfills
`is_system` to `false` on older account rows (no account created before this version can be a
system account); without that, a NULL `is_system` makes the account list fail to bind and return
500.

### Frontend Setup

```bash
cd ui

# Install dependencies
pnpm install

# Run the development server
pnpm dev

# Type check
pnpm type-check

# Build for production
pnpm build

# Lint and fix
pnpm lint

# Format code
pnpm format

# Preview production build
pnpm preview
```

The frontend development server runs at `http://localhost:5173`; the backend's development CORS policy already allows it.

---

Copyright (c) 2026 delly.net
