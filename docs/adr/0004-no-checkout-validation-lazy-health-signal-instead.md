# ADR-0004: Ingen synkron sundhedstjek ved checkout — signal-baseret i stedet

## Status
Accepteret

## Kontekst
Klassisk "validate on checkout" (kendt fra visse ADO.NET-providere) kræver en
netværks-roundtrip pr. `AcquireAsync()`-kald for at bekræfte at ressourcen stadig
virker. Det tilføjer latency til *hver* leje, selv i det normale (sunde) tilfælde.

## Beslutning
Poolen validerer **ikke** synkront ved hver checkout. I stedet:
1. Hver slot har en billig in-memory status (`Idle` / `Unhealthy` / `Recycling`),
   opdateret asynkront/event-drevet — ikke ved et netværkskald.
2. Brugeren kalder `lease.MarkUnhealthy(exception)` når de selv observerer en fejl
   på den udleverede klient (fx via Polly-adapteren, se ADR-0005) — poolen evakuerer
   og genopretter slotten i baggrunden, uden at blokere andre acquires.
3. En periodisk baggrundstjek (konfigurerbart interval, fx 1–5 min) kan proaktivt
   opdage token-udløb før brug, uafhængigt af checkout-flowet.

## Konsekvenser
- Normalt-sti (`AcquireAsync` på en sund slot) har ingen ekstra netværks-latency.
- Fejldetektion er reaktiv (afhænger af at brugeren kalder `MarkUnhealthy`) frem for
  proaktivt garanteret ved hver leje — accepteret tradeoff, da Polly-adapteren gør
  dette til ét linje boilerplate for brugeren (se ADR-0005).
- Skal testes: efter `MarkUnhealthy`, verificér at ny `AcquireAsync` ikke returnerer
  samme (nu usunde) ressource, og at genopretning ikke blokerer andre ventende leases.
