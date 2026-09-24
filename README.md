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

The account type also carries one **usage-side** rule: only **Fund and Liability accounts** may serve
as either end of a **transfer**. It is decided in exactly one place —
`AccountTypeExtensions.IsTransferAccount` (shaped like `IsUserAssignable`) — and the transfer
endpoint's validation and error text derive from it. A Contact account records "who owes whom"
rather than "where the money sits", so moving money into or out of it does not change where the
money is; the ledger account is an internal system account. Neither can be an end of a transfer.
This is not a new account type — only a rule about which types may transfer between each other.

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
| `POST /api/accounts` | Bearer | Create (201); body `{ name, scope, type, initialBalance, currencyCode }` where `type` is `Fund` / `Liability` / `Contact`. `currencyCode` must be an **active** currency and is immutable afterwards. A personal account's owner is forced to the caller. A non-zero opening balance also posts an opening transaction |
| `PUT /api/accounts/{id}` | Bearer | Rename only (204) — the body is just `{ name }`; scope, owner, account set, **`type`**, **currency** and the **opening balance** are all immutable. Sending `type` has no effect (it is not on the request record) |
| `POST /api/accounts/{id}/deactivate` | Bearer | Deactivate — soft delete (204) |
| `POST /api/accounts/{id}/activate` | Bearer | Reactivate (204) |

Failure contract: 400 without the account-set header (accounts always live inside one); 403 when
the account set is unknown or not yours; **404** when the account does not exist, belongs to
another account set, or is invisible to the caller (all three answer alike so the endpoint cannot
be used to probe for other people's accounts); 409 when the name repeats within the same
"account set + scope"; 400 for a numeric or unknown `scope` / `type`, since both travel as
**strings**.

##### Categories

A **category** answers "what was this posting for" (meals, salary, card repayment). It belongs to
exactly one account set — the list is filtered by the current account set, exactly like accounts —
so each account set keeps its own vocabulary: "Dining" can cover a different range of spending in
each one.

There is deliberately **no user-side / admin-side split** (unlike currencies): a category is a
dictionary the whole account set shares, and maintaining it takes no system administrator. Splitting
it would leave an ordinary user looking at a category they created themselves and being unable to
rename it.

Categories do **not** distinguish income / expense / transfer. A category is "what the money was
for", while the direction of the movement already lives on the transaction's `type`. Tying a
category to a type would force the same name to be created once per type and would make a shopping
refund — one category used on income and expense alike — impossible to record. That is also why the
**`GET` / `POST` endpoints take no type parameter at all**: there is no type dimension to filter on.

| Endpoint | Auth | Description |
|---|---|---|
| `GET /api/categories?includeInactive=false` | Bearer | Categories in the current account set, oldest first. `includeInactive=true` also returns deactivated ones |
| `POST /api/categories` | Bearer | Create (201); body `{ name }`, at most 32 characters |
| `PUT /api/categories/{id}` | Bearer | Rename only (204) — the body is just `{ name }`; the account set is immutable and **not on the request record**, so sending one has no effect |
| `POST /api/categories/{id}/deactivate` | Bearer | Deactivate — soft delete (204) |
| `POST /api/categories/{id}/activate` | Bearer | Reactivate (204) |

Failure contract: 400 without the account-set header (categories always live inside one), or for an
invalid `name`; 403 when the account set is unknown or not yours; 404 when the category does not
exist or belongs to another account set; 409 when the name repeats within the same account set.
Names are matched **case-insensitively** after trimming, so "餐饮" and " 餐饮 " are the same name
and cannot both exist.

Categories are **never physically deleted, only deactivated** (soft delete), for the same reason
accounts are: transactions hang off them, so deleting one would orphan historical rows. A
deactivated category is hidden from the default list and is **no longer offered** when recording new
postings; its **name is still returned** by the entry query, because history has to keep reading
correctly.

**A category hangs on the transaction, not on the entry** (see `hamster_transaction.category_id`).
It describes "why this posting happened", which is a property of the act of recording. A transfer
posts two entries, so a category on the entry would make one transfer ask for two categories — while
a transfer's category ("card repayment") is naturally about the whole thing. One consequence worth
knowing: **both entry rows of one transaction carry the same category.**

Storing the **primary key** rather than the name is what makes renaming safe: history keeps pointing
at the same row and therefore displays the new name, instead of splitting one category into two
across the books.

**Categories are deliberately neither seeded nor backfilled.** Seeding would mean inventing a
vocabulary every account set must then go and delete, and it is not idempotent in practice — a
category the user deleted comes back on the next startup, making the management page's own edits
look ineffective (unlike currencies, where "everyone needs these few" is a real consensus). An empty
dictionary plus **create-on-type while recording** covers "usable out of the box". Backfilling
`category_id` is unnecessary for the same structural reason: it is a nullable `int`, so NULL on a
pre-existing row is precisely the legitimate value — "unclassified".

##### Transactions and entries

Bookkeeping is **double-entry**: **every transaction carries two entries, one debit and one
credit**, and total debits always equal total credits. The data lives in two tables:

| Table | Contents |
|---|---|
| `hamster_transaction` | Transaction header: account set, type, timestamp, summary, remark, **category**, who posted it |
| `hamster_transaction_entry` | Transaction entry: parent transaction, account, direction, amount |

An entry's direction is a **`direction` enum plus a positive amount**, not a signed amount:

| Direction | Value | Effect on that account's balance |
|---|---|---|
| Debit | `Debit` (1) | Increase |
| Credit | `Credit` (2) | Decrease |

The sign conversion has **exactly one definition** (`EntryDirectionExtensions.SignedAmount`: debits
positive, credits negative), shared by two callers — the balance roll-up (`SumSignedAmountsAsync`)
and the entry query's `signedAmount` field; everywhere else only positive amounts are moved around. The
enum is deliberately **not flipped per account type** (as in "a credit increases a liability") —
that would give one `direction` opposite meanings on different accounts, and once the sign logic is
scattered it stops lining up.

Balance is guaranteed on the **write side**: entries are always written in pairs of "target
account + counterparty account", and there is no path that writes a single side.

The transaction header's `type` has four values, and **all four have a real write path** (no
placeholder-only enum members):

