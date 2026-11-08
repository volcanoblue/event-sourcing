# VolcanoBlue.EventSourcing

A .NET library with abstractions and an Entity Framework Core event store to speed up Event Sourcing development.

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
| `IEvent` | `Abstractions` | Contract for domain events. `EventId` is the sequence number in the stream and the entity version. |
| `EventSourcedEntity` | `Abstractions` | Base class for aggregates: tracks `Version`, uncommitted events, and applies events. |
| `ISnapshot` | `Abstractions` | Contract for a materialized state at a given `EntityVersion`. |
| `ISnapshotable<T>` | `Abstractions` | Optional contract for entities that decide when and how to create a snapshot. |
| `EventDbContext` | `EventStore` | Append-only event store (read streams, append, save). |
| `SnapshotDbContext` | `EventStore` | Snapshot store (get by id, upsert). |
| `EventTypesRegistry` | `EventStore.Serialization` | Maps stored type names to CLR event types for deserialization. |

### Versioning

- A new entity starts at `Version = 1`.
- `RaiseEvent(event)` increments `Version`, records the event as uncommitted and applies it via `ProcessEvent`.
- Create events with the current `Version` as `EventId` *before* calling `RaiseEvent` (the first event has `EventId = 1`).
- `Rehydrate(stream)` replays stored events (setting `Version` to each `EventId`) and then increments `Version`, so the next event gets the next id. Don't call it with an empty stream, as it would still bump the version.
- `CommitEvents()` clears the uncommitted list after a successful save.

## Quick start

### 1. Define events

```csharp
using VolcanoBlue.EventSourcing.Abstractions;

public sealed record NameChanged(long EventId, string Name) : IEvent;
```

### 2. Define an entity

```csharp
public sealed class Customer : EventSourcedEntity
{
    public string StreamId { get; }
    public string? Name { get; private set; }

    public Customer(string id) => StreamId = $"Customer-{id}";

    public static Customer Rehydrate(string id, ImmutableArray<IEvent> stream)
    {
        var customer = new Customer(id);
        if (!stream.IsEmpty)
            customer.Rehydrate(stream);
        return customer;
    }

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

### 3. Register event types and the stores

Event types are resolved by full name when reading. Call `EventTypesRegistry.Initialize` once at startup with every assembly containing events (later calls are ignored).

```csharp
EventTypesRegistry.Initialize(typeof(NameChanged).Assembly);

services.AddDbContext<EventDbContext>(o => o.UseSqlite("Data Source=events.db"));
services.AddDbContext<SnapshotDbContext>(o => o.UseSqlite("Data Source=events.db"));
```

### 4. Save and load

```csharp
// Save
var customer = new Customer("42");
customer.Rename("Ada");

await eventStore.AppendToStreamAsync(customer.StreamId, customer.UncommittedEvents);
await eventStore.SaveStreamAsync(); // throws DbUpdateException on conflicts
customer.CommitEvents();

// Load
var stream = await eventStore.ReadStreamAsync("Customer-42");
var loaded = Customer.Rehydrate("42", stream);
```

## Event store API (`EventDbContext`)

| Method | Description |
|--------|-------------|
| `ReadStreamAsync(streamId, ct)` | All events of a stream ordered by id, as `ImmutableArray<IEvent>`. Empty if the stream doesn't exist. |
| `ReadStreamFromEventIdAsync(streamId, eventId, ct)` | Events with `Id > eventId`; useful after loading a snapshot. |
| `AppendToStreamAsync(streamId, events, ct)` | Serializes and stages events. Nothing is persisted until saved. |
| `SaveStreamAsync(ct)` | Persists staged events; returns the number of rows written; throws `DbUpdateException` on failures such as a duplicate `EventId`. |

Storage details:

- Events are stored in an internal `Event` table: `Id` (the event's `EventId`), `StreamId`, `OccurredAt` (UTC), `EventType` (full type name) and `Data` (JSON).
- The primary key is `(Id, StreamId)`, so two writers appending the same `EventId` to a stream conflict and the save fails (optimistic concurrency).
- Reads use `AsNoTracking` and return immutable arrays.
- Failures are thrown as exceptions (e.g. `DbUpdateException` when the primary key conflicts).

## Snapshots

Snapshots avoid replaying long streams. A snapshot is any type implementing `ISnapshot` (`EntityVersion` = last event applied).

```csharp
public sealed record CustomerSnapshot(int EntityVersion, string? Name) : ISnapshot;
```

`SnapshotDbContext`:

| Method | Description |
|--------|-------------|
| `ByIdAsync<T>(id, ct)` | `Option<T>`; `None` if no snapshot exists. |
| `UpsertAsync(entityId, snapshot, ct)` | Replaces the stored snapshot for the entity. Call `SaveChangesAsync()` to persist. |

Typical rebuild flow:

```csharp
var snapshot = await snapshots.ByIdAsync<CustomerSnapshot>("Customer-42");
var state = snapshot ? snapshot.Get() : new CustomerSnapshot(0, null);

