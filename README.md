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
counterparty of opening balances. It **cannot be created by hand** (`POST` with `type: "Ledger"`
always answers 400) and it **appears in no account list** — an
administrator's list excludes it too. There is no exception and no toggle, so there is exactly
one visibility rule. It still counts toward balances and takes part in double-entry balancing;
only its presentation is hidden. Which types a user may assign is decided in exactly one place —
`AccountTypeExtensions.IsUserAssignable` — and the endpoint's validation and error text derive
from it. That check now **serves the create path only**: an account's type is immutable once
created (see the `PUT` row below), which removes the "create a `Fund` account and retype it to
`Ledger`" detour along with the ability to retype at all.

The **opening balance** is what the account already held when it was created (at most two decimal
places). It is written as an opening transaction at creation time (see the next section) and is
**immutable afterwards** — to change a balance, post a balance-adjustment transaction rather than
rewriting the opening. The **balance** is a **read-only derived value**: the signed sum of every
transaction entry on the account. The database holds **no balance column** — keeping a single
derivation path is what prevents a stale column from silently producing wrong totals.

Accounts are never physically deleted, only deactivated and reactivated (soft delete): accounts
are what transactions hang off, so deleting one would orphan historical rows. A deactivated
account is hidden from the default list; `includeInactive=true` shows it and lets you reactivate.

`isSystem` marks accounts the system created for itself (today, the ledger account). It
**cannot be created by a user and never appears in an account list** (the list filters on the
`Ledger` type — see the table above; "not creatable by hand" and "not listed" close the loop on
each other). The marker itself only serves "reuse the same row when creating on demand", and the
identity lives on that marker column rather than being reverse-engineered from a name or a type —
neither of which is mutable anyway (see the `PUT` row below), so there is nothing left that could
make the system lose track of its own account.

| Endpoint | Auth | Description |
|---|---|---|
| `GET /api/accounts?includeInactive=false` | Bearer | Accounts you can see in the current account set (all of them for an administrator); `balance` is the derived figure. **Never includes the ledger account** |
| `POST /api/accounts` | Bearer | Create (201); body `{ name, scope, type, initialBalance }` where `type` is `Fund` / `Liability` / `Contact`. A personal account's owner is forced to the caller. A non-zero opening balance also posts an opening transaction |
| `PUT /api/accounts/{id}` | Bearer | Rename only (204) — the body is just `{ name }`; scope, owner, account set, **`type`** and the **opening balance** are all immutable. Sending `type` has no effect (it is not on the request record) |
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

The transaction header's `type` has three values, and **all three have a real write path** (no
placeholder-only enum members):

| Type | Value | How it is produced | Direction (target / counterparty) |
|---|---|---|---|
| Opening balance | `OpeningBalance` (1) | Generated by the system on account creation (or backfill) | Debit / Credit (flipped automatically for a negative opening balance) |
| Income | `Income` (2) | Recorded by hand on the "Income" page | **Debit** (balance up) / Credit |
| Expense | `Expense` (3) | Recorded by hand on the "Expense" page | **Credit** (balance down) / Debit |

The direction follows from the **transaction type**, not from the sign of the amount: amounts are
always positive and the two entries are equal and opposite, so the books balance by construction.
"Which types may be recorded by hand" is decided in **exactly one place**
(`TransactionTypeExtensions.IsUserRecordable`, shaped like `IsUserAssignable` for account types);
the endpoint's validation and its error text are both derived from it — an opening balance can only
come from the system, so passing `OpeningBalance` by hand is a 400.

###### Opening balances

When an account is created, its opening balance **is posted as an opening transaction** rather than
only stored in the `initial_balance` column:

```
Opening balance of 500 on "Cash":
  Debit   Cash            500
  Credit  Opening Ledger  500
```

The **ledger account** (named "期初账本", type `Ledger`, public, `isSystem = true`) is the
counterparty. The system **creates it on demand**: once per account set, reused thereafter, and
**an account set always has exactly one**. It **never appears in an account list**, and it cannot
be created by hand (see the type table under "Accounts" — `Ledger` is rejected on create, and the
type of an existing account can no longer be changed at all). The same row serves as the
counterparty for income and expenses too, so the "opening" in its name is history rather than
scope. An account with an opening
balance of 0 **posts nothing** (its entry sum is 0, matching the opening balance). A negative opening balance (a
liability) flips the direction automatically — the target account takes the credit (balance
decreases) and the ledger account takes the debit.

The **balance is therefore the signed sum of all entries**: the opening entry already carries
"+opening balance", so `initial_balance` is **not added on top** — that would count it twice.

Accounts that predate this version get their missing opening transactions written by an **opening
balance backfill** at startup: it is idempotent, an account that already has an opening entry is
left alone, backfilled transactions carry no poster, and a failure only logs a warning rather than
blocking startup.