| Type | Value | How it is produced | Direction (target / counterparty) |
|---|---|---|---|
| Opening balance | `OpeningBalance` (1) | Generated by the system on account creation (or backfill) | Debit / Credit (flipped automatically for a negative opening balance) |
| Income | `Income` (2) | Recorded by hand on the "Income" page | **Debit** (balance up) / Credit |
| Expense | `Expense` (3) | Recorded by hand on the "Expense" page | **Credit** (balance down) / Debit |
| Transfer | `Transfer` (4) | Recorded by hand on the "Transfer" page | **Credit** (source account, balance down) / Debit (destination account) |

The direction follows from the **transaction type**, not from the sign of the amount: amounts are
always positive and the two entries are equal and opposite, so the books balance by construction.
**A transfer runs in the same direction as an expense** — the source account is "the account the
money leaves", so it takes the credit, while the destination account takes the debit as the
counterparty. "Which types may be recorded by hand" is decided in **exactly one place**
(`TransactionTypeExtensions.IsUserRecordable`, shaped like `IsUserAssignable` for account types);
the endpoint's validation and its error text are both derived from it — an opening balance can only
come from the system, so passing `OpeningBalance` by hand is a 400, and the other three share a
single recording endpoint.

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
all three kinds of posting land on the same row. **A transfer is the one exception**: its
counterparty is another real account (the destination), so the ledger account plays no part in it
and gets no entry. It is **not** "for opening balances only"; the "opening" in its name merely
records that it was born there. It never appears in any UI, so the name is cosmetic.

