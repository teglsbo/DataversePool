# ADR-0014: Probe-claim-generation-korrelation, og korrekt skelnen mellem AcquireTimeout-annullering og reelt CreateTimeout

## Status
Accepteret

## Kontekst
Brugeren bad om endnu en (femte) reviewrunde efter ADR-0013 blev committet (`5c6dd7d`). Sikkerhed
+ DB-pool-designekspert + distributed-systems-ekspert, alle bedt om aktivt at genverificere (ikke
tro på) ADR-0013s seks fix-påstande mod den committede kode. Sikkerhedsreviewet fandt intet nyt
(CTS-livscyklus, `DetectedLeakCount`, ADR-0009s CallerId-fix mv. bekræftet uændret/korrekte). De to
øvrige eksperter fandt, delvist uafhængigt, at flere af ADR-0013s fixes kun var **delvist
effektive**, samt en **ny regression** introduceret af selve ADR-0013-arbejdet:

1. **`CreateTimeout`/`AcquireTimeout`-forveksling (ny regression, blokerende, fundet af begge
   eksperter uafhængigt):** `CreateThroughGateAsync` racer oprettelse mod
   `Task.WhenAny(createTask, Task.Delay(timeout, cancellationToken))`. Hvis `cancellationToken`
   (ADR-0013s linkede `AcquireTimeout`-deadline-token, eller callerens eget token) udløber *før*
   `CreateTimeout` selv gør, går `Task.Delay` i Canceled-tilstand — men `Task.WhenAny` returnerer
   den alligevel som "vinderen", og koden klassificerede ubetinget dette som et reelt
   `CreateTimeout`-udløb: inkrementerede `_consecutiveCreateFailures` og kastede en rå
   `TimeoutException` i stedet for `OperationCanceledException`. Konsekvens: den ydre
   `AcquireAsync`-oversættelse til `PoolAcquireTimeoutException` (ADR-0013 fix #1) ramtes aldrig,
   `DataverseGroupPool`s nye specifikke `catch (PoolAcquireTimeoutException)` (ADR-0013 fix #4)
   ramtes derfor heller aldrig, og en ren kapacitets-/annulleringshændelse endte i det generelle
   `catch`, der rapporterede den som et **fejlet health-probe** — præcis det ADR-0013 #4 skulle
   forhindre. Samme mønster fandtes i `RecycleInPlaceAsync`s blanket `catch (Exception)`, der også
   slugte annullering som "recycle fejlede" (dekrementerede `_createdCount`, inkrementerede
   `_consecutiveCreateFailures`).
2. **Probe-claim-race stadig reelt åben, når `AcquireTimeout` er `null` (blokerende,
   distributed-systems-ekspert):** ADR-0013s "ende-til-ende `AcquireTimeout`" gør kun
   `probeClaimTimeout`-racen fra ADR-0011 mindre sandsynlig når `AcquireTimeout` rent faktisk er
   konfigureret og er kortere end `probeClaimTimeout`. Da `AcquireTimeout` er `null` (deaktiveret)
   som default, er den oprindelige race **fuldstændig urørt** i default-konfigurationen: en
   legitimt langsom (ikke hængende) caller A kan miste sit claim til `probeClaimTimeout`, mens en ny
   caller B vinder et overlappende probe.
3. **`AbandonProbe`/`CompleteProbe` uden per-forsøgs-identitet (blokerende/ikke-blokerende, begge
   eksperter):** disse metoder muterede "hvad end der aktuelt er claimet" uden at vide *hvilket*
   forsøg de egentlig afslutter. Et sent/forældet kald fra et allerede overhalet forsøg A kunne
   derfor fejlagtigt frigive eller lukke et nyere, stadig aktivt claim tilhørende B — og dermed
   tillade en tredje caller C at vinde endnu et overlappende probe, eller fejlagtigt lukke/genåbne
   circuit'en baseret på et forældet forsøg.
4. **`WarmupAsync` stadig ikke race-fri under samtidighed (blokerende, DB-pool-eksperten):**
   ADR-0013s før/efter-tjek af `_createdCount` gjorde check-og-opret-sekvensen idempotent for
   *sekventielle* gentagne kald, men ikke atomisk på tværs af *samtidige* `WarmupAsync`-kald: to
   overlappende kald kan begge observere `CreatedCount` under target (idle-ressourcer holder ikke en
   kapacitets-permit, så `_capacityGate` alene forhindrer ikke dette), og begge oprette — hvilket
   samlet kan overskride `MaxSize` med nok samtidige kaldere.

Brugeren var ikke tilgængelig for prioritering af denne runde (autopilot). Fund #1 og #4 er
veldefinerede, lavrisiko bugs med en klar, korrekt fix — de rettes. Fund #3 (claim-korrelation) er
tractable at rette delvist (en generation/token pr. claim, uden at ændre selve
udvælgelsesalgoritmen) og retter samtidig den konkrete stale-report-korruption begge eksperter
demonstrerede — den rettes. Fund #2 (den fundamentale, tidsbaserede race når intet reelt
"claim er stadig i live"-signal findes) er en arkitektonisk afvejning, ikke en simpel bug: en fuld
fix ville enten kræve at aldrig tildele et nyt claim før det gamles udfald er *kendt* (risiko:
et permanent hængende/crashed forsøg blokerer recovery for evigt — præcis derfor
`probeClaimTimeout`-fallbacken findes), eller reel cross-attempt-annullering af det gamle forsøg
(urealistisk mod en non-kooperativ Dataverse SDK). Dette dokumenteres som en kendt, **ikke rettet**
begrænsning, der kræver et bevidst valg fra brugeren næste gang de er tilgængelige — ikke noget der
skal besluttes ensidigt under autopilot.

Under implementeringen af fund #3 blev også opdaget (men **ikke rettet** i denne omgang) at
`HealthAwareRoundRobinSlotSelectionStrategy`/`LeastConnectionsSlotSelectionStrategy`s
udvælgelsesløkke kalder `MemberCircuitBreaker.IsEligible` for **alle** kandidater under
eligibility-filtreringen — ikke kun den der til sidst vælges via round-robin/least-connections. Da
`IsEligible` klaimer et half-open-probe med det samme det finder et ledigt et, betyder det at et
half-open-medlem, der ender med *ikke* at blive valgt denne runde, stadig får sit ene probe-slot
"brugt op" indtil `probeClaimTimeout` udløber — uden at noget forsøg nogensinde reelt sker mod det.
Dette er en reel, tidligere ikke-rapporteret bug, der kan forsinke reel recovery under samtidig
belastning. En fix kræver at adskille "er denne kandidat principielt valgbar" (læse-only) fra
"claim probet" (only for den faktisk valgte kandidat) — hvilket igen kræver håndtering af, hvad der
sker hvis claimet fejler *efter* valget er truffet (kandidaten var ledig ved peek, men blev
claimet af en anden tråd i mellemtiden). Det er en reel, men ikke-triviel algoritmeændring, der
risikerer at introducere nye samtidigheds-bugs uden grundig separat test-dækning og
design-overvejelse — udskudt til en fremtidig runde/ADR, dokumenteret her så det ikke går tabt.

## Beslutning

### Fix for #1: Skeln cancellation fra reelt `CreateTimeout`-udløb
`CreateThroughGateAsync`: efter `Task.WhenAny` returnerer delay-opgaven (ikke `createTask`), tjekkes
`cancellationToken.IsCancellationRequested` *før* det klassificeres som et `CreateTimeout`. Hvis
sandt, kastes `OperationCanceledException` (uden at inkrementere `_consecutiveCreateFailures`); kun
hvis token'et ikke er annulleret er det et reelt `CreateTimeout`-udløb (uændret adfærd: inkrementer
tælleren, kast `TimeoutException`). Tilføjet `catch (OperationCanceledException) { throw; }` (før
det generelle `catch`) så cancellation aldrig rammer den generelle fejl-tæller-forøgelse.

