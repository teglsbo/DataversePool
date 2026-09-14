# ADR-0011: Outcome-baseret probe-completion + finalizer-tråd-sikkerhed

## Status
Accepteret (delvist — se "Bevidst ikke løst her" for hvad der forbliver åbent)

## Kontekst
Efter ADR-0009/0010 blev committet, blev endnu en runde af tre parallelle reviews kørt mod hele
kodebasen: sikkerhed, en DB-connection-pool-designekspert, og en distribueret-systemer-ekspert
(der specifikt genverificerede ADR-0009/0010-fixene i stedet for at tage dem for pålydende).

**Sikkerhed**: ingen nye fund. CallerId-scrubbing (ADR-0009) blev bekræftet korrekt og komplet;
`MemberCircuitBreaker`s lock blev vurderet race-fri på selve datastrukturen.

**Distribueret-systemer-genreview** fandt at ADR-0010's single-probe-fix **ikke var komplet**:
1. `probeClaimTimeout` er en blind timer, ikke et signal om probens reelle udfald. Hvis den
   oprindelige prober stadig hænger i `CreateAsync` (ubegrænset uden `PoolOptions.CreateTimeout`)
   når timeout'en udløber, kan et *nyt* probe tildeles samtidig med det gamle — præcis den
   thundering-herd-bug ADR-0010 skulle løse.
2. `MemberCircuitBreaker`s konstruktør validerede ikke at `cooldownPeriod`/`probeClaimTimeout` er
   positive — `TimeSpan.Zero` ville lade alle samtidige callers vinde probet på én gang.
3. (Bekræftet fortsat åbent, forventet): circuit måler kun creation-fejl, ikke operationelle fejl.
4. (Bekræftet fortsat åbent, forventet): ingen bounded acquire-kø/deadline i `ResourcePool`.
5. (Non-blocking): `EarliestKnownRetryAt` i `DataverseGroupUnavailableException` kunne rapportere
   et allerede udløbet throttle-tidspunkt, fordi throttle-ticks aldrig ryddes efter udløb.

**DB-pool-designeksperten** fandt tre "blocking"-niveau fund:
1. **Finalizer-baseret leak-reclaim kan disponere en ressource der reelt stadig er i brug** — hvis
   en caller kun taber sin reference til selve `PooledLease<T>` (men stadig bruger
   `lease.Resource` et sted), kan GC finalizere leasen og udløse recycle/dispose, mens ressourcen
   samtidig bruges. Modsat fx HikariCP, der som udgangspunkt kun *logger* formodede leaks uden at
   handle på dem.
2. **Bruger-callbacks (`OnLeakDetected`, `HealthChanges`-observers) kaldes synkront på selve
   finalizer-tråden.** En ubehandlet exception der er process-fatal, og en langsom/blokerende
   subscriber ville forsinke finalization af alt andet.
3. **Den serielle creation-gate (ADR-0002) gælder kun *inden for* én `DataverseUserPool`s egen
   `ResourcePool`** — under normal belastning (ikke kun warmup) kan `DataverseGroupPool` sagtens
   udløse samtidige `CreateAsync`-kald på tværs af *forskellige* medlemmer, hvilket potentielt kan
   ramme den samme SDK-interne lock-contention (1-3.2s) ADR-0002 identificerede — men dette er
   uverificeret uden empirisk måling af, om SDK'ens interne lock er per-instans eller
   proces-global.

## Beslutning

### Fix: `MemberCircuitBreaker.CompleteProbe(member, succeeded)` — eksplicit outcome-rapportering
I stedet for udelukkende at stole på at `probeClaimTimeout` udløber, kan/skal kaldere nu
rapportere det faktiske udfald af et acquire-forsøg:
- **Success** rydder al breaker-bogføring for medlemmet med det samme (lukker kredsløbet uden at
  vente på næste stats-snapshot eller på at `probeClaimTimeout` udløber).
- **Failure** genstarter cooldown-vinduet fra nu og frigiver claim'et med det samme, så *næste*
  cooldowns probe ikke unødigt blokeres af et allerede afgjort forsøg.

`DataverseGroupPool.AcquireAsync` kalder nu `_strategy.ReportAcquireOutcome(member, succeeded)`
efter hvert forsøg (i en try/catch omkring selve `AcquireAsync`-kaldet på det valgte medlem) —
`OperationCanceledException` fra callerens egen cancellation-token rapporteres bevidst IKKE som et
outcome (annullering er ikke et sundhedssignal om medlemmet). `ISlotSelectionStrategy` fik en ny
default-no-op-metode `ReportAcquireOutcome`, så strategier uden circuit-breaking (almindelig
round-robin) ikke behøver ændres.

**Dette lukker ikke hele hullet, og det påstår vi ikke at det gør:** hvis selve acquire-forsøget
hænger uendeligt uden `PoolOptions.CreateTimeout` sat, rapporteres intet outcome, og
`probeClaimTimeout`-fallback'en er stadig det der til sidst tillader et nyt probe — den
oprindelige race fra reviewen kan stadig i teorien opstå i det tilfælde. Ligeledes: hvis et
half-open-forsøg tilfældigvis rammer en allerede oprettet idle-ressource (ingen `CreateAsync`
kaldes overhovedet), beviser et "success" ikke at medlemmet reelt kan oprette forbindelser igen —
det er stadig en tilnærmelse. Anbefalingen til operatører er derfor eksplicit: **sæt
`PoolOptions.CreateTimeout`** for at bounded dette residual-tilfælde. Dokumenteret direkte i
XML-doc på `CompleteProbe`.

