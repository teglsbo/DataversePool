# ADR-0007: Race conditions, timeouts og fejlscenarier — identificerede huller og rettelser

## Status
Accepteret (retter bugs i eksisterende ConnectionPool.Core/Dataverse-implementering)

## Kontekst
Ved en systematisk gennemgang af race conditions, timing og fejlscenarier i den eksisterende
implementering blev følgende identificeret:

### 1. `MarkUnhealthy` efter `DisposeAsync` (use-after-dispose race)
`PooledLease<T>.MarkUnhealthy` tjekkede ikke `_disposed`-flaget. Hvis brugerkode (fejlagtigt) kalder
`MarkUnhealthy` fra en baggrundstråd efter lease'en allerede er disposed, ville den mutere en slot der
enten allerede er tilbage i idle-stakken eller udleveret til en *anden* leaser — datakorruption.

**Rettelse:** `MarkUnhealthy` tjekker `_disposed` og no-op'er hvis lease allerede er disposed.
Dette er stadig ikke 100% race-fri (TOCTOU mellem tjek og mutation), men reducerer vinduet
betydeligt. Fuld sikkerhed kræver at brugerkoden overholder isolations-kontrakten fra ADR-0003
(kald aldrig `MarkUnhealthy` samtidig med/efter `DisposeAsync` fra en anden tråd).

### 2. Pool-shutdown mens leases stadig er "in flight"
`ResourcePool<T>.DisposeAsync` tømte kun `_idle`-stakken. Den forhindrede hverken nye
`AcquireAsync`-kald, ej heller håndterede den leases der returneres *efter* at poolen er disposed —
disse blev bare pushet tilbage på `_idle` og aldrig drænet igen. Reelt scenarie: applikations-shutdown
mens requests er in-flight → lækkede `ServiceClient`-forbindelser/håndtag.

**Rettelse:**
- `AcquireAsync` kaster `ObjectDisposedException` hvis poolen er disposed.
- `DisposeAsync` forsøger et best-effort drain (venter kortvarigt, pr. permit, på at udestående
  leases returneres) før den tømmer `_idle`.
- `ReturnAsync`/baggrunds-recycle tjekker `_poolDisposed` og bortskaffer ressourcen direkte i stedet
  for at genindsætte den i `_idle`, hvis en lease returneres efter shutdown er påbegyndt.

### 3. `PoolStats.UnhealthyOrRecyclingCount` var hardcodet til 0
Gjorde det umuligt for en fremtidig throttle/health-aware selection-strategi (eller telemetri) at se
hvor mange slots der reelt er i gang med at blive genoprettet.

**Rettelse:** Tælles nu korrekt med `Interlocked`-tællere, inkrementeret når en slot markeres
unhealthy/leaked, dekrementeret når recycling afsluttes (success eller endeligt opgivet).

### 4. Hængende/meget langsom `CreateAsync` blokerer hele poolens creation-gate på ubestemt tid
Den serielle creation-gate (ADR-0002) er nødvendig for at undgå lock-contention ved parallel cloning,
men har en skyggeside: hvis ét kald til `policy.CreateAsync` hænger (netværkspartition, DNS-timeout,
et SDK-kald der aldrig returnerer), er *hele* poolens evne til at oprette nye ressourcer frosset på
ubestemt tid — også for andre brugere/tråde der bare venter på en hvilken som helst ledig ressource
og ikke selv rammer creation-stien.

**Rettelse:** Ny valgfri `PoolOptions.CreateTimeout`. Oprettelse wrappes i en `Task.WhenAny` mod en
timer. Ved timeout:
- Creation-gaten frigives med det samme (poolen kan fortsætte), og et `TimeoutException` kastes til
  den kaldende `AcquireAsync`.
- Det oprindelige, nu "forladte" `CreateAsync`-kald må stadig afsluttes i baggrunden (kan ikke
  cooperativt afbrydes) — resultatet bortskaffes automatisk når/hvis det til sidst returnerer.
- **Bevidst tradeoff:** dette kan i sjældne tilfælde (kun ved faktisk timeout) tillade to reelle
  clone-forsøg at overlappe i tid — det er en accepteret afvigelse fra den strikte ADR-0002-garanti,
  fordi alternativet (permanent fastlåst pool) er værre.

### 5. Ingen max-levetid for idle ressourcer (svarer til ADO.NET's "Connection Lifetime")
En ressource der har ligget idle længe kan have mistet sin forbindelse stille (token udløbet uden at
`IsReady` nødvendigvis opdager det proaktivt) — den udleveres først som "sund" og fejler så ved
faktisk brug. Dette opdages i dag kun reaktivt (`MarkUnhealthy` fra brugerkoden), hvilket er en
accepteret v1-adfærd (ADR-0004), men kan forbedres proaktivt.

**Rettelse (delvis, opfølgning):** `PoolOptions.MaxIdleLifetime` (valgfri) — hvis sat, tjekkes en
idle slots alder ved checkout, og den recycles inline (samme kodevej som en usund slot) hvis den har
ligget idle længere end grænsen. Ikke en baggrundstimer i v1 (undgår kompleksitet med en separat
sweep-tråd) — tjekkes lazy ved næste `AcquireAsync`.

### 6. "Én bruger i en gruppe er død" — round-robin sender fortsat 1/N trafik til en permanent fejlende bruger
Hvis ét medlem i en `DataverseGroupPool` er permanent utilgængeligt (spærret app-bruger, forkert
secret, spærret IP), vil ren round-robin blive ved med at sende hver N'te request derhen, hvor de
enten hænger (indtil `CreateTimeout`) eller fejler gentagne gange — dårlig hale-latency og spildt
kapacitet, uden at de andre medlemmer kompenserer.

**Rettelse:** Ny `PoolStats.ConsecutiveCreateFailures`-tæller pr. medlemspool. En ny
`HealthAwareRoundRobinSlotSelectionStrategy` springer medlemmer over hvor denne tæller overstiger en
konfigurerbar grænse ("circuit open"), med en cooldown-baseret "half-open" gen-forsøgslogik (efter X
tid prøves medlemmet igen for én request; lykkes den, nulstilles tælleren). Hvis *alle* medlemmer er
circuit-open samtidig (fx forbigående total netværksfejl), vælges der alligevel ét medlem
round-robin (fail open) frem for at kaste en exception uden fallback — bevidst valg for at undgå at
gruppen "låser sig selv ude" permanent ved en kortvarig fælles fejl.

## Konsekvenser
- Core og Dataverse-adapteren er nu robuste mod de vigtigste identificerede race conditions og
  hænge-scenarier, verificeret med nye tests (se `TimeoutAndShutdownTests`,
  `HealthAwareRoundRobinSlotSelectionStrategyTests`).
- `CreateTimeout` og `MaxIdleLifetime` er opt-in (default: ingen timeout/levetidsgrænse) for ikke at
  ændre default-adfærd for eksisterende brugere af biblioteket.
- Fortsat åbent: fuld beskyttelse mod `MarkUnhealthy`-race kræver brugerdisciplin (ADR-0003); en
  baggrunds-sweep for idle max-lifetime (i stedet for kun lazy-ved-checkout) er udskudt til v2 hvis
  behovet opstår.