###### Recording income, expenses and transfers

All three kinds of posting **share a single write endpoint**, and there is deliberately **no**
single-transaction read endpoint:

| Endpoint | Auth | Description |
|---|---|---|
| `POST /api/transactions` | Bearer | Record one income, expense or transfer (201). Body `{ type, accountId, amount, occurredAt, summary, remark, currencyCode, counterpartyAccountId, counterpartyName, categoryId, categoryName }`: `type` is `Income` / `Expense` / `Transfer` (as a **string** — a number or `OpeningBalance` is a 400); `amount` must be `> 0` with at most two decimals; `occurredAt` is an ISO 8601 timestamp taken as the **business time** (back-dating is allowed); `summary` is required (≤128), `remark` optional (≤256); `currencyCode` is required and **must match both accounts**; leaving both `counterpartyAccountId` and `counterpartyName` empty means "no counterparty", and when both are given the **primary key wins** (it is more precise than a name); leaving both `categoryId` and `categoryName` empty means "unclassified". Returns the persisted transaction (with `id` / `accountName` / `counterpartyName` / `categoryName`) |

The endpoint writes **both entries at once**, equal amounts and opposite directions, with the
direction derived from `type` (see the table above) — which is why `amount` is always positive.
`accountId` is the **target account**: the income account for income (balance up), the expense
account for an expense and the **source account** for a transfer (both of those go down). The
counterparty comes from one of four places:

| Counterparty | Trigger | Meaning |
|---|---|---|
| By primary key | `counterpartyAccountId` names a visible account | A movement of funds between **two real accounts** |
| By name, found | `counterpartyName` matches an existing visible account | The same — the name is only a lookup, nothing is created |
| By name, created | `counterpartyName` matches nothing visible | An **Contact** account is auto-created (personal, opening balance 0, so no opening entry) and handled as above |
| Ledger account | Both empty | "The money came from / went outside the account set" |

The **category is optional** and comes from one of three places, resolved by the same
"primary key wins, otherwise by name" rule:

| Category | Trigger | Meaning |
|---|---|---|
| By primary key | `categoryId` names a category in the current account set | Attached as given |
| By name, found | `categoryName` matches one **in the current account set — including deactivated ones** | The same; the name is only a lookup, nothing is created |
| By name, created | `categoryName` matches nothing | A category is **auto-created** in the current account set and attached |
| None | Both empty | "Unclassified" — a legitimate state, not a missing value |

Matching against **deactivated** categories too is deliberate: typing an exact name means you want
*that* category, and silently creating a live twin beside a disabled one would split the history
that the disabled row still carries. The difference from the counterparty is that creating on demand
here is the *point* of the feature — a bookkeeper who types a new category expects it to exist — and
it applies to **all three types including transfers**, unlike the counterparty's create-by-name,
which a transfer forbids (a name-created counterparty is a Contact account, which would let a
transfer's counterparty walk around the "Fund/Liability only" restriction; a category has no such
restriction to walk around).

The category is resolved **after every validation gate**, not before: resolution can create a row,
so a request that is destined to be refused must not leave a trace in the category table.

**A transfer carries one category**, not two — the category lives on the transaction header, so both
of its entries share it.

**A transfer only ever takes the first branch**: both of its accounts are real, so there is no
"outside the account set" to fall back on. Beyond `type` it adds exactly four validations (every
other field rule is shared with income and expenses):

| Validation | Trigger | Response |
|---|---|---|
| Destination required | `counterpartyAccountId` is empty | 400 |
| No create-by-name | `counterpartyName` is non-empty | 400 |
| Both ends must be a **Fund or Liability account** | The type of the account behind `accountId` or `counterpartyAccountId` fails `AccountTypeExtensions.IsTransferAccount` (i.e. `Fund` / `Liability`) | 400, with the account's name and its actual type in the message |
| The two ends must differ | `counterpartyAccountId` equals `accountId` | 400 |

