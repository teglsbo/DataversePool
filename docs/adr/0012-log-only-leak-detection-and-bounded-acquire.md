# ADR-0012: Bounded acquire (timeout), operationel-fejl-bevidst circuit breaker, log-only leak-detection

## Status
Accepteret

**Opdatering (ADR-0013):** `AcquireTimeout`, som beskrevet her, bandt oprindeligt kun
`_capacityGate.WaitAsync` — ikke resten af acquire-operationen (recycle/creation efter permit).
ADR-0013 udvider den til at bounde hele acquire-operationen ende-til-ende. Ligeledes nulstillede
`ConsecutiveOperationalFailures` (introduceret her) oprindeligt ved enhver vellykket recycle, hvilket
ADR-0013 retter. Se ADR-0013 for detaljerne; denne ADR's øvrige beslutninger (log-only
leak-detection, #3/#5 accepteret uændret) forbliver gældende som beskrevet.

## Kontekst
Efter ADR-0011 blev committet, blev brugeren præsenteret for den resterende, bevidst udskudte
backlog fra anden reviewrunde (DB-pool-designeksperten + distributed-systems-genreview) og traf
eksplicitte beslutninger om hvert punkt:

1. **Bounded acquire-kø/deadline** — vurderet af begge eksperter som højeste tilbageværende
   prioritet: `ResourcePool<T>.AcquireAsync` venter i dag uendeligt på kapacitet, uden mulighed for
   at give reel backpressure. Brugerens svar: "lyder som en god ide at have den bounded" + spurgte
   hvad typisk DB-pool-praksis er.
2. **Operationel-fejl-bevidst circuit breaker** — circuit'en i `MemberCircuitBreaker` reagerede kun
   på creation-fejl (`ConsecutiveCreateFailures`), ikke på operationelle fejl rapporteret via
   `PooledLease.MarkUnhealthy` på en allerede oprettet ressource. Brugerens svar: "ja - nødvendigt,
   så man ikke bliver ved med at pushe en dårlig rundt til alle."
3. **Cross-member samtidig `CreateAsync` i `DataverseGroupPool`** — DB-pool-eksperten flaggede dette
   som en mulig reintroduktion af ADR-0002's lock-contention-problem, men anbefalede selv empirisk
   verifikation af om SDK'ens interne lock er per-instans eller proces-global, før man bygger en
   gate. Brugerens svar: "nok bare per instans, det koster blot lidt ekstra tid at danne, hvis man
   ikke serialiserer." → accepteret som en kendt, lille, ubetydelig omkostning; INGEN kode ændret.
4. **Log-only leak-detection** — DB-pool-eksperten anbefalede (som HikariCP) at gøre
   leak-detection rent diagnostisk i stedet for at recycle/disponere en muligvis-stadig-i-brug
   ressource. ADR-0011 afviste bevidst dette uden brugerens eksplicitte input. Brugerens svar denne
   gang: "log only." → implementeret.
5. **Cross-process koordinering** — fortsat eksplicit afvist ("enig" med at lade det være). Ingen
   ændring.

Typisk praksis for punkt 1, som brugeren spurgte til: HikariCP's `connectionTimeout` (default 30s,
kaster `SQLTransientConnectionException` ved udløb), Npgsql's `Timeout`, og SqlClient's
`Connect Timeout` bruger alle en **timeout på selve ventetiden** på en ledig forbindelse — ikke en
hård grænse på antal ventende callers. Begrundelsen: en ventende caller er billig (blot en
suspenderet `Task`/`await`), så risikoen der skal bounded er caller-pileup/manglende backpressure,
ikke hukommelsesforbrug. Samme mønster er valgt her.

## Beslutning

### Fix for #1: `PoolOptions.AcquireTimeout`
Ny, valgfri (`null` default, bagudkompatibel) `TimeSpan? AcquireTimeout`. Når sat, bruger
`ResourcePool<T>.AcquireAsync` `SemaphoreSlim.WaitAsync(timeout, cancellationToken)` på
capacity-gaten i stedet for et ubegrænset `WaitAsync(cancellationToken)`. Ved udløb kastes en ny,
offentlig `PoolAcquireTimeoutException` (arver `TimeoutException`, som `CreateTimeout`s eksisterende
exception, for konsistent fejlhåndtering) med et `PoolStats`-snapshot til diagnosticering (hvor
mange venter, hvor mange er idle/created, osv.). Ingen ændring i default-adfærd — eksisterende
kald uden `AcquireTimeout` sat venter stadig uendeligt, som før.

### Fix for #2: `PoolStats.ConsecutiveOperationalFailures` + `MemberCircuitBreaker` reagerer på begge signaler
- Ny tæller i `ResourcePool<T>`: `_consecutiveOperationalFailures`, inkrementeret af en ny intern
  `ReportOperationalFailure()`-metode, kaldt fra `PooledLease.MarkUnhealthy` (dvs. hver gang en
  bruger selv rapporterer at en *allerede udleveret* ressource fejlede operationelt — ikke ved
  oprettelse).