var tail = await events.ReadStreamFromEventIdAsync("Customer-42", state.EntityVersion);
// apply tail on top of state
```

Implement `ISnapshotable<TSnapshot>` (`ShouldTakeSnapshot()` / `TakeSnapshot()`) on an entity to encapsulate when and how snapshots are taken.

## Serialization

- Events are serialized with `System.Text.Json`; fields are included and property names are case-insensitive on read.
- Types with private constructors can be deserialized (internal `PrivateConstructorConverter`), keeping domain types free of serialization concerns.
- Event types must be concrete classes/records implementing `IEvent`, and their assembly must be passed to `EventTypesRegistry.Initialize`; otherwise reading throws `InvalidOperationException` ("Event type ... not found").
- The full type name is stored, so renaming or moving an event type breaks reading existing events.

## Database setup

The library ships no migrations; create the schema yourself. For tests, see the SQLite in-memory setup in `test/SqliteFixture.cs`:

```csharp
eventStore.Database.EnsureCreated();
snapshotStore.GetService<IRelationalDatabaseCreator>().CreateTables(); // when sharing one database
```

## Mapping a legacy database

If the events table already exists with its own names (database-first), keep the schema as is and map the library's `Event` entity onto it. `Event` and `Snapshot` are public, and each context accepts an optional callback resolved from DI: `EventModelConfiguration` for `EventDbContext` and `SnapshotModelConfiguration` for `SnapshotDbContext`. They are separate delegate types so each context only receives its own mapping. The callback runs after the default configuration, so your mapping wins.

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
using VolcanoBlue.EventSourcing.EventStore;
using VolcanoBlue.EventSourcing.EventStore.Serialization;

EventTypesRegistry.Initialize(typeof(NameChanged).Assembly);

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
```

### 3. Use it as usual

```csharp
await eventStore.AppendToStreamAsync("Customer-42", customer.UncommittedEvents);
await eventStore.SaveStreamAsync();

var stream = await eventStore.ReadStreamAsync("Customer-42");

await snapshotStore.UpsertAsync("Customer-42", new CustomerSnapshot(2, "Ada"));
await snapshotStore.SaveChangesAsync();
```

Notes:

- Don't call `EnsureCreated()` or run migrations against a legacy database; the schema is owned by the database.
- Both mappings are optional and independent: register only the callbacks you need.
- Existing rows must hold the full type name of the event in the type column and JSON in the data column, as written by this library.
- EF caches the model per context type, so use a single mapping per application. A working example is in `test/DatabaseFirstTests.cs`.

## Performance tips

Built in: reads use `AsNoTracking`, a single async query per read, a `(StreamId, Id)` index, and change detection is disabled on `EventDbContext`.

Recommended in your application:

- **Use snapshots** for long streams: load the snapshot, then `ReadStreamFromEventIdAsync` from its `EntityVersion`. Take one every N events (see `ISnapshotable<T>`).
- **Batch appends:** append all events of an aggregate in one `AppendToStreamAsync` and one `SaveStreamAsync` call (one round trip).
- **Legacy schemas:** create an index on `(stream column, id column)` yourself, as the library doesn't create it for you.
- **Pooling:** `AddDbContextPool<EventDbContext>` reduces allocations; the contexts hold no per-request state.
- **Database tuning:** keep connection pooling on, use WAL for SQLite, and consider native `datetime`/`jsonb` columns when you own the schema.
- **Caching:** keep hot aggregates in a short-lived cache and apply only events newer than the cached `Version`.

## Project layout

```
src/
  Abstractions/             IEvent, EventSourcedEntity, ISnapshot, ISnapshotable<T>
  EventStore/               EventDbContext, SnapshotDbContext, errors, persistence records
  EventStore/Serialization/ EventSerializer, SnapshotSerializer, EventTypesRegistry, PrivateConstructorConverter
test/                       xUnit tests using in-memory SQLite
```

## Build and test

```bash
dotnet build
dotnet test
```

## License

Released under the [Unlicense](LICENSE).
