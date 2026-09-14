# ADR-0009: Return-scrubbing hook (CallerId-lækage) + dokumenteret single-process-begrænsning

## Status
Accepteret

## Kontekst
En sikkerheds-review og en distributed-systems-review blev kørt mod hele kodebasen forud for en
produktionsklarheds-vurdering. Findings:

### 1. Sikkerhedsfund (rettet i denne ADR): CallerId-lækage mellem leases (HIGH, 8/10)
`ResourcePool<T>.ReturnAsync` puttede en sund ressource tilbage på idle-stakken **uændret**. For
Dataverse betyder det: hvis en forbruger sætter `ServiceClient.CallerId` (Dataverse's indbyggede
"act as another user"-impersonationsfelt, en public settable `Guid` — bekræftet ved refleksion) for
at udføre et kald på vegne af en slutbruger, og disponerer sin lease uden selv at nulstille
`CallerId`, ville **den samme `ServiceClient`-instans** — stadig impersonerende den forrige bruger
— blive udleveret til næste, urelaterede caller via `AcquireAsync`. Det er præcis den type
cross-caller identitetslækage denne pool (som eksplicit skal understøtte flere service-brugere) må
undgå.

Dette er en distinkt og hidtil ikke-adresseret risiko fra ADR-0003, som kun diskuterer *samtidig*
deling af én lease på tværs af tråde i dens levetid — ikke *sekventiel* genbrug efter
`DisposeAsync`.

### 2. Distributed-systems-fund (dokumenteret, IKKE løst i denne ADR — se "Konsekvenser")
En separat gennemgang fokuseret på multi-instans/Kubernetes-scenarier identificerede fem
blokerende problemer, hvis fælles rod er: **al pool-, throttle- og circuit-state er kun i hukommelse,
per-proces**. Med N instanser der deler de samme Dataverse service-principals:

1. Intet delt budget på tværs af processer — N instanser × MaxSize kan langt overstige det reelle
   per-app-bruger-loft, og en 429 set af instans A stopper ikke instans B i at bruge samme budget.
2. "Fail open, hvis alle medlemmer er throttlede/circuit-open" (bevidst valgt i ADR-0007/0008 for at
   undgå deadlock) kan forstærke en reel tenant-wide-outage i stedet for at give backpressure.
3. Circuit breaker'en er ikke en reel single-probe half-open — flere samtidige callers kan alle
   ramme det "recovering" medlem på én gang.
4. Circuit'en tracker kun *oprettelsesfejl* (clone-fejl), ikke operationelle fejl — en tenant der
   degraderer (langsomme kald, 5xx) uden at clone fejler vil ikke åbne circuit'en.
5. Ingen bounded acquire-kø/deadline — under vedvarende overload kan ventende callers vokse
   ubegrænset.

## Beslutning

**Sikkerhedsfund #1 er rettet nu:**
- `IPooledResourcePolicy<T>` fik en ny, valgfri (default-no-op via C# default interface-metode)
  metode: `void OnReturned(T resource)`, kaldt af `ResourcePool<T>.ReturnAsync` lige før en sund
  ressource lægges tilbage på idle-stakken. Dette er domæne-agnostisk i Core (ADR-0001) — Core ved
  stadig ikke *hvad* der scrubbes, kun at politikken får en chance for det ved retur.
- `DataverseServiceClientPolicy.OnReturned` implementerer den ved at sætte
  `resource.CallerId = Guid.Empty`.
- Testet: `ResourcePoolLifecycleTests.DisposeAsync_InvokesPolicyOnReturned_BeforeResourceIsReIdled`
  verificerer selve hook-forbindelsen (via `FakePolicy`); `CallerId`-nulstillingen i sig selv er
  ikke enhedstestet mod en ægte `ServiceClient` (kræver en levende Dataverse-forbindelse — samme
  begrænsning som resten af `DataverseServiceClientPolicy`), men er verificeret ved refleksion at
  `CallerId` er en public settable `Guid` på den faktiske SDK-type.

**Distributed-systems-fund #1-5 er bevidst IKKE løst i denne ADR.** At bygge ægte
multi-proces-koordinering (delt budget, distribueret half-open circuit, bounded backpressure på
tværs af instanser) er en stor arkitektonisk beslutning (kræver formentlig en ekstern koordinator —
Redis/Dataverse selv/anden delt store — og ændrer den nuværende "ingen eksterne
afhængigheder"-egenskab ved Core). Det besluttes ikke stiltiende som en implementeringsdetalje.

I stedet dokumenteres det eksplicit som en **produktionsbegrænsning**:

> **DataversePool understøtter i dag kun korrekt drift når præcis én proces-instans ejer et givent
> sæt Dataverse service-principals ad gangen.** Kør IKKE flere instanser/pods af den forbrugende
> applikation mod det samme `DataverseGroupPool`-medlemssæt, medmindre du selv accepterer at
> throttle-/circuit-state ikke er koordineret på tværs af dem (dvs. reelt N× det tilsigtede
> service-protection-budget, og ingen fælles backpressure ved en tenant-wide outage).

## Konsekvenser
- **Sikkerhedsfund #1**: lukket. `OnReturned`-hooket er generisk og kan genbruges til andre
  fremtidige per-lease-scrub-behov uden endnu en breaking change.
- **Distributed-systems-fund #1-5**: forbliver åbne, men er nu eksplicit dokumenteret (README +
  TODO.md) i stedet for et stiltiende hul. Prioriteret backlog for en evt. v2 "coordinated mode":
  1. Ekstern delt throttle/circuit-state (budget-bulkhead pr. tenant+app-bruger).
  2. Erstat unconditional fail-open med en konfigurerbar fail-fast/deadline-politik.
  3. Reelt single-probe half-open (atomisk "probe-in-flight" pr. medlem).
  4. Skeln oprettelsesfejl fra operationelle fejl i circuit-signalet.
  5. Bounded acquire-kø/deadline (backpressure) i `ResourcePool<T>.AcquireAsync`.
- Ingen af disse kræver et brud på den eksisterende single-process API — de er additive.
