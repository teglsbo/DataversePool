# ADR-0003: Non-shared lease er en dispose-kontrakt, ikke runtime-håndhævet isolation

## Status
Accepteret

## Kontekst
`ServiceClient` er ikke thread-safe ved deling på tværs af tråde med forskellig
`CallerId`. Vi har ikke selv verificeret denne race condition (0 exceptions i test),
men varierede aldrig `CallerId` samtidigt på tværs af tråde — så risikoen er reel,
men uverificeret af os.

En fuldt runtime-håndhævet isolation (fx per-kald lock-check) ville tilføje
overhead og kompleksitet til hvert kald på den udleverede klient.

## Beslutning
Isolation garanteres **inden for kontrakten**: én lease = eksklusiv ejerskab af
ressourcen indtil `DisposeAsync()` kaldes. Poolen håndhæver ikke at brugeren rent
faktisk undlader at dele referencen videre til andre tråde — det er brugerens ansvar.

Til gengæld:
- Leak-tracking opdager leases der aldrig disposes (via finalizer-warning, jf.
  `LeakTrackingObjectPool`-mønsteret) og evakuerer/genopretter slotten — billigt,
  fordi re-clone fra en varm base er ~1ms (se ADR-0002).
- `MarkUnhealthy(exception)` giver brugeren et billigt "nød-signal" hvis de selv
  opdager at en leased ressource mistede forbindelsen, uden at skulle vente på en
  fuld health-check-cyklus.

## Konsekvenser
- Ingen runtime-overhead pr. kald på den underliggende `ServiceClient`.
- Kontrakten skal være tydeligt dokumenteret i XML-docs/README: "del aldrig en
  lease's Resource mellem tråde, dispose altid, brug MarkUnhealthy ved mistanke om fejl."
- Åben opfølgning: hvis fremtidig test *bekræfter* faktisk cross-thread korruption
  (ikke kun teoretisk risiko), skal vi genoverveje en strengere runtime-check.
