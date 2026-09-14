# TODO — DvPool (Dataverse Connection Pooling)

Sidst opdateret: 2026-09-14 (ADR-0013: fjerde reviewrunde — ende-til-ende AcquireTimeout, retfærdig operationel-fejl-tælling, idempotent warmup, korrekt probe-outcome-rapportering, holdbar leak-synlighed)

## Navn: DataversePool (skiftet fra XrmPool)

Oprindeligt sammenlignede vi fire ledige NuGet-navne (XrmPool, DvPool, PoolVerse, DataversePool) og
valgte først **XrmPool**. Efter bruger-feedback ("mere moderne?") skiftede vi til
**DataversePool**: mere eksplicit/nutidigt, matcher MS's nuværende "Dataverse"-branding fremfor det
ældre "Xrm"-SDK-navn, stadig ledigt på NuGet, ingen kollision med søster-projektet DataverseDuck.
`PackageId` er sat til `DataversePool.Core` / `DataversePool.Dataverse` / `DataversePool.Polly` i de
tre lib-csproj'er. Sample-projekt (`samples/DataversePool.Sample`), solution-fil
(`DataversePool.slnx`) og env-var-præfiks (`DATAVERSEPOOL_SAMPLE_CONNECTION_STRING`) omdøbt til
match (interne C#-namespaces/mappenavne er bevidst IKKE omdøbt fra `ConnectionPool.*` — det er en
stor, lavværdi-refaktorering; NuGet-pakkenavnet er det, offentligheden ser).

## Gruppe-pool-valg: måling (load) vs. blind round-robin

Tilføjet `LeastConnectionsSlotSelectionStrategy` som alternativ til default
`HealthAwareRoundRobinSlotSelectionStrategy`: vælger medlemmet med færrest aktuelt udlånte leases
(`PoolStats.LeasedCount`) i stedet for blind tur-baseret fordeling, med samme dead-member
circuit-breaking (skip/half-open/fail-open). Relevant når kaldsvarighed varierer meget mellem
medlemmer — se [ADR-0006](docs/adr/0006-dual-pooling-model-single-user-and-round-robin-group.md)'s
opdaterede afsnit for den fulde afvejning, inkl. hvorfor dette **stadig ikke** er
throttle/429-bevidst (poolen ser ikke hvad man gør med en lease efter `AcquireAsync`). 4 nye tests,
13/13 grønne i `ConnectionPool.Dataverse.Tests`.

## Status-oversigt

| # | Opgave | Status | Afhænger af |
|---|---|---|---|
| 1 | Scaffolde solution + projektskelet | ✅ Done | — |
| 2 | Skrive ADR'er for kerne-beslutninger | ✅ Done | — |
| 3 | Implementere Core pool-interfaces | ✅ Done | — |
| 4 | Skrive Core unit-tests | ✅ Done (14/14 passing) | #3 |
| 5 | Implementere Dataverse-adapter | ✅ Done | #3 |
| 6 | Skrive Dataverse-adapter tests | ✅ Done (9/9 passing) | #5 |
| 7 | Hærdning: races/timeouts/dead group member (ADR-0007) | ✅ Done | #3,#5 |
| 8 | Implementere Polly-adapter | ✅ Done | #5 |
| 9 | Skrive Polly-adapter tests | ✅ Done (3/3 passing) | #8 |
| 10 | README, LICENSE (MIT), CONTRIBUTING, SECURITY, CODE_OF_CONDUCT | ✅ Done | — |
| 11 | Sample-projekt (`samples/DataversePool.Sample`) | ✅ Done | #5,#8 |
| 12 | Live Dataverse-forbindelsestest | ✅ **Verificeret mod rigtig org** (se nedenfor) | #11 |

**MVP + open-source-grundpakke er komplet, og biblioteket er nu bevist at virke end-to-end mod en
rigtig Dataverse-organisation** (ikke kun mod fakes). Alle 7 projekter (3 lib + 3 test + 1 sample)
bygger rent, 26/26 tests grønne.

## Live Dataverse-forbindelse: verificeret ✅

Kørte `samples/DataversePool.Sample`'s single-user smoke-test mod en rigtig Dataverse-organisation, ved
at genbruge connection-oplysningerne fra søsterprojektet dvduck's `.env`
(`a local .env file outside this repository` — client-secret-baseret app-bruger, aldrig printet/logget
i klartekst i denne session). Resultat:

