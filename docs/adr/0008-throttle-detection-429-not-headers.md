# ADR-0008: Throttle-detektion via HTTP 429/exception, ikke proaktive rate-limit-headere

## Status
Accepteret

## Kontekst
Spørgsmål: skal gruppe-poolens throttle-awareness (dvs. undgå at sende trafik til et medlem der er
tæt på sit Dataverse service-protection-loft) baseres på (a) Dataverse's proaktive
`x-ms-ratelimit-*` response-headere, eller (b) det reaktive HTTP 429-svar (`Retry-After`)?

Generelt, for Dataverse's Web API i sig selv, er svaret klart **headers er bedre**: Dataverse sender
`x-ms-ratelimit-burst-remaining-xrm-requests`, `x-ms-ratelimit-time-remaining-xrm-requests` (og
tilsvarende for execution-time-budgettet) på **hver eneste** response — succesfuld eller ej. Det er
et *leading* signal (reager før man bliver throttlet), hvor et 429 er et *lagging* signal (reager
først efter Dataverse allerede har afvist et kald).

Men denne pool wrapper ikke Dataverse's rå Web API — den wrapper
`Microsoft.PowerPlatform.Dataverse.Client.ServiceClient`. Vi undersøgte via refleksion om
`ServiceClient` (og dens `ConnectionOptions`) eksponerer disse headere for et *succesfuldt* kald:

- Ingen public property, event eller hook på `ServiceClient` eller `ConnectionOptions` giver adgang
  til response-headers for et succesfuldt kald.
- `ServiceClient` har sin egen interne retry-logik for throttling
  (`MaxRetryCount`/`RetryPauseTime`/`UseExponentialRetryDelayForConcurrencyThrottle`) — den
  absorberer 429'ere internt og retryer selv, før noget overhovedet når brugerkoden.
- **Når** SDK'ens egen retry-budget er opbrugt, kastes en
  `Microsoft.PowerPlatform.Dataverse.Client.Exceptions.HttpOperationException` hvis
  `Response`-property *rent faktisk* eksponerer `StatusCode` (verificeret: `429`) og `Headers`
  (verificeret: `IDictionary<string, IEnumerable<string>>`, inkl. `Retry-After`) — bekræftet ved
  refleksion på den faktiske SDK-DLL, ikke antaget.

Konklusion: for **denne SDK**, er 429/exception-vejen ikke en præference blandt to lige gode
muligheder — det er den eneste faktisk tilgængelige struktur uden at reflektere ind i
`ServiceClient`s private HTTP-pipeline (skrøbeligt, ikke understøttet af Microsoft, og et
vedligeholdelsesmareridt ved SDK-opgraderinger). Det blev derfor bevidst fravalgt.

## Beslutning
- `DataverseThrottleDetector.TryGetRetryAfter(Exception?, out TimeSpan)` går exception-kæden
  igennem, finder en `HttpOperationException` med `Response.StatusCode == 429`, og parser
  `Retry-After`-headeren (sekunder eller HTTP-dato) til en `TimeSpan`. Hvis 429 findes men
  `Retry-After` mangler/ikke kan parses, bruges en konservativ default (5s) — stadig bedre end at
  ignorere signalet.
- `DataverseUserPool` får `ThrottledUntil`/`IsThrottled`/`ReportThrottled(TimeSpan)` — throttling
  markerer **ikke** ressourcen unhealthy/til recycling (forbindelsen er fin, brugeren er bare midlertidigt
  over sit eget budget). Dette er bevidst en separat mekanisme fra `MarkUnhealthy`
  (ADR-0004/0007), som er til reelt defekte forbindelser.
- `DataverseGroupPool.AcquireAsync()` returnerer nu `DataverseGroupLease` (ikke en rå
  `PooledLease<ServiceClient>`) specifikt så en forbruger kan rapportere en 429 tilbage til det
  **rigtige** medlem — poolen ved selv ikke hvilket medlem der blev valgt til et givent kald uden
  denne reference. `DataverseGroupLease.ReportIfThrottled(exception)` er bekvemmeligheds-metoden
  der kombinerer detection + rapportering i ét kald.
- Begge selection-strategier (`HealthAwareRoundRobinSlotSelectionStrategy`,
  `LeastConnectionsSlotSelectionStrategy`) springer nu også throttlede medlemmer over, med samme
  "fail open hvis alle er nede"-garanti som for circuit-open medlemmer (ADR-0007 #6).

## Konsekvenser
- **Kræver eksplicit rapportering fra forbrugeren.** Poolen kan ikke selv opdage throttling — den
  ser ikke hvad man gør med en udleveret `ServiceClient` efter `AcquireAsync()`. Hvis en forbruger
  ikke kalder `ReportIfThrottled`/`ReportThrottled` i sit fejl-håndteringsflow, er throttle-awareness
  en no-op. Dette er dokumenteret som en hård grænse, ikke en fremtidig "todo".
- **Kun 429 der rent faktisk undslipper SDK'ens egen retry, opdages.** De fleste transiente
  throttle-hændelser absorberes allerede internt af `ServiceClient` (det er meningen med dens egen
  retry-logik) — vores signal er reelt kun for vedvarende/gentagne throttling der overlever
  SDK'ens interne retry-budget. Det er stadig værdifuldt (det er præcis situationen hvor gruppens
  round-robin ellers ville blive ved med at hamre det samme medlem), men det er ikke en generel
  telemetri-kilde for hvor tæt et medlem er på sit loft under normal drift.
- **`DataverseGroupPool.AcquireAsync()`s returtype ændret** fra `PooledLease<ServiceClient>` til
  `DataverseGroupLease` — et bevidst API-brud, acceptabelt fordi biblioteket stadig er
  pre-1.0/preview. `DataverseGroupLease` beholder samme brugsmønster (`.Resource`,
  `await using`/`DisposeAsync`), så migration er minimal.
- Hvis en fremtidig SDK-version eksponerer response-headers for succesfulde kald (eller vi skifter
  til at kalde Web API'et direkte i stedet for via `ServiceClient`), kan en proaktiv
  header-baseret strategi tilføjes som et nyt, separat signal uden at ændre den kontrakt der er
  sat op her.
