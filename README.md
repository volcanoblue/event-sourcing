# VolcanoBlue.EventSourcing

A .NET library with abstractions and an Entity Framework Core event store to speed up Event Sourcing development.
Model your aggregates, then load and save them with a single type: `EventStore`.

- **Target framework:** .NET 10
- **Dependencies:** `Microsoft.EntityFrameworkCore`, `Moonad` (`Option<T>`)
- **License:** Unlicense

## Installation

```bash
dotnet add package VolcanoBlue.EventSourcing
```

You also need an EF Core provider for your database (SQL Server, SQLite, PostgreSQL, ...).

## Concepts

| Type | Namespace | Purpose |
|------|-----------|---------|
| `EventStore` | (root) | **The main entry point.** `LoadAsync`, `LoadFromSnapshotAsync` and `SaveAsync` for your aggregates. |
| `EventSourcedEntity` | `Abstractions` | Base class for aggregates: tracks `Version`, uncommitted events, and applies events. |
| `IEvent` | `Abstractions` | Contract for domain events. `EventId` is the sequence number in the stream and the entity version. |
| `ISnapshot` | `Abstractions` | Contract for a materialized state at a given `EntityVersion`. An entity can implement it to be its own snapshot and decide when one is taken (`ShouldTakeSnapshot()`). |
| `EventTypesRegistry` | `Serialization` | Maps stored type names to CLR event types for deserialization. |
| `EventDbContext` / `SnapshotDbContext` | (root) | The EF Core stores behind the store. You rarely need them directly (see [Low-level stores](#low-level-stores)). |

### Versioning

- A new entity starts at `Version = 1`.
- `RaiseEvent(event)` increments `Version`, records the event as uncommitted and applies it via `ProcessEvent`. If `ProcessEvent` throws, the version and the uncommitted list are rolled back.
- Create events with the current `Version` as `EventId` *before* calling `RaiseEvent` (the first event has `EventId = 1`).
- `Rehydrate(ImmutableArray<IEvent> stream)` replays stored events (setting `Version` to each `EventId`) and then increments `Version`, so the next event gets the next id. The store calls it for you.
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

        RaiseEvent(new NameChanged(Version, name));
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

services.AddDbContext<EventDbContext>(o => o.UseSqlite("Data Source=events.db"));
services.AddDbContext<SnapshotDbContext>(o => o.UseSqlite("Data Source=events.db"));

// Registers EventStore (scoped) and the event types of the given assemblies.
// Without arguments it scans the calling assembly.
services.AddEventSourcing(typeof(NameChanged).Assembly);
```

Event types are resolved by full name when reading. `AddEventSourcing` calls `EventTypesRegistry.Initialize`; you can also call it yourself at startup. Each assembly is scanned once, and later calls with new assemblies add their events.

### 4. Save and load

Inject `EventStore` and use the stream id of your aggregate:

```csharp
public sealed class CustomerService(EventStore store)
{
    public async Task RenameAsync(string id, string name)
    {
        var streamId = $"Customer-{id}";

        var customer = await store.LoadAsync<Customer>(streamId);
        var entity = customer ? customer.Get() : new Customer();

        entity.Rename(name);
        await store.SaveAsync(streamId, entity); // throws DbUpdateException on conflicts
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
        order.RaiseEvent(new OrderPlaced(order.Version, customerId));
        return order;
    }

    public string CustomerId { get; private set; } = "";
    public bool Shipped { get; private set; }
    public IReadOnlyList<OrderItem> Items => _items;
    public long EntityVersion => Version - 1;

    public void AddItem(string sku, int quantity)
    {
        if (Shipped) throw new InvalidOperationException("Order already shipped.");
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));

        RaiseEvent(new ItemAdded(Version, sku, quantity));
    }

    public void Ship()
    {
        if (Shipped) return; // idempotent
        if (_items.Count == 0) throw new InvalidOperationException("Order has no items.");

        RaiseEvent(new OrderShipped(Version));
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
- **`EventId` is `Version`, read before `RaiseEvent`.** This keeps ids sequential and lets the database reject two writers appending the same event id (optimistic concurrency).
- **Immutable event records.** They serialize cleanly and cannot be altered after being raised.
- **`EntityVersion` is derived from `Version`.** It cannot get out of sync with the entity, and `SaveAsync` rejects a snapshot whose `EntityVersion` is not the last applied event.
- **Snapshots keep the encapsulation.** Snapshots are written from the entity fields, so private setters, `readonly` fields and a private constructor all survive a restore. See [Snapshots](#snapshots).
- **`ShouldTakeSnapshot()` is a cheap rule on the entity.** `SaveAsync` applies it for you after each save.
- **Idempotent commands (`Ship`).** Repeating a command that already took effect raises no new event, so retries do not pollute the stream.

## Store API (`EventStore`)

| Method | Description |
|--------|-------------|
| `LoadAsync<T>(streamId, ct)` | Reads the whole stream, creates a `T` and rehydrates it. Returns `Option<T>`; `None` when the stream is empty. |
| `LoadFromSnapshotAsync<T>(streamId, ct)` | Loads the latest snapshot and replays only the events after it. Without a snapshot it replays the whole stream. `None` when neither exists. |
| `SaveAsync<T>(streamId, entity, ct)` | Appends the uncommitted events, saves them in one round trip and calls `CommitEvents()`. Does nothing when there are no uncommitted events. If the entity implements `ISnapshot` and `ShouldTakeSnapshot()` returns true, it then persists the entity as a snapshot (see [Snapshots](#snapshots)). |

Notes:

- `T` is created through a cached, compiled factory (no reflection per call), so non-public parameterless constructors are fine. A type without one throws `InvalidOperationException`.
- Two writers appending the same `EventId` to a stream conflict and `SaveAsync` fails with `DbUpdateException` (optimistic concurrency).
- The store is scoped, like the `DbContext`s it wraps. Don't share one instance across threads.

## Snapshots

Snapshots avoid replaying long streams. With `LoadFromSnapshotAsync<T>`, the entity is its own snapshot: `T : EventSourcedEntity, ISnapshot`, where `EntityVersion` is the last event applied.

```csharp
public sealed class Customer : EventSourcedEntity, ISnapshot
{
    // Last applied event. It is serialized with the snapshot, and its setter restores Version on load.
    public long EntityVersion
    {
        get => Version - 1;
        set => Version = value + 1;
    }

    public string? Name { get; set; }

    public bool ShouldTakeSnapshot() => Version % 50 == 0; // optional; the default never snapshots

    // Rename and ProcessEvent as above
}
```

The snapshot is stored as JSON by `SnapshotDbContext`. Entities are serialized by their instance fields, so they keep their state encapsulated: no public setters are needed, and the parameterless constructor can be private. Private and `readonly` fields are saved and restored; auto-property backing fields are stored under the property name. Mark a property `[JsonIgnore]` (or a field `[NonSerialized]`) to leave its state out. `Version` is restored from the snapshot, so `EntityVersion` can be a computed, get-only property (`Version - 1`) and the `set` in the example above is optional. Setter logic is not run on restore, only fields are assigned. Each field is written and read using its declared type, so a field typed as an interface or base class (other than collection interfaces such as `IReadOnlyList<T>`) cannot keep its concrete type: declare the concrete type.

`SaveAsync` takes snapshots for you: after the events are saved, if the entity implements `ISnapshot` and `ShouldTakeSnapshot()` returns true, it persists the entity itself, replacing the stored snapshot under the stream id. `EntityVersion` must equal the last applied event (`Version - 1`); otherwise `SaveAsync` throws `InvalidOperationException` before saving anything, because a wrong value would make loads skip or repeat an event. The snapshot is taken after `CommitEvents()`, so no uncommitted events are serialized with it. The events and the snapshot are separate writes: if the snapshot fails, the events are already stored and the next load just replays from the previous snapshot.

```csharp
await store.SaveAsync(streamId, customer); // snapshots automatically when ShouldTakeSnapshot() is true

// Later: latest snapshot + only the newer events
var loaded = await store.LoadFromSnapshotAsync<Customer>(streamId);
```

The snapshot id is the stream id. Only the latest snapshot is kept. You can still write snapshots manually with `SnapshotDbContext` (see below).

## Serialization

- Events are serialized with `System.Text.Json`; fields are included and property names are case-insensitive on read.
- Types with private constructors can be deserialized (internal `PrivateConstructorConverter`), keeping domain types free of serialization concerns.
- Event types must be concrete classes/records implementing `IEvent`, and their assembly must be registered (`AddEventSourcing` or `EventTypesRegistry.Initialize`); otherwise reading throws `InvalidOperationException` ("Event type ... not found").
- The full type name is stored, so renaming or moving an event type breaks reading existing events.

## Low-level stores

`EventStore` is built on two EF Core contexts, available when you need direct control.

`EventDbContext`:

| Method | Description |
|--------|-------------|
| `ReadStreamAsync(streamId, ct)` | All events of a stream ordered by id, as `ImmutableArray<IEvent>`. Empty if the stream doesn't exist. |
| `ReadStreamFromEventIdAsync(streamId, eventId, ct)` | Events with `Id > eventId`. |
| `AppendToStreamAsync(streamId, events, ct)` | Serializes and stages events. Nothing is persisted until saved. |
| `SaveStreamAsync(ct)` | Persists staged events; returns the number of rows written; throws `DbUpdateException` on failures such as a duplicate `EventId`. |

`SnapshotDbContext`:

| Method | Description |
|--------|-------------|
| `ByIdAsync<T>(entityId, ct)` | `Option<T>` with the highest stored version; `None` if no snapshot exists. |
| `UpsertAsync(entityId, snapshot, ct)` | Replaces the stored snapshot for the entity. Call `SaveChangesAsync()` to persist. |

Storage details:

- Events are stored in the `Events` table (`Event` entity): `Id` (the event's `EventId`), `StreamId`, `OccurredAt` (UTC), `EventType` (full type name) and `Data` (JSON).
- The primary key is `(Id, StreamId)`; there is also an index on `(StreamId, Id)` for reads.
- Snapshots are stored in the `Snapshots` table (`Snapshot` entity): `EntityId`, `EntityVersion`, `Created` and `Data` (JSON), with primary key `(EntityId, EntityVersion)`.
- Reads use `AsNoTracking` and return immutable arrays.

## Database setup

The library ships no migrations; create the schema yourself. For tests, see the SQLite in-memory setup in `test/SqliteFixture.cs`:

```csharp
eventStore.Database.EnsureCreated();
snapshotStore.GetService<IRelationalDatabaseCreator>().CreateTables(); // when sharing one database
```

## Mapping a legacy database

If the events table already exists with its own names (database-first), keep the schema as is and map the library's `Event` entity onto it. `Event` and `Snapshot` are public, and each context accepts an optional callback resolved from DI: `EventModelConfiguration` for `EventDbContext` and `SnapshotModelConfiguration` for `SnapshotDbContext`. They are separate delegate types so each context only receives its own mapping. The callback runs after the default configuration, so your mapping wins. `EventStore` works on top of the mapped contexts unchanged.

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

### 2. Map the columns when registering the contexts

```csharp
using Microsoft.EntityFrameworkCore;
using VolcanoBlue.EventSourcing;
using VolcanoBlue.EventSourcing.Extensions;

// The mapping callbacks are picked up by the contexts' constructors.
services.AddSingleton<EventModelConfiguration>(modelBuilder =>
    modelBuilder.Entity<Event>(entity =>
    {
        entity.ToTable("event_log");
        entity.Property(e => e.Id).HasColumnName("event_id");
        entity.Property(e => e.StreamId).HasColumnName("stream_name");
        entity.Property(e => e.OccurredAt).HasColumnName("created_utc");
        entity.Property(e => e.EventType).HasColumnName("type_name");
        entity.Property(e => e.Data).HasColumnName("payload");
    }));

services.AddSingleton<SnapshotModelConfiguration>(modelBuilder =>
    modelBuilder.Entity<Snapshot>(entity =>
    {
        entity.ToTable("snapshot_log");
        entity.Property(e => e.EntityId).HasColumnName("entity_name");
        entity.Property(e => e.EntityVersion).HasColumnName("entity_version");
        entity.Property(e => e.Created).HasColumnName("created_utc");
        entity.Property(e => e.Data).HasColumnName("state_json");
    }));

services.AddDbContext<EventDbContext>(options =>
    options.UseSqlite("Data Source=DatabaseFirst.db"));
services.AddDbContext<SnapshotDbContext>(options =>
    options.UseSqlite("Data Source=DatabaseFirst.db"));

services.AddEventSourcing(typeof(NameChanged).Assembly);
```

### 3. Use it as usual

```csharp
await store.SaveAsync("Customer-42", customer);
var loaded = await store.LoadAsync<Customer>("Customer-42");
```

Notes:

- Don't call `EnsureCreated()` or run migrations against a legacy database; the schema is owned by the database.
- Both mappings are optional and independent: register only the callbacks you need.
- Existing rows must hold the full type name of the event in the type column and JSON in the data column, as written by this library.
- EF caches the model per context type, so use a single mapping per application. A working example is in `test/DatabaseFirstTests.cs`.

## Performance tips

Built in: entity factories are compiled once per type, reads use `AsNoTracking` and deserialize while streaming, `SaveAsync` persists all events of an aggregate in one round trip, there is a `(StreamId, Id)` index, and change detection is disabled on `EventDbContext`.

Recommended in your application:

- **Use snapshots** for long streams: `LoadFromSnapshotAsync` reads the latest snapshot and only the newer events. Let `ShouldTakeSnapshot()` return true every N events and `SaveAsync` handles the rest.
- **Legacy schemas:** create an index on `(stream column, id column)` yourself, as the library doesn't create it for you.
- **Database tuning:** keep connection pooling on, use WAL for SQLite, and consider native `datetime`/`jsonb` columns when you own the schema.
- **Caching:** keep hot aggregates in a short-lived cache and apply only events newer than the cached `Version`.

## Project layout

```
src/
  Abstractions/             IEvent, EventSourcedEntity, ISnapshot
  (root)                    EventStore, EventDbContext, SnapshotDbContext, Event, Snapshot, model configuration
  Serialization/            EventSerializer, SnapshotSerializer, EventTypesRegistry, PrivateConstructorConverter
  Extensions/               AddEventSourcing (DI registration)
test/                       xUnit tests using in-memory SQLite
```

## Build and test

```bash
dotnet build
dotnet test
```

## License

Released under the [Unlicense](LICENSE).