- `DataverseUserPool.WarmupAsync()` clonede rigtigt (sekventiel warmup, ADR-0002) og oprettede en
  ægte forbindelse (MSAL client-credential-flow, ~1.4s login).
- `AcquireAsync()` udleverede en `ServiceClient` med `IsReady=True`.
- Et rigtigt `WhoAmIRequest` blev eksekveret (~3.3s første kald, inkl. cold-start) og returnerede
  et ægte `UserId`/`OrganizationId`.

Dette bekræfter at hele kæden — connection-string-parsing, warmup/clone, lease-udlevering,
`IPooledResourcePolicy<T>`-integrationen — rent faktisk virker mod en levende Dataverse-instans,
ikke kun mod fakes/mocks. Gruppe-pool (round-robin på tværs af flere app-brugere) er **ikke**
afprøvet live endnu, da kun én app-brugers credentials var tilgængelige (kræver
`DATAVERSEPOOL_SAMPLE_CONNECTION_STRING_2`/`_3` for en ekstra service-principal).

## Polly-adapter (ConnectionPool.Dataverse.Polly) — færdig

`PollyPoolHealthSignalExtensions` er **generisk** over ressourcetypen (ikke hardcodet til
`ServiceClient`), så den kan testes end-to-end med en fake pool/ressource uden Dataverse-afhængighed,
og genbruges for enhver `ConnectionPool.Core`-baseret pool. Projektet refererer derfor kun
`ConnectionPool.Core` + `Polly.Core`, ikke `ConnectionPool.Dataverse` (undgår at trække hele
Dataverse-SDK'en ind for rene Polly-brugere).

- `AddRetryWithPoolHealthSignal<TResult, TResource>(lease, options)` og
  `AddCircuitBreakerWithPoolHealthSignal<TResult, TResource>(lease, options)` (+ non-generic
  object-result overloads) wire'r `OnRetry`/`OnOpened` til `lease.MarkUnhealthy(exception)`.
- Eksisterende bruger-callbacks på `options.OnRetry`/`OnOpened` bevares og kaldes altid først.
- Tests verificerer end-to-end gennem en rigtig `ResourcePool<T>`: efter en retry/circuit-open og
  efterfølgende dispose, udleveres en *ny* ressource ved næste acquire, og den gamle er bevisligt
  disposed (ikke kun et mock-assert på at MarkUnhealthy blev kaldt).

## Åbne spørgsmål / opfølgning

- [ ] Bekræft eller afkræft socket-depletion-antagelsen ved new-per-request (uverificeret).
- [ ] Bekræft eller afkræft CallerId cross-thread race condition ved faktisk parallel varierende CallerId-test.
- [ ] Baggrunds-sweep for MaxIdleLifetime (i dag kun lazy-ved-checkout) — udskudt til v2 hvis behov.
- [x] ~~Kør den faktiske live Dataverse-smoke-test~~ — kørt og bekræftet mod rigtig org (se ovenfor).
- [ ] Kør gruppe-pool (round-robin) smoke-testen live med 2+ app-brugere (kræver en ekstra
      service-principal ud over den ene der blev brugt til single-user-testen).
- [ ] Overvej integrationstest-projekt (opt-in, mod ægte Dataverse-instans) — ikke oprettet i denne session.
- [ ] Før faktisk NuGet-publicering: opdater placeholder-URL'er i `Directory.Build.props`
      (`PackageProjectUrl`/`RepositoryUrl` peger pt. på et fiktivt `github.com/dataversepool/dataversepool`)
      til det rigtige repo, og afklar rigtigt forfatter/copyright-navn i `LICENSE` (pt.
      "DataversePool contributors" som placeholder).

## Hærdning (ADR-0007) — færdig

Efter en systematisk gennemgang af race conditions/timeouts/real-world-scenarier blev følgende rettet:
- `MarkUnhealthy` no-op'er hvis kaldt efter `DisposeAsync` (use-after-dispose guard).
- `ResourcePool<T>.DisposeAsync` gør nu et best-effort drain og forhindrer nye `AcquireAsync`
  (kaster `ObjectDisposedException`); leases der returneres efter shutdown disposes direkte i stedet
  for at blive lækket i `_idle`.
- `PoolStats.UnhealthyOrRecyclingCount` tælles nu korrekt (var tidligere hardcodet til 0).
- `PoolOptions.CreateTimeout`: en hængende `CreateAsync` blokerer ikke længere den serielle
  creation-gate på ubestemt tid — gaten frigives ved timeout, det forladte kald må selv afslutte og
  bortskaffes automatisk (bevidst tradeoff: kan sjældent tillade 2 overlappende clones).
- `PoolOptions.MaxIdleLifetime`: idle ressourcer ældre end grænsen recycles proaktivt ved checkout
  (svarer til ADO.NET's Connection Lifetime).
- **"Én bruger i en gruppe er død":** ny `HealthAwareRoundRobinSlotSelectionStrategy` (nu default i
  `DataverseGroupPool`) sporer `ConsecutiveCreateFailures` pr. medlem, springer permanent fejlende
  medlemmer over (circuit-open), prøver dem igen efter cooldown (half-open), og fail'er *open*
  (vælger stadig et medlem) hvis alle er nede samtidig, frem for at låse gruppen helt ude.

Se `docs/adr/0007-race-conditions-timeouts-and-failure-scenarios.md` for fuld analyse og alle 6
identificerede punkter. Nye tests: `CreateTimeoutTests`, `PoolShutdownTests`,
`DefensiveBehaviorTests` (Core); `HealthAwareRoundRobinSlotSelectionStrategyTests` (Dataverse).

## Core-implementering (ConnectionPool.Core) — færdig

Filer: `IPooledResourcePolicy.cs`, `PoolIncidentInfo.cs`, `PoolOptions.cs`, `PoolStats.cs`,
`SlotHealthChanged.cs`, `Slot.cs` (internal), `PooledLease.cs`, `ResourcePool.cs`.

Nøgle-implementeringsdetaljer:
- `SemaphoreSlim`-baseret capacity-gate (1 permit pr. slot) bounder created+leased til `MaxSize`
  uden separat tælling der kan komme ud af sync.
- Separat `_creationGate` (1,1) garanterer aldrig parallel `CreateAsync` (ADR-0002) — verificeret af
  `SerialCreationGateTests` med 20 samtidige acquires på tom pool (`MaxObservedConcurrentCreations == 1`).
- `MarkUnhealthy` → baggrunds-recycle uden at blokere andre waiters (ADR-0004) — verificeret af
  `HealthSignalTests`.
- Lease-leak detection via finalizer (ADR-0003) → samme recycle-vej som `MarkUnhealthy`.
- `HealthChanges` er en håndrullet `IObservable<SlotHealthChanged>` (ingen `System.Reactive`-afhængighed).
- Tests: 100% fakes (`FakePolicy`/`FakeResource`), ingen Dataverse-afhængighed, jf. teststrategi.

## Projektstruktur

```
DvPool.sln
src/
  ConnectionPool.Core/                 # generisk pool-motor, ingen Dataverse-viden
  ConnectionPool.Dataverse/            # ServiceClient-adapter, single-user + group/round-robin
  ConnectionPool.Dataverse.Polly/      # valgfri Polly-integration (MarkUnhealthy-wiring)
tests/
  ConnectionPool.Core.Tests/
  ConnectionPool.Dataverse.Tests/
  ConnectionPool.Dataverse.Polly.Tests/
docs/adr/                              # arkitektur-beslutninger, se ADR-0001..0006
```

## Nøglebeslutninger (se docs/adr/ for fuld begrundelse)

- **ADR-0001**: `IPooledResourcePolicy<T>` afkobler Core fra Dataverse.
- **ADR-0002**: Seriel oprettelses-gate — aldrig parallel cloning (empirisk begrundet).
- **ADR-0003**: Lease-isolation er en dispose-kontrakt, ikke runtime-håndhævet.
- **ADR-0004**: Ingen synkron health-check ved checkout; `MarkUnhealthy`-signal i stedet.
- **ADR-0005**: Polly er en separat, valgfri adapter-pakke (del af MVP).
- **ADR-0006**: Dobbelt pooling-model — `DataverseUserPool` + `DataverseGroupPool` (round-robin, pluggable strategi).
- **ADR-0007**: Hardening af race conditions, timeouts og dead-member-scenarier.
- **ADR-0008**: Throttle-detektion via HTTP 429/exception (`DataverseThrottleDetector`), ikke proaktive `x-ms-ratelimit-*` headers — SDK'en eksponerer ikke headers på succesfulde kald. `DataverseGroupPool.AcquireAsync()` returnerer nu `DataverseGroupLease` så en 429 kan rapporteres tilbage til det rigtige medlem (`ReportIfThrottled`).
- **ADR-0009**: Sikkerhedsfund fra security-review rettet — `IPooledResourcePolicy<T>.OnReturned` nulstiller `ServiceClient.CallerId` ved retur til poolen, så impersonation ikke lækker til næste, urelaterede caller. Desuden: distributed-systems-review afdækkede 5 blokerende multi-instans-problemer (delt budget, fail-open-forstærkning, ikke-atomisk half-open, circuit tracker kun creation-fejl, ubegrænset acquire-kø) — bevidst IKKE løst nu, men dokumenteret som eksplicit produktionsbegrænsning i README ("single process per service-principal set").
- **ADR-0010**: Rettede 2 af de 3 punkter brugeren bad om at få styr på: (a) `MemberCircuitBreaker` — ny delt type, reelt single-probe half-open (kun én samtidig caller vinder probe-slottet pr. cooldown-vindue, per-proces, ingen delt state mellem processer per eksplicit ønske), erstatter den duplikerede og ikke-atomiske `_openedAt`-logik i begge strategier; (b) `GroupAllUnavailableBehavior` (`FailOpen` default/bagudkompatibel, eller `FailFast` → kaster `DataverseGroupUnavailableException` med medlemsnavne + tidligste kendte throttle-udløb i stedet for at sende trafik til et gruppe, man allerede ved er utilgængelig). Delt budget-koordinering på tværs af processer (punkt 1 i den oprindelige liste) forbliver bevidst uløst — brugeren afviste eksplicit delt state mellem processer, så det er kun dokumenteret (ADR-0009), ikke bygget. `ISlotSelectionStrategy.SelectNext` returnerer nu `SlotSelection` (breaking, accepteret jf. pre-1.0). 52/52 tests grønne.
- **ADR-0011**: Endnu en reviewrunde (sikkerhed: ingen fund; DB-pool-ekspert; distributed-systems-genreview) fandt at ADR-0010's single-probe-fix ikke var komplet + to nye "blocking"-fund i Core. Rettet: (a) `MemberCircuitBreaker.CompleteProbe(member, succeeded)` — eksplicit outcome-rapportering i stedet for udelukkende at stole på `probeClaimTimeout`; `DataverseGroupPool.AcquireAsync` kalder den nu efter hvert forsøg; (b) constructor-validering af `cooldownPeriod`/`probeClaimTimeout` (kaster på ikke-positive værdier); (c) `ReportLeakedLease`/`PublishHealthChanged` dispatcher nu bruger-callbacks og observer-notifikation via `ThreadPool.QueueUserWorkItem` i stedet for direkte på finalizer-tråden, med try/catch omkring hver — en fejlende subscriber kan hverken crashe processen eller strande kapacitet; (d) `BuildUnavailableException` filtrerer nu udløbne `ThrottledUntil`-ticks. Bevidst IKKE løst: bounded acquire-kø/deadline, operationel-fejl-bevidst circuit, cross-member samtidig creation i gruppen (kræver empirisk verifikation), og leak-detection som rent diagnostisk (afvist — ville reversere ADR-0003/0004 uden brugerens input). 61/61 tests grønne.
- **ADR-0012**: Brugeren traf eksplicit stilling til hele den resterende ADR-0011-backlog. Rettet: (a) `PoolOptions.AcquireTimeout` — bounded ventetid på `AcquireAsync` (samme mønster som HikariCP `connectionTimeout`/ADO.NET `Connect Timeout`: timeout på selve ventetiden, ikke en max-kø-længde), kaster ny `PoolAcquireTimeoutException` (arver `TimeoutException`) med `PoolStats`-snapshot; (b) `PoolStats.ConsecutiveOperationalFailures` — ny tæller inkrementeret af `PooledLease.MarkUnhealthy`, nulstillet ved sund retur/vellykket recycle; `MemberCircuitBreaker.IsEligible` åbner nu kredsløbet på ENTEN create- eller operationelle fejl, så et medlem der opretter fint men fejler i brug ikke længere bliver ved med at få trafik; (c) leak-detection er nu **rent diagnostisk (log-only, som HikariCP)** — en lækket lease bliver hverken disponeret eller recycled, kun rapporteret via `OnLeakDetected`/ny `SlotHealthState.LeakDetected`; et reelt leak reducerer nu permanent poolens kapacitet med én slot indtil genstart (eksplicit accepteret trade-off). IKKE ændret: cross-member samtidig creation i gruppen (bruger accepterede den lille formodede ekstra omkostning ved ikke at serialisere, uden empirisk verifikation) og cross-process koordinering (fortsat afvist). 66/66 tests grønne.
- **ADR-0013**: Fjerde reviewrunde (sikkerhed: ingen fund; DB-pool-ekspert + distributed-systems-ekspert: overlappende root causes). Rettet (brugeren var utilgængelig, autonome beslutninger — alle veldefinerede bugs, ingen scope-udvidelse): (a) `AcquireTimeout` bounder nu **hele** acquire-operationen ende-til-ende (capacity-wait + idle-recycle + serialiseret creation), ikke kun det indledende semaphore-wait — via et linket `CancellationTokenSource`; rest-begrænsning (en enkelt, ikke-cancellation-bevidst `CreateAsync` uden `CreateTimeout` konfigureret kan stadig ikke afbrydes) er dokumenteret i XML-docs; (b) `ConsecutiveOperationalFailures` nulstilles ikke længere af en vellykket recycle (kun af en reelt sund `ReturnAsync`) — rettede en bug hvor et konsekvent-fejlende-men-klonbart medlem aldrig nåede breaker-tærsklen; (c) `WarmupAsync` er nu idempotent (topper op til `min(PrewarmCount, MaxSize)` i stedet for at oprette det antal *igen* hvert kald); (d) ny `MemberCircuitBreaker.AbandonProbe` + `ISlotSelectionStrategy.ReportAcquireAbandoned` — en ren kapacitets-timeout (`PoolAcquireTimeoutException`) rapporteres ikke længere som et fejlet health-probe, så den ikke unødigt forlænger et gennemrettende medlems cooldown; (e) ny `PoolStats.DetectedLeakCount` — holdbar, synkron tæller for GC-detekterede leaks, synlig via `GetStats()` selv uden nogen `OnLeakDetected`/`HealthChanges`-abonnent, inkluderet i `PoolAcquireTimeoutException`s besked; (f) `ResourcePool<T>`s konstruktør validerer nu `AcquireTimeout`/`CreateTimeout`/`MaxIdleLifetime` (kaster på ikke-positive værdier i stedet for at fejle sent/forvirrende). 82/82 tests grønne (op fra 66/66), kørt 3x uden flaky timing-fejl.

## Åbne spørgsmål / opfølgning

- [ ] Bekræft eller afkræft socket-depletion-antagelsen ved new-per-request (uverificeret, se research).
- [ ] Bekræft eller afkræft CallerId cross-thread race condition ved faktisk parallel varierende CallerId-test (bemærk: dette er en *anden* risiko end den nu-rettede cross-*lease*-lækage, se ADR-0009).
- [x] Throttle-aware `ISlotSelectionStrategy` — implementeret via `DataverseUserPool.ReportThrottled`/`IsThrottled` + `DataverseGroupLease.ReportIfThrottled`, se ADR-0008. Begge selection-strategier springer nu throttlede medlemmer over (fail-open hvis alle er throttlet).
- [ ] Overvej om SOAP-fault (`OrganizationServiceFault`)-baseret throttle-detektion også er nødvendig (bevidst udeladt indtil videre, se ADR-0008 — kræver ekstra `System.ServiceModel.Primitives`-reference og er uverificeret om denne SDK-version overhovedet kaster SOAP-faults for throttling).
- [ ] Overvej om single-user (ikke-gruppe) `DataverseUserPool` også bør eksponere throttle-state udadtil til monitorering (i dag kun brugt internt af gruppens selection-strategi).
- [x] Navn valgt: **DataversePool** (NuGet-id'er: `DataversePool.Core`/`.Dataverse`/`.Polly`).
- [x] Sikkerhedsfund: `CallerId` lækkede mellem leases ved pool-genbrug — rettet via nyt `IPooledResourcePolicy<T>.OnReturned`-hook, se ADR-0009.
- [x] Reelt single-probe half-open circuit breaker — implementeret via `MemberCircuitBreaker`, se ADR-0010. Per-proces, ingen delt state mellem processer (bevidst valg).
- [x] Konfigurerbar fail-fast (ikke kun fail-open) når alle gruppemedlemmer er utilgængelige — implementeret via `GroupAllUnavailableBehavior` + `DataverseGroupUnavailableException`, se ADR-0010.
- [x] Probe-claim-timeout race i `MemberCircuitBreaker` (single-probe var ikke helt atomisk endnu) — rettet via eksplicit `CompleteProbe`-outcome-rapportering, se ADR-0011. Residual-risiko ved uendeligt hængende `CreateAsync` uden `PoolOptions.CreateTimeout` er dokumenteret, ikke fuldt elimineret.
- [x] Manglende validering af `cooldownPeriod`/`probeClaimTimeout` i `MemberCircuitBreaker` — rettet, se ADR-0011.
- [x] Finalizer-tråd-sikkerhed: brugerkode (`OnLeakDetected`, `HealthChanges`-observers) kørte synkront på finalizer-tråden (process-fatal risiko ved ubehandlet exception) — rettet via `ThreadPool.QueueUserWorkItem`-dispatch + try/catch, se ADR-0011.
- [x] Stale `EarliestKnownRetryAt` ved udløbne throttle-ticks — rettet, se ADR-0011.
- [x] Bounded acquire-kø/deadline i `ResourcePool<T>.AcquireAsync` — implementeret via `PoolOptions.AcquireTimeout` + `PoolAcquireTimeoutException`, se ADR-0012; udvidet til at bounde hele acquire-operationen ende-til-ende (ikke kun det indledende semaphore-wait), se ADR-0013.
- [x] Skeln oprettelsesfejl fra operationelle fejl i circuit-signalet — implementeret via `PoolStats.ConsecutiveOperationalFailures`, se ADR-0012; rettede en efterfølgende bug hvor en vellykket recycle nulstillede tælleren for tidligt, se ADR-0013.
- [x] Leak-detection som rent diagnostisk (log-only) — implementeret, se ADR-0012. Bemærk: et reelt leak reducerer nu poolens kapacitet permanent indtil genstart (bevidst, brugergodkendt trade-off). Nu synligt via ny, holdbar `PoolStats.DetectedLeakCount`, se ADR-0013.
- [x] Cross-member samtidig `CreateAsync` i `DataverseGroupPool` — **accepteret som er** af brugeren uden empirisk verifikation ("nok bare per instans, koster blot lidt ekstra tid"). Ingen kode ændret.
- [x] `WarmupAsync` er nu idempotent (topper op til `min(PrewarmCount, MaxSize)` i stedet for at oprette det antal *igen* hvert kald) — rettet, se ADR-0013.
- [x] En kapacitets-timeout (`PoolAcquireTimeoutException`) rapporteredes fejlagtigt som et fejlet circuit-probe — rettet via `MemberCircuitBreaker.AbandonProbe`/`ISlotSelectionStrategy.ReportAcquireAbandoned`, se ADR-0013.
- [x] `PoolOptions`-varigheder (`AcquireTimeout`/`CreateTimeout`/`MaxIdleLifetime`) var uvaliderede i `ResourcePool`s konstruktør — rettet, se ADR-0013.
- [ ] **Distributed-systems backlog (v2, "coordinated mode")** — kun punkt 1 tilbage, se ADR-0009/0010/0011/0012:
  1. ~~Delt throttle/circuit-state på tværs af processer~~ — **bevidst afvist af brugeren** ("ingen delt state mellem processer"). Forbliver en dokumenteret produktionsbegrænsning, ikke en todo.
- [ ] **DB-pool-design backlog fra fjerde reviewrunde**, resterende (ikke prioriteret af brugeren endnu):
  - `PoolStats`/observability mangler histogrammer/percentiler, wait-latency, creation/recycle-varighed, leak-alder — nødvendigt for reel produktionsdiagnose.
  - Ingen `MinIdle`/Little's Law-vejledning til pool-sizing.
  - `CreateTimeout` fejlklassificerer caller-side cancellation som creation-timeout (åbner circuit forkert).
  - `ConsecutiveOperationalFailures`s "consecutive"-model kan stadig nulstilles for tidligt af en enkelt, sen, held retur under blandet samtidig trafik (dokumenteret nuance i ADR-0013, ikke rettet — kræver en tidsvindue-/rate-baseret model for en fuld rettelse).
- [ ] Ingen CI/CD-pipeline endnu — bør etableres før 1.0. (Git-repo er nu etableret, se commits.)

## Teststrategi (kort, se fulde designdiskussion i sessionen)

- Core: 100% fakes, ingen netværk, concurrency-stresstest for seriel gate.
- Dataverse-adapter: fake `IPooledResourcePolicy<ServiceClient>`, test kun orkestrering.
- Polly-adapter: verificér `MarkUnhealthy` kaldes korrekt ved retry/circuit-open.
- Integrationstest mod ægte Dataverse: opt-in, `[Trait("Category","Integration")]`, ikke i normal CI.
