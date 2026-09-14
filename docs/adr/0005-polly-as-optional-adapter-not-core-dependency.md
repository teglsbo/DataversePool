# ADR-0005: Polly som separat, valgfri adapter-pakke — ikke en kerneafhængighed

## Status
Accepteret (del af MVP-scope)

## Kontekst
Vi har behov for retry/circuit-breaker omkring operationer på leasede klienter, og et
signal til poolen når forbindelser er "syge". Polly er det oplagte valg for
resilience-logik i moderne .NET, men bør ikke tvinges på alle forbrugere af
`ConnectionPool.Core`/`ConnectionPool.Dataverse`.

## Beslutning
- `ConnectionPool.Core` og `ConnectionPool.Dataverse` har **ingen** Polly-afhængighed.
  De eksponerer kun de generelle hooks: `lease.MarkUnhealthy(Exception)` og
  `pool.HealthChanges` (`IObservable<SlotHealthChanged>`).
- Et separat projekt, `ConnectionPool.Dataverse.Polly`, tilbyder en extension-metode
  der wire'r Polly's `OnRetry`/`OnOpened`-callbacks til `lease.MarkUnhealthy(...)`:

  ```csharp
  pipeline = new ResiliencePipelineBuilder()
      .AddRetry(...)
      .WithPoolHealthSignal(lease)   // <- vores extension
      .Build();
  ```

- Inkluderet i MVP (ikke udskudt til v2), da det dækker 90% af det forventede
  brugsmønster med minimal kode og holder kernen ren.

## Konsekvenser
- Forbrugere der ikke bruger Polly pådrager sig ingen ekstra NuGet-afhængighed.
- Forbrugere der bruger Polly får en ét-linje integration frem for at skulle
  selv opdage og implementere `MarkUnhealthy`-kaldet korrekt.
- Fremtidig udvidelse (pool → Polly retning, dvs. proaktiv circuit-opening baseret på
  `pool.HealthChanges` ved systemisk fejl) er en additiv ændring i samme projekt,
  ikke et brud på kontrakten.
