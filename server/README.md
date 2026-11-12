# X-Ray sharing service

The server side of [shared X-Ray data](../docs/shared-data-design.md): an ASP.NET Core minimal API
over PostgreSQL 17 + pgvector. It uses the plugin's `Jellyfin.Plugin.SceneXray.Sharing` library, so
the two cannot disagree about wire formats, title keys, anchors or fits.

It is for invited friends only. It has no admin surface on its hostname; operators use the CLI
below, `kubectl` and `psql`.

## Layout

| Path | What |
|---|---|
| `Xray.Share.Server/Endpoints.cs` | the `/v1/*` routes (design §6) |
| `Xray.Share.Server/Submissions.cs` | checks, storage, credit and copy-back detection for one contribution |
| `Xray.Share.Server/Validation.cs` | structural checks (design §4) |
| `Xray.Share.Server/Tmdb.cs` | the TMDb credits check, cached 30 days |
| `Xray.Share.Server/Ledger.cs` | the append-only credit ledger and the balance rules (design §1) |
| `Xray.Share.Server/Consensus.cs` | consensus per (variant, analyser version), run as a queued job |
| `Xray.Share.Server/Jobs.cs` | the `jobs` queue (`FOR UPDATE SKIP LOCKED`) and the migrator |
| `Xray.Share.Server/Data/Migrations/*.sql` | the schema, applied in order at startup |
| `Xray.Share.Server/Cli.cs` | `migrate`, `invite create / list / revoke` |
| `Xray.Share.Server.Tests` | end-to-end tests against a real PostgreSQL |

## Running it locally

```bash
docker compose -f server/docker-compose.yml up -d --build
```

The service listens on `http://127.0.0.1:8080` (`/v1/health` answers), the database on
`127.0.0.1:55432`. Without `TMDB_API_KEY` in the environment, the TMDb credits check is skipped;
that switch (`Share__AllowWithoutTmdb`) is for local use only.

Issue an invite (the line it prints is what a friend pastes into the plugin):

```bash
docker compose -f server/docker-compose.yml exec server /app/xray-share invite create --note "Test"
```

With a Cloudflare Access service token, pass its client id and give the secret on stdin (or in
`XRAY_ACCESS_CLIENT_SECRET`), so it never lands in shell history. The secret goes into the printed
invite line only; the database keeps the client id.

```bash
docker compose -f server/docker-compose.yml exec -T server /app/xray-share invite create --note "Ali" --access-client-id 1234.access < secret.txt
```

To point a test Jellyfin at it, set `SharingServerUrl` in the plugin's XML configuration
(`plugins/configurations/Jellyfin.Plugin.SceneXray.xml`); there is no UI field for it on purpose.

## Tests

```bash
docker compose -f server/docker-compose.yml up -d db
dotnet test server/Xray.Share.Server.Tests
```

Each test class gets its own throwaway database on that server. CI runs the same tests against a
`pgvector/pgvector:pg17` service (`server-test` job). Set `XRAY_SHARE_TEST_DB` to use another server.

## Configuration

Section `Share` (environment `Share__<Name>`):

| Setting | Default | |
|---|---|---|
| `Database` | | Npgsql connection string, without the password in production |
| `DatabasePassword` | | the database password (secret: from Infisical in production) |
| `TmdbApiKey` | | TMDb v3 key for the credits check (secret) |
| `GraceMinutes` | 600 | design D2 |
| `EarnMultiplier` | 2 | design D1 |
| `UncorroboratedFloorMinutes` | 0 | uncorroborated credit spendable beyond 2 × corroborated |
| `MaxInstallsPerInvite` | 3 | design D9 |
| `MinAnalyzerVersion` | 2 | older submissions are refused |
| `MinPluginVersion` | 1.3.1.0 | older plugins cannot register |
| `RateLimits` | true | per-token limits (design §4) |
| `MaxBodyBytes` | 262144 | request body as sent |
