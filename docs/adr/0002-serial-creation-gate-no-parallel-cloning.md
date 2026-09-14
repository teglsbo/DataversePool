# ADR-0002: Seriel oprettelses-gate — ingen parallel cloning af ressourcer

## Status
Accepteret

## Kontekst
Empirisk måling (denne sessions forudgående arbejde) viste at `ServiceClient`-cloning
er bimodal:
- Første clone fra en base-forbindelse: ~500ms (reel auth/netværk).
- Efterfølgende sekventielle clones: ~1ms (cached token/discovery genbruges).
- Parallelle clones (flere samtidigt): 1–3.2s pr. clone pga. intern lock-contention
  i selve SDK'ets oprettelseskode.

Dette betyder at "prewarm parallelt for hurtigere opstart" er kontraproduktivt og kan
gøre opstart markant langsommere end sekventiel oprettelse.

## Beslutning
`ResourcePool<T>` garanterer at `policy.CreateAsync()` **aldrig kaldes samtidigt fra to
tråde** for samme underliggende base-forbindelse — håndhævet med en intern
`SemaphoreSlim(1,1)` ("creation gate") omkring alle kald til `CreateAsync`.

Dette gælder uanset trigger:
- Eager/sekventiel prewarm ved opstart (via `IHostedService`).
- Lazy oprettelse ved `AcquireAsync()` når poolen er tom.
- Genopretning af en slot efter `MarkUnhealthy`.

Prewarm-ved-opstart er **ikke** et hårdt krav i v1 (lazy er acceptabelt), men den
serielle gate er et hårdt krav uanset oprettelsesstrategi.

## Konsekvenser
- Simpel implementering: én global (eller pr.-base-connection) semaphore, ingen
  kompleks scheduling.
- Mulig ventetid ved cold start under høj samtidig load (flere kaldere venter på
  samme gate), accepteret som tradeoff mod at undgå 1–3.2s lock-contention-cost pr. clone.
- Skal verificeres med en concurrency-stresstest (se teststrategi) der tæller
  samtidige `CreateAsync`-kald via `Interlocked` og assert'er max == 1.