### Fix: input-validering i `MemberCircuitBreaker`-konstruktøren
`cooldownPeriod <= TimeSpan.Zero` og `probeClaimTimeout <= TimeSpan.Zero` kaster nu
`ArgumentOutOfRangeException` i stedet for stiltiende at acceptere værdier der ville ødelægge
single-probe-garantien.

### Fix: finalizer-tråd-sikkerhed i `ResourcePool<T>`
`ReportLeakedLease` (kaldt direkte fra `PooledLease<T>`s finalizer) er nu begrænset til billige,
exception-fri felt-opdateringer. Selve callback-kaldet (`OnLeakDetected`) og
observer-notifikationen (`PublishHealthChanged`/`HealthChanges`) dispatches nu via
`ThreadPool.QueueUserWorkItem` til en almindelig trådpool-tråd — ikke finalizer-tråden. Både
`OnLeakDetected`-kaldet og hver enkelt observers `OnNext` er desuden nu wrappet i try/catch: en
fejlende subscriber kan hverken (a) crashe processen via en ubehandlet exception på
finalizer-tråden, (b) blokere andre observers fra at blive notificeret, eller (c) forhindre at
slottet stadig bliver sendt til recycling bagefter.

**Vi ændrede bevidst IKKE den grundlæggende arkitektur-beslutning** (ADR-0003/0004) om at en
leaked lease udløser recycle/dispose af den underliggende ressource. DB-pool-ekspertens stærkere
anbefaling — gør leak-detection rent diagnostisk (kun log, aldrig disponer, som HikariCP) — blev
overvejet, men afvist for nu: det ville reversere en allerede truffet, dokumenteret
design-beslutning uden brugerens eksplicitte input, og det fjerner en sikkerhedsnet-egenskab
(en virkelig glemt/lækket ressource bliver aldrig genbrugt af en fremtidig caller i ukendt
tilstand). Den *reelle* bug her var ikke "at recycle er en dårlig idé", men at
callback-eksekveringen skete usikkert på finalizer-tråden — det er rettet. Risikoen for at
disponere en teknisk stadig-i-brug ressource (fordi kun lease-wrapperen, ikke selve ressourcen,
blev tabt af referencer) er en iboende konsekvens af selve leak-detection-designet og forbliver
dokumenteret som en kendt afvejning, ikke en bug at "rette" uden at ændre hele modellen.

### Fix: stale throttle-tick i `DataverseGroupPool.BuildUnavailableException`
`EarliestKnownRetryAt`-beregningen filtrerer nu `ThrottledUntil`-værdier til kun dem der stadig er
i fremtiden (`> DateTimeOffset.UtcNow`) før `Min()` beregnes — en allerede udløbet throttle-tick
(som aldrig ryddes proaktivt) rapporteres ikke længere fejlagtigt som et gyldigt fremtidigt
retry-tidspunkt.

## Bevidst ikke løst her
Følgende fund fra denne reviewrunde er **bekræftet reelle, men bevidst ikke rettet** i denne ADR —
enten fordi de kræver et større arkitektur-skifte, empirisk verifikation, eller en eksplicit
produktbeslutning fra brugeren, som ikke var tilgængelig da dette arbejde blev udført:

- **Bounded acquire-kø/deadline** (`AcquireTimeout`/`MaxWaiters` i `ResourcePool.AcquireAsync`) —
  vurderet af begge eksperter som højeste tilbageværende prioritet. Ikke bygget her, da det er en
  ikke-triviel, potentielt breaking API-udvidelse (ny option, ny exception-type for
  queue-timeout/overflow) der fortjener sin egen dedikerede runde.
- **Outcome-drevet circuit breaker for operationelle fejl** (ikke kun creation-fejl) — kræver at
  `PooledLease.MarkUnhealthy` (eller en ny mekanisme) fodrer samme `ConsecutiveCreateFailures`-agtige
  signal som group-strategierne læser, hvilket er en større ændring af `PoolStats`/`ResourcePool`s
  ansvarsfordeling.
- **Cross-member samtidig `CreateAsync` i `DataverseGroupPool`** — DB-pool-eksperten selv
  anbefalede empirisk verifikation (måle om SDK'ens lock-contention er per-instans eller
  proces-global) FØR man bygger en global gate på tværs af gruppe-medlemmer, for ikke at
  introducere unødig serialisering, der ikke løser et reelt problem.
- **Leak-detection som rent diagnostisk (log-only)** — se begrundelse ovenfor; en bevidst afvigelse
  fra DB-pool-ekspertens anbefaling, ikke en fejl.
- **Cross-process budget-koordinering** (ADR-0009/0010's punkt #1) — fortsat eksplicit afvist af
  brugeren ("ingen delt state mellem processer").

## Konsekvenser
- **Ikke breaking**: `CompleteProbe` og `ReportAcquireOutcome` er additive API'er
  (`ISlotSelectionStrategy.ReportAcquireOutcome` har default no-op-implementering). Eksisterende
  brugerdefinerede strategier kompilerer uændret.
- `MemberCircuitBreaker`-konstruktøren kaster nu for tidligere gyldige (men meningsløse)
  `TimeSpan.Zero`/negative inputs — teknisk breaking for enhver der (fejlagtigt) brugte disse, men
  vurderet ønskværdigt: den slags konfiguration var altid en bug.
- Test-dækning: 61/61 grønne efter denne ændring (Core 17 [+2], Dataverse 41 [+7], Polly 3),
  inklusiv nye tests for TimeSpan-validering, `CompleteProbe` success/failure-adfærd, stale
  throttle-tick-filtrering, og GC-triggered leak-reporting med en bevidst fejlende
  callback/observer (verificerer ingen exception undslipper, og at slottet stadig recycles).