- Nulstilles ved enhver efterfølgende sund `ReturnAsync` (et vellykket brug er bevis på
  genopretning) og ved vellykket `RecycleInPlaceAsync` (en frisk ressource antages operationelt
  sund igen) — samme mønster som `ConsecutiveCreateFailures` allerede brugte.
- Eksponeret på `PoolStats` som `ConsecutiveOperationalFailures`.
- `MemberCircuitBreaker.IsEligible` åbner nu kredsløbet hvis **enten** `ConsecutiveCreateFailures`
  **eller** `ConsecutiveOperationalFailures` når tærsklen — et medlem der opretter fint, men hvis
  ressourcer konsekvent fejler i faktisk brug, behandles nu lige så alvorligt som et medlem der slet
  ikke kan oprettes, og roteres væk fra i stedet for at blive ved med at få trafik sendt til sig
  ("pushe en dårlig rundt til alle", som brugeren formulerede det).

### Fix for #4: Log-only leak-detection
`ResourcePool<T>.ReportLeakedLease`/`CompleteLeakReport` er omskrevet til **rent diagnostisk**:
- Ved en GC-detekteret lækket lease markeres kun incident-metadata (til `OnLeakDetected`/
  `HealthChanges`) — poolen kalder IKKE længere `RecycleInBackgroundAsync`, disponerer IKKE
  ressourcen, og frigiver IKKE capacity-permit'en.
- Ny `SlotHealthState.LeakDetected`-værdi (adskilt fra `MarkedUnhealthy`, som stadig betyder "vil
  blive recycled") gør det muligt for observability-kode at skelne "vi har mistanke om et leak, rent
  informativt" fra "denne slot bliver aktivt genoprettet nu."
- **Konsekvens, eksplicit accepteret af brugeren:** et reelt leak reducerer nu poolens effektive
  kapacitet permanent med én slot, indtil processen genstartes — nøjagtig samme reelle
  drift-erfaring som HikariCP's log-only leak-detection giver i praksis. `OnLeakDetected`/
  `HealthChanges` er derfor ikke længere "nice to have" telemetri, men den eneste måde man kan
  opdage at dette er sket og handle på det (alarmere, genstarte processen, undersøge kildekoden for
  det manglende `DisposeAsync`-kald).
- Finalizer-tråd-sikkerheden fra ADR-0011 (dispatch via `ThreadPool.QueueUserWorkItem`, try/catch om
  callback og hver observer) er bevaret uændret — kun *hvad* der sker efter rapporteringen er
  ændret, ikke *hvordan* den rapporteres sikkert.
- `PooledLease<T>` og `PoolOptions.OnLeakDetected`s XML-docs er opdateret til at beskrive den nye
  adfærd. ADR-0003 (som oprindeligt beskrev leak-tracking som "evakuerer/genopretter slotten") er
  annoteret med en henvisning til denne ADR i stedet for at blive omskrevet, jf. `docs/adr/README.md`s
  konvention om ikke at redigere en accepteret ADR's beslutning.

### #3 og #5: ingen kodeændring
- **#3** (cross-member samtidig creation): brugeren accepterede den formodede lille ekstra
  clone-omkostning ved ikke at serialisere på tværs af gruppemedlemmer, fremfor at bygge en
  uverificeret gate. Ingen kode ændret; forbliver dokumenteret som en kendt, accepteret afvejning.
- **#5** (cross-process koordinering): fortsat eksplicit afvist. Ingen ændring.

## Konsekvenser
- **Bagudkompatibelt for #1/#2**: `AcquireTimeout` er `null` som default (ubegrænset ventetid,
  uændret adfærd); `ConsecutiveOperationalFailures` er en ny, additiv `PoolStats`-property med
  default `0`.
- **Adfærdsændring for #4 (bevidst, ikke bagudkompatibel i praksis)**: enhver eksisterende bruger
  der implicit har regnet med at et leak bliver "repareret af poolen selv" oplever nu i stedet en
  permanent reduktion af effektiv kapacitet ved et reelt leak. Dette er en tilsigtet
  produktbeslutning truffet eksplicit af brugeren, ikke en regression.
- `PoolAcquireTimeoutException` er en ny offentlig type (arver `TimeoutException`, fanges derfor
  også af eksisterende `catch (TimeoutException)`-kode der allerede håndterer `CreateTimeout`).
- Test-dækning: 66/66 grønne efter denne ændring (Core 21 [+4: 3 nye `AcquireTimeoutTests` og net
  +1 i leak-tests efter omskrivning fra 2 til 3 log-only-tests], Dataverse 42 [+1: operationel-fejl
  åbner kredsløbet], Polly 3), op fra 61/61. `LeakReportingSafetyTests` fra ADR-0011 er omskrevet
  til at verificere den nye log-only-adfærd (aldrig disponeret, capacity permanent tabt, stadig
  ingen crash/strandet kapacitet ved fejlende callback/observer) i stedet for den gamle
  recycle-adfærd.