**The ledger account is the counterparty shared by opening balances, income and expenses alike.**
The system creates it on demand and reuses it thereafter, so an account set still has exactly one —
all three kinds of posting land on the same row. It is **not** "for opening balances only"; the
"opening" in its name merely records that it was born there. It never appears in any UI, so the
name is cosmetic.

###### Recording income and expenses

There is a single write endpoint, and deliberately **no** single-transaction read endpoint:

| Endpoint | Auth | Description |
|---|---|---|
| `POST /api/transactions` | Bearer | Record one income or expense (201). Body `{ type, accountId, amount, occurredAt, summary, remark }`: `type` is `Income` / `Expense` only (as a **string** — a number or `OpeningBalance` is a 400); `amount` must be `> 0` with at most two decimals; `occurredAt` is an ISO 8601 timestamp taken as the **business time** (back-dating is allowed); `summary` is required (≤128), `remark` optional (≤256). Returns the persisted transaction (with `id` / `accountName`) |

The endpoint writes **both entries at once** — one on the chosen account, one on the account set's
ledger account, equal amounts and opposite directions. The caller **cannot and need not** name the
counterparty. The direction is derived from `type` (see the table above), which is why `amount` is
always positive.

Failure contract: 400 without the account-set header, or for an invalid `type` / `amount` /
`occurredAt` / text; **404 when the account is invisible, missing, or belongs to another account
set** (all three answer alike so the endpoint cannot be used to probe for other people's accounts).
The account set's **system ledger account cannot be posted to by hand**: it is never returned in
any account list, so a caller has no way to obtain its primary key.

For line-level detail (including the counterparty bucket and the debit/credit direction) use
`GET /api/entries`. Opening a second, near-identical endpoint just to read back what was just
written would add a read path free to drift away from the entries query.

###### Entry query

Entries are read back through one endpoint, which answers "what moved, when, on which account":

| Endpoint | Auth | Description |
|---|---|---|
| `GET /api/entries?from=&to=&accountIds=&page=&pageSize=` | Bearer | Transaction entries in the current account set, oldest first, paged. `from` / `to` are ISO 8601 timestamps compared against the transaction's **business time** (`occurred_at`), both **inclusive**; omitting either leaves that side unbounded. `accountIds` may be repeated and omitted entirely; `page` defaults to 1 and `pageSize` to 50 (max **200**). Returns `{ items, total, page, pageSize }` |

**One row is one entry, not one transaction.** A transaction consists of a debit and a credit; when
both sides sit on accounts you selected, both appear as rows — that is what double-entry looks like
on screen. `amount` is always **positive** and the direction travels in `direction` (`Debit` /
`Credit`); this endpoint performs **no sign conversion** (`SumSignedAmountsAsync` remains the only
place that does).

Ordering is `occurred_at` ascending, then transaction id, then entry id. The third key is not
decoration: without it, rows sharing a timestamp could swap places between requests and appear on
two pages at once.

**Visibility is not re-implemented here.** The visible account set comes from the existing
`IAccountService.ListByAccountSetAsync(..., includeInactive: true, ...)` — the same single source the
account list uses — and entries are filtered by it. `includeInactive: true` is mandatory: accounts
are soft-deleted, so a deactivated account still carries history, and dropping it would make past
entries vanish while the rows sit in the database. `IAccountService` and `ITransactionService` are
**untouched** by this feature, so the opening-balance, balance-aggregation and backfill paths are
unaffected.

`accountIds` is **intersected** with the visible set rather than validated against it: ids you cannot
see are silently dropped (no error, no rows), and an empty intersection returns an empty page. Were
an invisible id to answer 403/404, the parameter would become a probe for other people's accounts.

The counterparty is the entry with the opposite direction in the same transaction, and it travels as
a **tiered** `counterpartyKind`:

| `counterpartyKind` | Meaning | Carries |
|---|---|---|
| `Account` | Counterparty is visible to the caller | `counterpartyAccountId` + `counterpartyName` |
| `Ledger` | Counterparty is the opening **ledger account** | Nothing — neither id nor name |
| `Hidden` | Counterparty exists but is not visible to the caller | Nothing — neither id nor name |
| `None` | No opposite-direction entry in the transaction | Nothing |

Splitting `Ledger` out from `Hidden` is not a leak: the ledger account is the system's own account,
exactly one per account set, its existence already documented here, and it never appears in any
account list. Someone else's personal account, by contrast, gets not even an id.

Failure contract: 400 without the account-set header ("请先选择账套"); the account set unknown or not
yours → 403; caller not found → 401; a malformed `from` / `to`, `from` later than `to`, `page < 1`, or
`pageSize` outside `1..200` → 400 as a field-level error.

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
