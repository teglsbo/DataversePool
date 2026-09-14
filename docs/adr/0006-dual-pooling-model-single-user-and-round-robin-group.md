# ADR-0006: Dobbelt pooling-model — single-user pool og round-robin gruppe-pool

## Status
Accepteret

## Kontekst
Dataverse håndhæver service protection limits pr. bruger/app (~52 samtidige kald).
Nogle forbrugere har kun én service-bruger og ønsker simpel pooling af flere
`ServiceClient`-instanser for den ene bruger. Andre har flere app-brugere og vil
skalere ud over én brugers loft ved at fordele load på tværs (round-robin), med
plads til senere at gøre fordelingen throttle-bevidst (baseret på `x-ms-dop-hint`
og 429/budget-signaler).

## Beslutning
To offentlige typer i `ConnectionPool.Dataverse`:

- `DataverseUserPool` — pool af `ServiceClient`-instanser for **én** bruger/forbindelsesstreng.
- `DataverseGroupPool` — sammensætter flere `DataverseUserPool`-instanser og vælger
  hvilken der bruges via en pluggable strategi:

  ```csharp
  public interface ISlotSelectionStrategy
  {
      DataverseUserPool SelectNext(IReadOnlyList<DataverseUserPool> members, PoolStats[] stats);
  }
  ```

  v1 leverer kun en simpel `RoundRobinSlotSelectionStrategy`. Interfacet er designet
  til senere at kunne modtage en `ThrottleAwareSlotSelectionStrategy` der undgår
  medlemmer tæt på deres budget/dop-hint-loft, uden at ændre public API på
  `DataverseGroupPool`.

## Konsekvenser
- Forbrugere med simpelt behov (én bruger) bruger `DataverseUserPool` direkte, uden
  overhead fra gruppelaget.
- Gruppelogik er isoleret i strategien — round-robin i v1 kan udskiftes uden at
  ændre `DataverseGroupPool`s offentlige kontrakt.
- Kræver at `PoolStats` (in-use/wait-time/unhealthy-count) er tilgængelig pr.
  medlems-pool, så en fremtidig throttle-aware strategi har data at vælge ud fra.

## Opdatering: måling (load) vs. blind round-robin

Efter v1 er standard-strategien blevet `HealthAwareRoundRobinSlotSelectionStrategy` (se ADR-0007
#6), og der er nu også en `LeastConnectionsSlotSelectionStrategy` tilføjet som alternativ — den
vælger medlemmet med færrest aktuelt udlånte leases (`PoolStats.LeasedCount`) frem for blind
tur-baseret rækkefølge, med samme circuit-breaking (dead-member skip/half-open/fail-open) som
health-aware-varianten.

**Hvorfor dette stadig ikke er "throttle-aware":** `LeasedCount` måler kun *hvor mange leases der
er checked out lige nu* — ikke hvor tæt et medlem reelt er på sit ~52-samtidig-loft, og slet ikke
om Dataverse allerede sender 429/`Retry-After` på requests udført gennem en udleveret
`ServiceClient`. Poolen har ingen synlighed i hvad en forbruger gør med en lease efter
`AcquireAsync()` returnerer den, så ægte throttle-bevidst valg (baseret på `x-ms-dop-hint` eller
observerede 429'ere) kræver at forbrugeren rapporterer request-niveau-udfald tilbage til poolen —
en større API-udvidelse end blot en ny `ISlotSelectionStrategy`-implementation. Det er stadig
fremtidigt scope, ikke gjort i denne omgang.

**Hvornår vælge hvilken:**
- `HealthAwareRoundRobinSlotSelectionStrategy` (default): simplest, ensartet fordeling, foretræk
  når medlemmerne typisk har ensartet requestlængde/-tid.
- `LeastConnectionsSlotSelectionStrategy`: foretræk når kald har meget varierende varighed (nogle
  medlemmer kan sidde fast i langvarige kald), så nye acquires ikke bare fortsætter med at hobe sig
  op på et allerede presset medlem.
