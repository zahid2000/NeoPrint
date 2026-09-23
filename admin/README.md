# NeoPrint admin panel

The Angular web application administrators and operators use to run the
platform (plan sections 8A.1–8A.3). Everything the platform can be configured
to do is done from here; Swagger exists for development only.

## Running it

```bash
npm ci
npm start          # ng serve on http://localhost:4200
```

`ng serve` proxies `/api` and `/hubs` to `http://localhost:8080` through
`proxy.conf.json`, so the panel and the API share an origin. That matters: the
admin refresh token is an HttpOnly cookie with `SameSite=Strict` and
`Path=/api/auth/admin`, and it would simply never be sent across origins. In a
container the same job is done by nginx (`nginx.conf`).

The backend has to be up:

```bash
docker compose up -d        # from the repository root
```

Sign in with the seeded SuperAdmin. The password comes from
`NEOPRINT_SEED_ADMIN_PASSWORD` in OpenBao and must be changed at first
sign-in — the router allows nothing else until it is (plan section 2.8).

## Commands

| Command | What it does |
| --- | --- |
| `npm start` | Development server with the API proxy |
| `npm run build` | Production build into `dist/admin/browser` |
| `npm test` | Unit tests (vitest, through the Angular builder) |
| `npm run lint` | eslint over TypeScript and templates |

## How it is put together

```text
src/app/
  core/
    api/          One typed service for the whole admin API surface
    auth/         Session, interceptor, route guards, *appHasPermission
    i18n/         az (default), en, ru — loaded in memory at startup
    layout/       The shell, with a menu built from the account's permissions
  features/
    login/        Sign-in and the mandatory password change
    dashboard/    What the account may do and what needs attention
    devices/      Pending devices, the activation wizard, enrollment tokens
    roles/        The permission matrix (plan section 8A.2)
    feature-flags/ Defaults, percentage rollout, per-kiosk overrides
```

Angular 21, standalone components, signals, zoneless change detection and the
built-in control flow (`@if`, `@for`). No NgModules, and no `zone.js`.

### Sessions

The access token lives in a signal and is never written to `localStorage`: it
lasts fifteen minutes, and anything in storage is readable by any script on the
page. The refresh token is a cookie the panel cannot read at all. On a 401 the
interceptor refreshes once — shared between every request that got a 401
together, because the refresh token rotates on use and a second call would be
treated as a replay and revoke the whole chain.

### Permissions

`authGuard` and `permissionGuard` keep an account off pages whose requests
would all be refused, and `*appHasPermission` hides controls it could not use.
Both are presentation only: every admin endpoint enforces the same permission
on the server (plan section 8A.2). A role change is picked up by reloading the
account's own permissions, without signing in again, and the menu and the
controls follow.

### Interface text

All three languages live in `core/i18n/translations.ts` and are loaded into
ngx-translate at startup, so switching language is instant and needs no
request. A test asserts that the three dictionaries carry exactly the same keys
and the same interpolation placeholders — a key missing from one language would
otherwise ship as the raw key on screen.

## Not built yet

The routes for **Administrators**, **Settings** and **Legal documents** exist
and are guarded, but the pages are placeholders: the endpoints behind them are
not written yet. The seeded data they will manage is already in place.

## Testing notes

The spec files run sequentially in a single worker (`vitest.config.ts`):
vitest gives a worker five seconds to start and does not let that be
configured, and on Windows starting one worker per file regularly missed it.
Because they then share a process, `src/test-setup.ts` resets Angular's global
`TestBed` around every test.
