# VolcanoBlue.EventSourcing

A .NET library with abstractions and an Entity Framework Core event store to speed up Event Sourcing development.
Model your aggregates, then load and save them with a single type: `EventStore`.

- **Target framework:** .NET 10
- **Dependencies:** `Microsoft.EntityFrameworkCore`
- **License:** Unlicense

## Installation

```bash
dotnet add package VolcanoBlue.EventSourcing
```

You also need an EF Core provider for your database (SQL Server, SQLite, PostgreSQL, ...).

## Concepts

| Type | Namespace | Purpose |
|------|-----------|---------|
| `EventStore` | (root) | **The main entry point.** `LoadAsync` and `SaveAsync` for your aggregates. |
| `EventSourcedEntity` | `Abstractions` | Base class for aggregates: tracks `Version`, uncommitted events, and applies events. |
| `IEvent` | `Abstractions` | Contract for domain events. `EventId` is the sequence number in the stream and the entity version. |
| `ISnapshot` | `Abstractions` | Contract for a materialized state at a given `EntityVersion`. An entity can implement it to be its own snapshot and decide when one is taken (`ShouldTakeSnapshot()`). |
| `EventSourcingOptions` | (root) | Instance-based configuration: event types and `TimeProvider`. |
| `EventStoreDbContext` | (root) | The single EF Core context with the `Events` and `Snapshots` tables. It only stores rows (see [Storage](#storage)). |
| `AddEventSourcing` | `Extensions` | DI registration of `EventSourcingOptions` and `EventStore`. |

### Versioning

- `Version` is the id of the last applied event. A new entity starts at `Version = 0`.
- `RaiseEvent(event)` increments `Version`, records the event as uncommitted and applies it via `ProcessEvent`.
- Create events with `NextEventId` (`Version + 1`) as `EventId` *before* calling `RaiseEvent` (the first event has `EventId = 1`). `RaiseEvent` does not validate the id; `SaveAsync` does, throwing `InvalidOperationException` (nothing saved) when the pending events are not consecutive. A competing writer is reported as `ConcurrencyException`.
- `Rehydrate(ImmutableArray<IEvent> stream)` replays stored events, setting `Version` to each `EventId`, so `Version` ends at the last event. The store calls it for you.
- `CommitEvents()` clears the uncommitted list; `EventStore.SaveAsync` calls it after a successful save.

## Quick start

### 1. Define events

```csharp
using VolcanoBlue.EventSourcing.Abstractions;

public sealed record NameChanged(long EventId, string Name) : IEvent;
```

### 2. Define an entity

Entities loaded by the store need a parameterless constructor (it can be private or protected).

```csharp
public sealed class Customer : EventSourcedEntity
{
    public string? Name { get; private set; }

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name is required.", nameof(name));

        RaiseEvent(new NameChanged(NextEventId, name));
    }

    protected override void ProcessEvent(IEvent @event)
    {
        switch (@event)
        {
            case NameChanged e:
                Name = e.Name;
                break;
            default:
                throw new InvalidOperationException($"Unsupported event {@event.GetType().Name}.");
        }
    }
}
```

### 3. Register everything

```csharp
using VolcanoBlue.EventSourcing.Extensions;

// One context, one connection string, one set of migrations (tables Events and Snapshots).
services.AddDbContext<EventStoreDbContext>(o => o.UseSqlite("Data Source=events.db"));

// Registers EventSourcingOptions (singleton) and EventStore (scoped).
// Events are registered one by one.
services.AddEventSourcing(options => options.AddEvent<NameChanged>());

// Or scan assemblies (the calling assembly when none is given).
services.AddEventSourcing(typeof(NameChanged).Assembly);
```

Without DI:

```csharp
var options = new EventSourcingOptions().AddEvent<NameChanged>();
var store = new EventStore(db, options); // db is an EventStoreDbContext
```

`EventSourcingOptions` is instance-based (no global state) and is shared as a singleton. It is frozen once an `EventStore` uses it: further changes throw. `AddEvent<TEvent>()`, `AddEvent(Type)` and `AddEventsFrom(params Assembly[])` (scanning, skips open generic types) register event types; `TimeProvider` (default `TimeProvider.System`) is a settable property. Event types are resolved by name when reading (see [Serialization](#serialization)).

### 4. Save and load

Inject `EventStore` and use the stream id of your aggregate:

```csharp
public sealed class CustomerService(EventStore store)
{
    public async Task RenameAsync(string id, string name)
    {
        var streamId = $"Customer-{id}";

        var customer = await store.LoadAsync<Customer>(streamId);
        var entity = customer ?? new Customer();

        entity.Rename(name);
        await store.SaveAsync(streamId, entity); // throws ConcurrencyException if another writer got there first
    }
}
```

## Recommended entity design

The `Customer` above is the minimum. For a real aggregate, this is the shape that works best with the library:

```csharp
public sealed record OrderPlaced(long EventId, string CustomerId) : IEvent;
public sealed record ItemAdded(long EventId, string Sku, int Quantity) : IEvent;
public sealed record OrderShipped(long EventId) : IEvent;

public sealed record OrderItem(string Sku, int Quantity);

public sealed class Order : EventSourcedEntity, ISnapshot
{
    private readonly List<OrderItem> _items = [];

    private Order() { } // used only by the EventStore

    public static Order Place(string customerId)
    {
        if (string.IsNullOrWhiteSpace(customerId))
            throw new ArgumentException("Customer is required.", nameof(customerId));

        var order = new Order();
        order.RaiseEvent(new OrderPlaced(order.NextEventId, customerId));
        return order;
    }

    public string CustomerId { get; private set; } = "";
    public bool Shipped { get; private set; }
    public IReadOnlyList<OrderItem> Items => _items;
    public long EntityVersion => Version;

    public void AddItem(string sku, int quantity)
    {
        if (Shipped) throw new InvalidOperationException("Order already shipped.");
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));

        RaiseEvent(new ItemAdded(NextEventId, sku, quantity));
    }

    public void Ship()
    {
        if (Shipped) return; // idempotent
        if (_items.Count == 0) throw new InvalidOperationException("Order has no items.");

        RaiseEvent(new OrderShipped(NextEventId));
    }

    public bool ShouldTakeSnapshot() => EntityVersion % 50 == 0; // every 50 events

    // Only applies state: no validation, no exceptions, no side effects.
    protected override void ProcessEvent(IEvent @event)
    {
        switch (@event)
        {
            case OrderPlaced e: CustomerId = e.CustomerId; break;
            case ItemAdded e: _items.Add(new(e.Sku, e.Quantity)); break;
            case OrderShipped: Shipped = true; break;
        }
    }
}
```

Why this design:

- **State changes only through events.** Setters are private and the collection is exposed read-only, so nothing can change state without `RaiseEvent`. A change made any other way would not be stored and would be lost on the next load by replay.
- **A private constructor plus a static factory.** The store builds the entity through the private parameterless constructor and replays events into it. `Place` is the only way to create a new one, and it raises the first event, so a new order can never exist in an invalid state. A public constructor with parameters would still need the parameterless one, and the two could drift apart.
- **Validation in commands, never in `ProcessEvent`.** `ProcessEvent` also runs when events are replayed from the store. Events are facts that already happened, so a rule that is stricter today must not make old streams unloadable.
- **`EventId` is `NextEventId`.** This keeps ids sequential and lets the database reject two writers appending the same event id (optimistic concurrency).
- **Immutable event records.** They serialize cleanly and cannot be altered after being raised.
- **`EntityVersion` is derived from `Version`.** It cannot get out of sync with the entity, and `SaveAsync` rejects a snapshot whose `EntityVersion` is not the last applied event.
- **Snapshots keep the encapsulation.** In the default reflection mode snapshots are written from the entity fields, so private setters, `readonly` fields and a private constructor all survive a restore. See [Snapshots](#snapshots) and [Serialization](#serialization) for the source-generated mode.
- **`ShouldTakeSnapshot()` is a cheap rule on the entity.** `SaveAsync` applies it for you after each save.
- **Idempotent commands (`Ship`).** Repeating a command that already took effect raises no new event, so retries do not pollute the stream.

## Store API (`EventStore`)

| Method | Description |
|--------|-------------|
| `LoadAsync<T>(streamId, ct)` | Creates a `T` and rehydrates it. When `T` implements `ISnapshot` it starts from the last snapshot and replays only the later events; for any other type the `Snapshots` table is never queried. Without a snapshot, or when the stored one can no longer be read back (for example after refactoring the entity), it replays the whole stream. Returns `T?`; `null` when the stream is empty. Events are applied while they stream from the database, never materialized in memory. |
| `SaveAsync<T>(streamId, entity, ct)` | Appends the uncommitted events and, if the entity implements `ISnapshot` and `ShouldTakeSnapshot()` returns true, its snapshot, in **one transaction** (a single `SaveChanges`), then calls `CommitEvents()`. Does nothing when there are no uncommitted events. See [Snapshots](#snapshots). |

Notes:

- Construct it with `new EventStore(EventStoreDbContext db, EventSourcingOptions options)` or let DI do it.
- Saving an event whose type is not registered throws `InvalidOperationException` before anything is written.
- `T` is created through a cached, compiled factory (no reflection per call), so non-public parameterless constructors are fine. A type without one throws `InvalidOperationException`.
- Two writers appending the same `EventId` to a stream conflict and `SaveAsync` throws `ConcurrencyException` (`StreamId`, `ExpectedVersion`, `ActualVersion`): reload the entity, reapply the command and save again. Other write failures surface as the original `DbUpdateException`. In a conflict neither the events nor the snapshot are written.
- The store is scoped, like the `DbContext` it wraps. Don't share one instance across threads.

## Snapshots

Snapshots avoid replaying long streams. The entity is its own snapshot: `T : EventSourcedEntity, ISnapshot`, where `EntityVersion` is the last event applied. `LoadAsync<T>` uses the last snapshot automatically.

```csharp
public sealed class Customer : EventSourcedEntity, ISnapshot
{
    public long EntityVersion => Version; // restored from the snapshot row's EntityVersion

    public string? Name { get; private set; }

    public bool ShouldTakeSnapshot() => Version % 50 == 0; // optional; the default never snapshots

    // Rename and ProcessEvent as above
}
```

The snapshot is stored as JSON in the `Snapshots` table. `EventSourcedEntity.Version` and `UncommittedEvents` are `[JsonIgnore]`; `Version` is restored from the row's `EntityVersion`. The entity is serialized by its fields (see [Serialization](#serialization)).

`SaveAsync` takes snapshots for you: if the entity implements `ISnapshot` and `ShouldTakeSnapshot()` returns true, the events **and** the snapshot are written in one transaction: both are stored or neither. Everything is serialized before anything is written, so a snapshot that cannot be serialized (or an unregistered event type) saves nothing and leaves the uncommitted events in place. `EntityVersion` must equal the last applied event (`Version`); otherwise `SaveAsync` throws `InvalidOperationException` before saving anything, because a wrong value would make loads skip or repeat an event. One snapshot is kept per entity: a stored row with the same version is overwritten, others are removed.

```csharp
await store.SaveAsync(streamId, customer); // snapshots automatically when ShouldTakeSnapshot() is true

// Later: latest snapshot + only the newer events
var loaded = await store.LoadAsync<Customer>(streamId);
```

The snapshot id is the stream id. A snapshot that cannot be read back (`JsonException`) makes `LoadAsync` replay the whole stream.

## Serialization

Events and snapshots are serialized with `System.Text.Json` through reflection.

- Fields are included and property names are case-insensitive on read.
- Events may have private constructors (internal `PrivateConstructorConverter`), keeping domain types free of serialization concerns.
- Entities are snapshotted by their instance fields (emitted accessors): no public setters are needed, the parameterless constructor can be private, and private and `readonly` fields are saved and restored. Auto-property backing fields are stored under the property name. Computed properties (`Name => _name`) have no field, so they are never persisted and need no attribute. Mark an auto-property `[JsonIgnore]` (or a field `[NonSerialized]`) to leave its state out. Setter logic is not run on restore, only fields are assigned. Each field is written and read using its declared type, so a field typed as an interface or base class (other than collection interfaces such as `IReadOnlyList<T>`) cannot keep its concrete type: declare the concrete type.

```csharp
public sealed class Customer : EventSourcedEntity, ISnapshot
{
    private string? _name;

    public string? Name => _name;
    public long EntityVersion => Version;

    // Rename as above
    public bool ShouldTakeSnapshot() => true;

    protected override void ProcessEvent(IEvent @event) { /* ... */ }
}
```

Event names:

- Event types must be concrete classes/records implementing `IEvent` and must be registered in `EventSourcingOptions`; saving an unregistered type throws `InvalidOperationException` before anything is written, and reading an unknown stored name throws too.
- The full type name is stored by default, so renaming or moving an event type breaks reading existing events. Put `[EventName("order-placed")]` on the event to store a stable name instead; events already stored under the full name stay readable. Names must be unique across the registered types; duplicates throw at the first use of the options.

## Native AOT and trimming

The library is reflection based by design (entity fields, private constructors, emitted accessors) and is not compatible with trimming or Native AOT.

## Storage

`EventStoreDbContext` has public `DbSet<Event> Events` and `DbSet<Snapshot> Snapshots` and no read or append methods: serialization and persistence are done by `EventStore`.

- Events are stored in the `Events` table (`Event` entity): `Id` (the event's `EventId`), `StreamId`, `OccurredAt` (UTC), `EventType` (the `[EventName]`, or the full type name) and `Data` (JSON).
- The primary key is `(Id, StreamId)`; there is also an index on `(StreamId, Id)` for reads.
- Snapshots are stored in the `Snapshots` table (`Snapshot` entity): `EntityId`, `EntityVersion`, `Created` and `Data` (JSON), with primary key `(EntityId, EntityVersion)`.
- `OccurredAt` and `Created` are UTC text with sub-second precision (`yyyy-MM-dd HH:mm:ss.fffffff`) taken from `EventSourcingOptions.TimeProvider`. Rows stored without the fraction still read and sort fine; the values are informational, ordering uses `Id`.
- Reads use `AsNoTracking` and stream the rows.

## Database setup

The library ships no migrations; create the schema yourself. For tests, see the PostgreSQL setup in `test/DatabaseFixture.cs`:

```csharp
db.Database.EnsureCreated(); // db is the EventStoreDbContext: creates Events and Snapshots
```

With migrations, the single `EventStoreDbContext` gives one migration set for both tables.

## Mapping a legacy database

If the events table already exists with its own names (database-first), keep the schema as is and map the library's `Event` entity onto it. `Event` and `Snapshot` are public, and `EventStoreDbContext` accepts an optional `EventStoreModelConfiguration(ModelBuilder)` callback resolved from DI, which maps both `Event` and `Snapshot`. It runs after the default configuration, so your mapping wins. `EventStore` works on top of the mapped context unchanged.

### 1. The existing table (`DatabaseFirst.db`)

```sql
CREATE TABLE event_log (
    event_id    INTEGER NOT NULL,   -- Event.Id (the event's EventId)
    stream_name TEXT    NOT NULL,   -- Event.StreamId
    created_utc TEXT    NOT NULL,   -- Event.OccurredAt
    type_name   TEXT    NOT NULL,   -- Event.EventType (full CLR type name)
    payload     TEXT    NOT NULL,   -- Event.Data (JSON)
    PRIMARY KEY (event_id, stream_name)
);
```

All five columns are required, and the primary key must be `(Id, StreamId)` to keep the optimistic concurrency behavior.

The snapshot table (only needed if you use snapshots):

```sql
CREATE TABLE snapshot_log (
    entity_name    TEXT    NOT NULL,   -- Snapshot.EntityId
    entity_version INTEGER NOT NULL,   -- Snapshot.EntityVersion
    created_utc    TEXT    NOT NULL,   -- Snapshot.Created
    state_json     TEXT    NOT NULL,   -- Snapshot.Data (JSON)
    PRIMARY KEY (entity_name, entity_version)
);
```

### 2. Map the columns when registering the context

```csharp
using Microsoft.EntityFrameworkCore;
using VolcanoBlue.EventSourcing;
using VolcanoBlue.EventSourcing.Extensions;

// The mapping callback is picked up by the context's constructor.
services.AddSingleton<EventStoreModelConfiguration>(modelBuilder =>
{
    modelBuilder.Entity<Event>(entity =>
    {
        entity.ToTable("event_log");
        entity.Property(e => e.Id).HasColumnName("event_id");
        entity.Property(e => e.StreamId).HasColumnName("stream_name");
        entity.Property(e => e.OccurredAt).HasColumnName("created_utc");
        entity.Property(e => e.EventType).HasColumnName("type_name");
        entity.Property(e => e.Data).HasColumnName("payload");
    });

    modelBuilder.Entity<Snapshot>(entity =>
    {
        entity.ToTable("snapshot_log");
        entity.Property(e => e.EntityId).HasColumnName("entity_name");
        entity.Property(e => e.EntityVersion).HasColumnName("entity_version");
        entity.Property(e => e.Created).HasColumnName("created_utc");
        entity.Property(e => e.Data).HasColumnName("state_json");
    });
});

services.AddDbContext<EventStoreDbContext>(options =>
    options.UseSqlite("Data Source=DatabaseFirst.db"));

services.AddEventSourcing(options => options.AddEvent<NameChanged>());
```

### 3. Use it as usual

```csharp
await store.SaveAsync("Customer-42", customer);
var loaded = await store.LoadAsync<Customer>("Customer-42");
```

Notes:

- Don't call `EnsureCreated()` or run migrations against a legacy database; the schema is owned by the database.
- Map `Snapshot` only if you use snapshots; the table must still exist for them to be stored.
- Existing rows must hold the `[EventName]` (or the full type name) of the event in the type column and JSON in the data column, as written by this library.
- EF caches the model per context type, so use a single mapping per application. A working example is in `test/DatabaseFirstTests.cs`.

## Performance tips

Built in: entity factories are compiled once per type, reads use `AsNoTracking` and are applied while streaming, `SaveAsync` persists the events and the snapshot in one round trip, `LoadAsync` queries `Snapshots` only for `ISnapshot` types, there is a `(StreamId, Id)` index, and change detection is disabled on `EventStoreDbContext`.

Recommended in your application:

- **Use snapshots** for long streams: `LoadAsync` reads the latest snapshot and only the newer events. Let `ShouldTakeSnapshot()` return true every N events and `SaveAsync` handles the rest.
- **Legacy schemas:** create an index on `(stream column, id column)` yourself, as the library doesn't create it for you.
- **Database tuning:** keep connection pooling on, use WAL for SQLite, and consider native `datetime`/`jsonb` columns when you own the schema.
- **Caching:** keep hot aggregates in a short-lived cache and apply only events newer than the cached `Version`.

## Project layout

```
src/
  Abstractions/             IEvent, EventSourcedEntity, ISnapshot
  (root)                    EventStore, EventStoreDbContext, EventSourcingOptions, Event, Snapshot, EventStoreModelConfiguration
  Serialization/            event and snapshot serializers, event type registry, PrivateConstructorConverter
  Extensions/               AddEventSourcing (DI registration)
test/                       xUnit tests against PostgreSQL (Testcontainers)
```

## Build and test

```bash
dotnet build
dotnet test   # the tests start a PostgreSQL container: Docker must be running
```

## License

Released under the [Unlicense](LICENSE).
