# Contributing

Thanks for considering a contribution to DataversePool.

## Before you start

- For anything beyond a small fix (new pooling strategy, public API change, new adapter), please
  open an issue first to discuss the approach. Several non-obvious design decisions here are
  deliberate — check `docs/adr/` before proposing a change that touches:
  - The serial creation gate (`ResourcePool<T>`'s `_creationGate`) — see ADR-0002.
  - Lease isolation semantics — see ADR-0003.
  - Health-check timing (no synchronous checkout validation) — see ADR-0004.
  - Whether `ConnectionPool.Core` may take new hard dependencies — see ADR-0001/ADR-0005 (Polly is
    intentionally an optional adapter, not a core dependency).

## Development

```bash
dotnet build
dotnet test
```

- `src/ConnectionPool.Core` — no Dataverse/network dependency; all tests run against fakes.
- `src/ConnectionPool.Dataverse` — tests must not require a real Dataverse connection (keep
  `PrewarmCount` at 0 and avoid calling `AcquireAsync` on pools built with real connection
  strings in unit tests). Use `samples/DataversePool.Sample` for real-connection smoke testing instead.
- `src/ConnectionPool.Dataverse.Polly` — generic over `<TResource>`; test with fakes, not a real
  `ServiceClient`.

## Pull requests

- Keep PRs focused; unrelated refactors make review slower.
- Add/update tests for any behavior change.
- Add an ADR under `docs/adr/` for any decision future contributors would otherwise have to
  rediscover by reading git history.
- Update `README.md`/`TODO.md` if the change affects usage or status.

## Code style

- Nullable reference types and implicit usings are enabled solution-wide; keep new code warning-free.
- No hard dependency on `System.Reactive`, Polly, or other heavy packages in `ConnectionPool.Core`.
