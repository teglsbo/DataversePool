# ADR-0001: Generisk pool-kerne afkoblet fra Dataverse via `IPooledResourcePolicy<T>`

## Status
Accepteret

## Kontekst
Vi skal bygge connection pooling til Dataverse `ServiceClient`, men ønsker ikke at låse
hele designet til Dataverse-specifik viden (tokens, discovery, `CallerId`). Andre
ressourcetyper (fx andre SDK-klienter) kan få gavn af samme pool-motor.

## Beslutning
`ConnectionPool.Core` kender kun den generiske type `T` og et policy-interface:

```csharp
public interface IPooledResourcePolicy<T>
{
    Task<T> CreateAsync(CancellationToken ct);
    bool IsHealthy(T resource, PoolIncidentInfo? lastIncident);
    ValueTask DisposeResourceAsync(T resource);
}
```

Navngivningen er bevidst lånt fra `Microsoft.Extensions.ObjectPool.PooledObjectPolicy<T>`
for genkendelighed. "Policy" fremfor "Factory", fordi kontrakten dækker hele
ressourcens livscyklus (opret, sundhedsvurdering, bortskaffelse) — ikke kun oprettelse.

`ConnectionPool.Dataverse` implementerer `DataverseServiceClientPolicy : IPooledResourcePolicy<ServiceClient>`
og er det eneste sted i løsningen der kender Dataverse-specifikke typer.

## Konsekvenser
- Core kan enheds-testes 100% med fakes, uden nogensinde at oprette en `ServiceClient`.
- Fremtidige ressourcetyper (andre SDK'er) kan genbruge `ConnectionPool.Core` uden ændringer.
- Al domænelogik (hvornår er en Dataverse-forbindelse "syg") ligger i adapteren, ikke kernen.
