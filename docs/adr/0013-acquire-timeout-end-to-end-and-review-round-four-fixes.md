# ADR-0013: Ende-til-ende AcquireTimeout, retfærdig operationel-fejl-tælling, idempotent warmup, korrekt probe-outcome-rapportering, holdbar leak-synlighed

## Status
Accepteret. **Delvist superseret af [ADR-0014](0014-probe-claim-generation-and-cancellation-vs-createtimeout-misclassification.md):**
en femte reviewrunde fandt at fix #1 (ende-til-ende `AcquireTimeout`) og fix #4 (`AbandonProbe`)
kun var delvist effektive — en ny regression (en `AcquireTimeout`-cancellation, der rammer under en
igangværende `CreateAsync`, blev fejlagtigt klassificeret som et `CreateTimeout` og dermed et fejlet
health-probe) omgik begge. ADR-0014 retter denne regression samt tilføjer claim-generation-
korrelation til `MemberCircuitBreaker`. Fix #2, #3, #5, #6 nedenfor forbliver upåvirkede og gyldige.

## Kontekst
Efter ADR-0012 blev committet (`0c35c39`), bad brugeren om endnu en (fjerde) reviewrunde: sikkerhed
+ DB-pool-designekspert + distributed-systems-ekspert, alle rettet mod den netop committede kode.
Sikkerhedsreviewet fandt intet nyt. De to andre fandt, uafhængigt af hinanden, i store træk **samme
root cause** fra to forskellige vinkler, samt yderligere reelle, veldefinerede fejl:

1. **`AcquireTimeout` bounder kun `_capacityGate.WaitAsync`, ikke hele acquire** — begge eksperter
   fandt dette. DB-pool-eksperten: en gang permit er opnået, kan recycle/creation efter det tage
   ubegrænset tid, uden nogen `AcquireTimeout`-grænse. Distributed-systems-eksperten: det var
   *også* årsagen til at probe-claim-race'en fra ADR-0011 stadig var reelt åben — den første
   half-open prober kan sidde fast i denne ubundne fase, mens `probeClaimTimeout` udløber og en ny
   prober vinder, selvom den første stadig legitimt venter (ikke er hængt).
2. **`ConsecutiveOperationalFailures` nulstilles for aggressivt** — enhver vellykket
   `RecycleInPlaceAsync` (dvs. en frisk klon efter en operationel fejl) nulstillede tælleren. Et
   medlem hvis operationer konsekvent fejler, men hvis `ServiceClient.Clone` fortsat lykkes, ville
   derfor aldrig nå tærsklen: fail → recycle lykkes → reset → fail → recycle lykkes → reset...
   Underminerede hele formålet med ADR-0012's operationelle circuit-signal.
3. **`WarmupAsync` er ikke idempotent** — hvert kald opretter `min(PrewarmCount, MaxSize)` *flere*
   ressourcer, uden hensyn til hvor mange der allerede findes. Et gentaget kald (fx en retried
   startup-hook) kan derfor overskride `MaxSize`.
4. **En kapacitets-timeout rapporteres fejlagtigt som et fejlet circuit-probe** —
   `PoolAcquireTimeoutException` fanges af `DataverseGroupPool.AcquireAsync`s generelle `catch` og
   rapporteres til `MemberCircuitBreaker` som `succeeded: false`. En ren load/kapacitets-timeout er
   ikke bevis på at *medlemmet* er usundt, men kunne unødigt forlænge et gennemrettende medlems
   cooldown.
