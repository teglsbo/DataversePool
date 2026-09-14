# ADR-0010: Konfigurerbar fail-fast (i stedet for kun fail-open) + reelt single-probe half-open

## Status
Accepteret

## Kontekst
En distributed-systems-fokuseret review identificerede tre relaterede problemer i
gruppe-selection-strategierne (`HealthAwareRoundRobinSlotSelectionStrategy`,
`LeastConnectionsSlotSelectionStrategy`):

1. **Manglende delt budget-koordinering på tværs af processer.** Se ADR-0009 — dette forbliver en
   dokumenteret produktionsbegrænsning ("single process per service-principal set"), IKKE løst her.
   Brugeren har eksplicit afvist at indføre delt state mellem processer (ingen ekstern
   koordinator/Redis/lignende) — enhver løsning her skal derfor forblive strengt per-proces.
2. **Unconditional fail-open kan forstærke en reel outage.** Når alle gruppens medlemmer er
   circuit-open/throttlede, valgte strategierne hidtil altid et medlem alligevel (ADR-0007 #6, for
   at undgå deadlock). Under en tenant-wide degradering betyder det, at gruppen bliver ved med at
   sende trafik til noget der med sikkerhed vil fejle/være throttlet, i stedet for at give
   backpressure.
3. **Half-open var ikke atomisk.** Når et medlems cooldown udløb, kunne *alle* samtidige
   `SelectNext`-kald i samme øjeblik betragte medlemmet som "eligible" — dvs. et helt bundt
   samtidige callers kunne stime mod præcis den forbindelse der lige er ved at komme sig, hvilket i
   praksis er en lokal thundering-herd på det svageste led.

## Beslutning

### Fix for #3: `MemberCircuitBreaker` (ny, delt type, stadig kun per-proces)
Circuit-breaking-logikken (der før var duplikeret identisk i begge strategier) er udtrukket til en
ny offentlig type, `MemberCircuitBreaker`, som:
- Bevarer closed/open/half-open-semantikken fra ADR-0007 #6 (åbner ved
  `ConsecutiveCreateFailures >= failureThreshold`, retries efter `cooldownPeriod`).
- Tilføjer et **atomisk single-probe-lag**: når et medlem bliver half-open, er det kun den *første*
  caller (inden for et `probeClaimTimeout`-vindue, default = `cooldownPeriod`) der får lov at
  betragte det som eligible. Alle andre samtidige callers forbliver rettet mod andre medlemmer,
  indtil probens udfald er synligt (medlemmets `ConsecutiveCreateFailures` falder under tærsklen —
  success — eller selve probe-claimet udløber, hvorefter en ny probe kan tildeles).
- Er stadig **rent per-proces, ingen delt state** — `Dictionary`+`lock` i hukommelsen, præcis som
  før. Dette er en bevidst grænse: at gøre selve probe-atomiciteten korrekt *inden for* én proces
  kræver ingen ekstern koordinator, og brugeren har eksplicit afvist at indføre en sådan for dette
  arbejde.
- Testet: `MemberCircuitBreakerTests` inkluderer en konkurrence-test med 50 samtidige kald via
  `Parallel.For`, der verificerer præcis 1 vinder af probe-slottet (ikke 0, ikke >1).

Begge strategier bruger nu denne fælles type i stedet for hver sin private `Dictionary`-baserede
bogføring — fjerner duplikeret, tricky concurrency-kode og retter buggen i samme ombæring.

### Fix for #2: `GroupAllUnavailableBehavior` (konfigurerbar, default uændret)
`DataverseGroupPool` får en ny konstruktør-parameter,
`GroupAllUnavailableBehavior allUnavailableBehavior = GroupAllUnavailableBehavior.FailOpen`:
- **`FailOpen`** (default, bagudkompatibel): uændret opførsel — vælg et medlem alligevel, selv når
  alle er circuit-open/throttlede. Fornuftigt når et resiliens-lag længere oppe (Polly, egen retry)
  alligevel håndterer fejlen/throttlingen, og man hellere vil forsøge end afvise.
- **`FailFast`**: kast `DataverseGroupUnavailableException` (med medlemsnavne og — hvis kendt —
  det tidligste throttle-vindue på tværs af medlemmer) **før** noget `AcquireAsync`-kald overhovedet
  forsøges mod et medlem, man allerede ved er utilgængeligt. Fornuftigt når man ikke vil tilføje
  belastning til (eller forlænge) en outage man allerede kender til, og hellere vil have
  backpressure med det samme.

For at gøre dette muligt uden at ændre `ISlotSelectionStrategy`s eksisterende
returtype-signatur til noget nullable/kastende, blev interfacet i stedet udvidet med et
`readonly record struct SlotSelection(DataverseUserPool Member, bool AllMembersUnavailable)` — en
lille, additiv API-ændring (breaking i teknisk forstand, men accepteret jf. pre-1.0-status, samme
begrundelse som `DataverseGroupLease` i ADR-0008). `RoundRobinSlotSelectionStrategy` (ingen
health-awareness) rapporterer altid `AllMembersUnavailable: false`.

### #1 forbliver bevidst uløst her
Ingen kode i denne ADR forsøger at koordinere budget/state på tværs af processer — det er stadig
dokumenteret som produktionsbegrænsning i README (ADR-0009). At løse #1 "rigtigt" (globalt korrekt
budget-håndhævelse) kræver per definition delt state et sted (en ekstern koordinator, database,
distribueret lock, etc.), hvilket brugeren eksplicit har fravalgt for dette arbejde. Den eneste
indirekte forbedring #1 får her er, at `FailFast` + `DataverseGroupUnavailableException` gør det
markant nemmere for en operatør at *observere* at gruppen er presset (i stedet for stiltiende at
blive ved med at sende trafik), hvilket er et skridt i retning af bedre drift uden at kræve delt
state.

## Konsekvenser
- **Bagudkompatibelt**: default-opførslen (`FailOpen`) er identisk med før denne ADR. Eksisterende
  forbrugere af `DataverseGroupPool`/strategierne påvirkes ikke, medmindre de selv opgraderer til
  `FailFast`.
- **`ISlotSelectionStrategy.SelectNext`s returtype ændret** fra `DataverseUserPool` til
  `SlotSelection` — påvirker enhver brugerdefineret strategi-implementering (ingen kendte findes i
  dette repo udover de tre indbyggede + tests, alle opdateret).
- `MemberCircuitBreaker` er offentlig, så en brugerdefineret strategi kan genbruge den fremfor at
  genimplementere half-open-logik (og det tilhørende single-probe-fix) selv.
- Test-dækning: 52/52 grønne efter denne ændring (Core 15, Dataverse 34, Polly 3), inkl. 4 nye
  `MemberCircuitBreakerTests` og 3 nye `DataverseGroupPoolTests` (default fail-open, fail-fast,
  fail-fast med korrekt "earliest retry"-beregning fra throttle-vinduer).