`RecycleInPlaceAsync`: tilføjet en dedikeret `catch (OperationCanceledException)`, der rethrower
uden at markere recyclen som fejlet (ingen `_consecutiveCreateFailures`-forøgelse, intet
`RecoveryFailed`-healthevent) — men *decrementerer* stadig `_createdCount`, fordi den gamle
ressource allerede blev disposed før forsøget på at genoprette, så slottets kapacitet reelt er væk
uanset årsagen til at erstatningen ikke blev færdig.

Begge steder betyder dette at en `AcquireTimeout`-deadline (eller callerens eget token), der
udløber mens en oprettelse er i gang — hvad enten det er en frisk oprettelse eller en inline
idle-lifetime-recycle — nu korrekt propagerer som `OperationCanceledException`, som
`AcquireAsync`s ydre `catch` (ADR-0013) omsætter til `PoolAcquireTimeoutException`, som
`DataverseGroupPool`s specifikke `catch` (ADR-0013 #4) derefter korrekt håndterer via
`ReportAcquireAbandoned` i stedet for at rapportere et fejlet probe.

### Fix for #3: Claim-generation-korrelation i `MemberCircuitBreaker`
`CircuitState` fik et `ClaimGeneration`-felt, inkrementeret hver gang et nyt half-open-probe
claimes. Ny overload `IsEligible(member, stats, now, out long? claimGeneration)` — den oprindelige
3-arguments-metode kalder denne og kasserer generationen (100% bagudkompatibel, alle eksisterende
kald/tests uændrede). `claimGeneration` er kun non-null når kaldet rent faktisk vinder et
half-open-probe (ikke når circuit'en blot er lukket, eller kaldet afvises).

`CompleteProbe`/`AbandonProbe` fik tilsvarende nye overloads med en valgfri `long? claimGeneration`
-parameter. Hvis en ikke-null generation gives, og den ikke matcher medlemmets aktuelle
`ClaimGeneration`, ignoreres rapporten stille — den er fra et allerede overhalet forsøg og må ikke
røre et nyere, stadig aktivt claim. `null` (de oprindelige 2-arguments-overloads) bevarer den gamle,
ukorrelerede adfærd uændret.

Denne generation føres igennem hele kæden: `SlotSelection` fik et nyt, valgfrit
`ProbeClaimGeneration`-felt (default `null`, så eksisterende `new SlotSelection(member, bool)`-kald
i tests forbliver uændrede); `ISlotSelectionStrategy.ReportAcquireOutcome`/`ReportAcquireAbandoned`
fik tilsvarende nye default-overloads der modtager generationen og videresender den til
`MemberCircuitBreaker`; begge circuit-breaker-bevidste strategier fanger generationen fra
`IsEligible` for den faktisk valgte kandidat og lægger den i `SlotSelection`;
`DataverseGroupPool.AcquireAsync` sender `selection.ProbeClaimGeneration` med i alle
`ReportAcquireOutcome`/`ReportAcquireAbandoned`-kald.

Dette retter den konkrete, af begge eksperter demonstrerede korruption: probe A claimes og
abandones, probe B vinder et nyt claim, og A's forsinkede/dobbelte completion-rapport rammer nu
korrekt intet (ignoreres), fordi dens generation ikke længere matcher B's.

### Fix for #4: `WarmupAsync` samtidigheds-sikker via dedikeret warmup-gate
Ny `SemaphoreSlim _warmupGate = new(1, 1)`, der serialiserer hele
tjek-target/tag-permit/opret-sekvensen på tværs af samtidige `WarmupAsync`-kald. Kun én
`WarmupAsync`-kalder kan være inde i beslutningen ad gangen; det interne dobbelttjek af
`_createdCount` (før og efter `_capacityGate`) bevares som forsvar-i-dybden mod samtidige lazy
`AcquireAsync`-oprettelser (som ikke går gennem `_warmupGate`, og ikke behøver at gøre det — de
sigter ikke efter `PrewarmCount`-target'et). Serialiseringen tilføjer ingen reel ekstra
lock-contention udover hvad `_creationGate` allerede pålægger selve oprettelsen (docs/adr/0002).

### Ikke rettet (bevidst, kræver brugerens stillingtagen): #2, det fundamentale probe-race
`PoolOptions.AcquireTimeout`/`MemberCircuitBreaker`-dokumentationen udvides med en eksplicit,
ærlig advarsel: single-probe-garantien er **kun** race-fri i praksis når `AcquireTimeout` er
konfigureret til en værdi kortere end `probeClaimTimeout` for enhver realistisk oprettelsestid —
med default (`AcquireTimeout = null`), er den oprindelige, tidsbaserede race fra ADR-0011 stadig
reelt mulig (en legitimt langsom, ikke-hængende, prober kan miste sit claim til
`probeClaimTimeout` mens den stadig er aktiv). En fuld fix kræver et arkitektonisk valg mellem:
- **Streng eneste-i-luften-semantik**: aldrig tildel et nyt claim før det forrige claims udfald er
  eksplicit kendt (`CompleteProbe`/`AbandonProbe` kaldt) — risiko: et permanent hængende/crashed
  forsøg (ingen `CreateTimeout` konfigureret, `CreateAsync` respekterer ikke cancellation) blokerer
  recovery af det medlem for evigt.
- **Bevare timeout-fallback'en** (nuværende model) — risiko: sjælden dobbelt-probe-overlap under
  uheldig timing, som denne runde har vist er reelt muligt, om end sjældent i praksis (kræver at en
  reel oprettelse tager længere end `probeClaimTimeout`, som selv falder tilbage til
  `cooldownPeriod` som default).

Dette er samme kategori valg som de cross-proces-/cross-medlem-afvejninger, der allerede er
dokumenteret og bevidst udskudt i ADR-0010/0012 — det kræver en produktbeslutning om
tilgængelighed-vs-korrekthed, ikke en kode-fix, og afventer brugerens input.

### Kendt, udskudt fund (ikke rettet): probe-claims "brugt op" af ikke-valgte kandidater
Se Kontekst-afsnittet ovenfor. Dokumenteret her som et konkret, scope'et backlog-punkt til en
fremtidig ADR: adskil "er kandidat principielt valgbar" (read-only) fra "claim probet" (kun for den
faktisk valgte kandidat), og definér korrekt fallback-adfærd hvis claimet fejler efter valget.

## Konsekvenser
- **Bagudkompatibelt**: alle nye parametre er valgfrie/default `null`; eksisterende kald til
  `IsEligible`/`CompleteProbe`/`AbandonProbe`/`SlotSelection`-konstruktion samt alle eksisterende
  `ISlotSelectionStrategy`-implementeringer (inkl. test-doubles) fortsætter uændret.
- **Skarpere fejlklassificering**: en `AcquireTimeout`- eller caller-cancellation, der rammer midt i
  en igangværende oprettelse (frisk eller inline recycle), tæller ikke længere fejlagtigt som en
  create-fejl eller et fejlet health-probe.
- **Stale probe-rapporter kan ikke længere korrumpere et nyere claim** på samme medlem.
- **`WarmupAsync` er nu korrekt atomisk på tværs af samtidige kald**, ikke kun idempotent for
  sekventielle gentagne kald.
- **To fund er bevidst ikke rettet** og er dokumenteret ovenfor med begrundelse: den fundamentale
  probe-race når `AcquireTimeout` er `null` (kræver brugerens produktbeslutning), og
  "probe-claims brugt op af ikke-valgte kandidater" (kræver en større, isoleret redesign-indsats).
- Test-dækning: **90/90 grønne** (Core 40 [+4], Dataverse 47 [+4], Polly 3), op fra 82/82. Kørt 3x i
  træk uden flaky timing-fejl. Nye tests: `CreateTimeout`/`AcquireTimeout`-forveksling via både
  frisk oprettelse og inline recycle (2 tests), samtidig `WarmupAsync`-race (2 tests),
  claim-generation-adfærd — kun non-null ved reelt vundet claim, stale-completion/-abandon ignoreres
  korrekt, nyere claim forbliver intakt, bagudkompatibel null-adfærd (5 tests).