5. **Log-only leaks er usynlige i `PoolStats`** — den eneste evidens for et permanent tabt slot
   (ADR-0012's bevidste afvejning) var en flygtig callback/event; en sen eller manglende abonnent
   kan ikke se det bagefter, og en generisk `PoolAcquireTimeoutException` ser identisk ud uanset om
   årsagen er reel belastning eller lækket kapacitet.
6. **`PoolOptions`-varigheder var uvaliderede** i `ResourcePool`s konstruktør (kun `MaxSize` blev
   tjekket) — en negativ `AcquireTimeout`/`CreateTimeout`/`MaxIdleLifetime` ville fejle sent og
   forvirrende i stedet for med det samme.

Brugeren var ikke tilgængelig for prioritering af denne runde (autopilot); jeg valgte derfor at
rette alle seks fund, da de alle er veldefinerede bugs/robusthedsforbedringer uden
arkitektur-reversering eller ny scope, i tråd med mønsteret fra tidligere runder.

## Beslutning

### Fix for #1: Ende-til-ende `AcquireTimeout`
`ResourcePool<T>.AcquireAsync` opretter nu, når `PoolOptions.AcquireTimeout` er sat, en
`CancellationTokenSource` med den varighed, linket med callerens eget token via
`CancellationTokenSource.CreateLinkedTokenSource`. Dette linkede token (`effectiveToken`) bruges
til **alt** efterfølgende arbejde: `_capacityGate.WaitAsync`, idle-lifetime-recycle
(`RecycleInPlaceAsync`), og ny oprettelse (`CreateNewSlotAsync` → `CreateThroughGateAsync`, inkl.
dens egen `_creationGate.WaitAsync`). Udløber deadline'en (og ikke callerens eget token), fanges
`OperationCanceledException` og omsættes til `PoolAcquireTimeoutException` — ikke en rå
cancellation, der ellers ville være umulig at skelne fra caller-initieret annullering.

**Vigtig, dokumenteret rest-begrænsning:** dette er *kooperativ* annullering. Hvis den konkrete,
igangværende `CreateAsync`-opkald hverken selv respekterer `CancellationToken` eller er bundet af
`PoolOptions.CreateTimeout`, kan netop det opkald ikke afbrydes af `AcquireTimeout` alene — kun
ventetiden *foran* det opkald (fx bag `_creationGate` mens et andet, serialiseret opkald kører) er
garanteret bounded. `AcquireTimeout`s XML-doc er opdateret til eksplicit at anbefale at konfigurere
`CreateTimeout` sammen med `AcquireTimeout` for en reel worst-case-grænse. Dette er samme kategori
rest-risiko som den allerede dokumenterede "hængende `CreateAsync` uden `CreateTimeout`"-begrænsning
fra ADR-0011's `probeClaimTimeout`-fallback.

Ny regressionstest beviser den konkrete, oprindeligt rapporterede fejl er rettet: med `MaxSize=2`
kan begge callere straks få en kapacitets-permit, men oprettelse er altid serialiseret
(`docs/adr/0002`) — den anden caller, der før ville vente ubegrænset bag den førstes langsomme
oprettelse, timer nu korrekt ud nær `AcquireTimeout`.

### Fix for #2: Operationel-fejl-tælleren nulstilles kun ved en reel sund retur
Fjernet: `Interlocked.Exchange(ref _consecutiveOperationalFailures, 0)` fra
`RecycleInPlaceAsync`s success-gren. En vellykket kloning af en erstatningsressource er bevis på at
medlemmet *kan oprettes*, ikke at det kan udføre en reel operation succesfuldt — kun en
efterfølgende, faktisk sund `ReturnAsync` (dvs. ingen `MarkUnhealthy` blev kaldt på leasen) nulstiller
nu tælleren. Et medlem hvis operationer konsekvent fejler vil derfor korrekt akkumulere
`ConsecutiveOperationalFailures` på tværs af gentagne recycles og til sidst nå tærsklen.

*Kendt, ikke rettet nuance (dokumenteret, ikke et blokerende fund):* en enkelt, langvarig, held
lease der returneres sundt efter at flere nyere leases allerede har fejlet, kan stadig nulstille
tælleren "for tidligt" pga. samtidig trafik — en fuldt tidsvindue-/rate-baseret
outcome-tracker ville løse dette generelt, men er en større arkitekturændring end denne runde
retter; den nuværende "consecutive"-model er en tilnærmelse, ligesom før.

### Fix for #3: `WarmupAsync` er nu idempotent
Omskrevet fra "opret ubetinget `min(PrewarmCount, MaxSize)` *flere*" til "sørg for at
`_createdCount` når mindst `min(PrewarmCount, MaxSize)`, ved kun at oprette differencen". Tjekker
`_createdCount` før *og* efter at have taget en kapacitets-permit (for at håndtere race med
samtidige `WarmupAsync`/lazy `AcquireAsync`-oprettelser), og topper kun op med den reelle
mangel. Permit-livscyklussen er uændret ift. før (permit tages, ressource oprettes og lægges i
`_idle`, permit frigives igen — idle-ressourcer holder aldrig selv en permit i denne pools model;
kun en aktiv lease/oprettelse gør). Gentagne kald med samme `PrewarmCount` er nu no-ops.

### Fix for #4: Kapacitets-timeout rapporteres ikke længere som fejlet probe
Ny `MemberCircuitBreaker.AbandonProbe(member)`: frigiver en klaimet half-open-probe **uden** at
genstarte cooldown-vinduet (i modsætning til `CompleteProbe(succeeded: false)`). Ny default no-op
`ISlotSelectionStrategy.ReportAcquireAbandoned(member)`, implementeret af begge
circuit-breaker-bevidste strategier til at kalde `_breaker.AbandonProbe`.
`DataverseGroupPool.AcquireAsync` fanger nu `PoolAcquireTimeoutException` specifikt, *før* det
generelle `catch`, og kalder `ReportAcquireAbandoned` i stedet for `ReportAcquireOutcome(false)`.

### Fix for #5: `PoolStats.DetectedLeakCount` — holdbar leak-synlighed
Ny, monotont stigende `PoolStats.DetectedLeakCount`, inkrementeret **synkront** i
`ReportLeakedLease` (før callback/event-dispatch til `ThreadPool`), så den er pålideligt synlig via
`GetStats()` selv hvis ingen `OnLeakDetected`/`HealthChanges`-abonnent nogensinde var tilsluttet.
`PoolAcquireTimeoutException`s besked inkluderer nu `DetectedLeakCount` og, hvis > 0, en eksplicit
note om at stigende leak-count sammen med timeouts indikerer tabt kapacitet, ikke kun belastning.

### Fix for #6: `PoolOptions`-varigheder valideres i konstruktøren
`ResourcePool<T>`s konstruktør kaster nu `ArgumentOutOfRangeException` hvis `AcquireTimeout`,
`CreateTimeout`, eller `MaxIdleLifetime` er sat til en ikke-positiv varighed. `null` er fortsat den
eneste måde at signalere "deaktiveret/ubegrænset" på for alle tre.

## Konsekvenser
- **Bagudkompatibelt** for alle seks fixes: ingen offentlig default-adfærd ændrer sig for kode der
  ikke bruger de berørte features (`AcquireTimeout`/`CreateTimeout`/`MaxIdleLifetime` forbliver
  `null` som default; `DetectedLeakCount` er en ny, additiv `PoolStats`-property med default `0`;
  `ReportAcquireAbandoned` er en ny default no-op-interface-metode).
- **Skarpere fejlklassificering i `DataverseGroupPool`**: en ren kapacitets/load-timeout påvirker nu
  ikke længere et medlems circuit-cooldown forkert — kun reelle create-/operationelle fejl gør.
- **`ConsecutiveOperationalFailures` er nu et pålideligt signal** for den oprindeligt tilsigtede
  brugssag (et medlem hvis operationer konsekvent fejler, men hvis kloning lykkes) — den kendte
  samtidigheds-nuance (en enkelt sen, held retur kan stadig nulstille under blandet trafik) er
  dokumenteret som en accepteret tilnærmelse, ikke rettet i denne runde.
- **`WarmupAsync` kan nu trygt kaldes flere gange** (fx fra en retried hosted-service-hook) uden at
  risikere at overskride `MaxSize`.
- Test-dækning: **82/82 grønne** (Core 36 [+15], Dataverse 43 [+1], Polly 3), op fra 66/66. Kørt 3x
  i træk uden flaky timing-fejl. Nye tests: end-to-end `AcquireTimeout` (bag serialiseret creation +
  respekt for caller-cancellation), `WarmupAsync`-idempotens (3 tests), operationel-fejl-akkumulering
  på tværs af recycles + korrekt nulstilling ved reel sund retur (2 tests), `AbandonProbe` (1 test),
  `DetectedLeakCount`-synlighed (1 test), `PoolOptions`-varighedsvalidering (6 tests).