The second one is not a nicety: a name-created counterparty is a **Contact** account, so letting it
through (or merely ignoring the field) would let a transfer's counterparty walk around the
"Fund/Liability only" restriction.

Failure contract: 400 without the account-set header, or for an invalid `type` / `amount` /
`occurredAt` / text, or when **the two accounts disagree on currency** (including a `currencyCode`
that does not match them), or on any of the four transfer-specific validations above, or when a
`categoryId` **does not exist in the current account set** (a 400 on the `categoryId` field, *not* a
404 — see below); **404 when an account is invisible, missing, or belongs to another account set**
(all three answer alike so the endpoint cannot be used to probe for other people's accounts).

The category's failure code **deliberately diverges from the accounts'**: an unresolvable account is
a 404 so the endpoint cannot be probed for other people's accounts, but a category has **no
visibility dimension to hide behind** — every category in the account set is visible to everyone in
it (that is the whole point of the user-side/admin-side merge above). A 404 would therefore be
protecting nothing, and a 400 is the better answer: it is a body-field error, and it **aggregates**
with the other field-level errors instead of short-circuiting them, so one request can report "the
amount is bad *and* the category is bad" at once. The account set's **system ledger
account cannot be posted to by hand**: it is never returned in any account list, so a caller has no
way to obtain its primary key.

For line-level detail (including the counterparty bucket and the signed amount) use
`GET /api/entries`. Opening a second, near-identical endpoint just to read back what was just
written would add a read path free to drift away from the entries query.

###### Entry query

Entries are read back through one endpoint, which answers "what moved, when, on which account":

| Endpoint | Auth | Description |
|---|---|---|
| `GET /api/entries?from=&to=&accountIds=&page=&pageSize=` | Bearer | Transaction entries in the current account set, oldest first, paged. `from` / `to` are ISO 8601 timestamps compared against the transaction's **business time** (`occurred_at`), both **inclusive**; omitting either leaves that side unbounded. `accountIds` may be repeated and omitted entirely; `page` defaults to 1 and `pageSize` to 50 (max **200**). Returns `{ items, total, page, pageSize }` |

**One row is one entry, not one transaction.** A transaction consists of a debit and a credit; when
both sides sit on accounts you selected, both appear as rows. `amount` is always **positive** and
`direction` (`Debit` / `Credit`) carries the side: those two are the underlying bookkeeping facts,
and no signed amount is stored.

**Read `signedAmount` to render.** It is `amount` signed by `direction` (debits positive, credits
negative) and means "how much this entry moved its account's balance". The UI's income / expense
columns and its green / red colouring derive from it, so a caller **need not** repeat the
direction-to-sign conversion — that conversion has exactly one definition
(`EntryDirectionExtensions.SignedAmount`), shared with the balance roll-up `SumSignedAmountsAsync`.

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

**A transfer shows up here as two rows**: the source account takes the credit (landing in the
expense column) and the destination account the debit (the income column), both with a
`counterpartyKind` of `Account` — both ends are real accounts, and no ledger account is involved.
The two rows look exactly like "one expense plus one income", so the **transaction type label is the
only thing telling them apart**, and "Transfer" is shown beside the summary. Each row counts toward
the page subtotal as usual: nothing is merged and nothing is excluded.

Each row also carries `categoryId` + `categoryName`. **They have no visibility tier** — unlike the
counterparty, a category has nothing to hide: every category in the account set is visible to every
member of it. Both fields are `null` for an **unclassified** transaction, which is a normal state
rather than missing data. The **name is supplied by the backend** rather than denormalized onto the
transaction row, so a rename propagates to past entries automatically (denormalizing would leave
history on the old name and split one category into two across the report). A **deactivated**
category's name is still returned: those postings happened, and hiding the label would make history
unreadable. Because the category is transaction-level, **both entry rows of one transaction carry
the same pair** — a transfer's two rows share one category.

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
