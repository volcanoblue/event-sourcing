# Changelog

All notable changes to `VolcanoBlue.EventSourcing` are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses [Semantic Versioning](https://semver.org/).

## [5.0.0] - 2026-11-08

### Removed
- **Breaking:** `EventSourcingOptions.TypeInfoResolver` and source-generated serialization. Events and snapshots are always serialized through reflection. `[JsonInclude]`/`[JsonConstructor]` annotations and a `JsonSerializerContext` are no longer needed (nor used).
- **Breaking:** trimming and Native AOT support. The library is reflection based by design: the trim/AOT analyzers and every `[RequiresUnreferencedCode]`, `[RequiresDynamicCode]` and `[DynamicallyAccessedMembers]` annotation were removed, and the entity factory always compiles the constructor.

### Changed
- Computed properties (`Name => _name`) have no field, so they are never persisted and need no `[JsonIgnore]`. `[JsonIgnore]` is only for excluding an auto-property; `[NonSerialized]` for a field.

### Fixed
- An event whose only constructor is private and parameterless (state in fields or public setters) failed to read with "converter read too much or not enough"; it is now created through that constructor and filled like any other event.
- Reading an event whose stored data is `null` throws `JsonException` instead of a `NullReferenceException`.
- A snapshotted entity whose base class declares a field with the same name as a derived class no longer stores one over the other: the base field is stored as `BaseType.name`.
- Malformed snapshot JSON (including a non-object) always surfaces as `JsonException`, so `LoadAsync` falls back to a full replay.

### Performance
- Event constructor arguments and snapshot fields are read straight from `Utf8JsonReader` (no `JsonDocument`), common scalar types (`string`, `int`, `long`, `bool`, `decimal`, `double`, `Guid`, `DateTime`, `DateTimeOffset`) skip the per-value serializer setup, and the `JsonTypeInfo` of each field and constructor parameter is cached. Measured against 4.0.0 (steady state, reflection mode): snapshot read about 25% faster, snapshot write about 20% faster, event read about 5% faster.
- Snapshot serialization reuses a per-thread buffer and writer.
- Replacing a snapshot reads only the keys of the stored rows (not their `Data`) and deletes stale rows by key.

## [4.0.0] - 2026-11-08

### Added
- `EventStoreDbContext`: a single context with `DbSet<Event> Events` and `DbSet<Snapshot> Snapshots` (one connection string, one set of migrations; the schema is unchanged from 3.0.0). `EventStoreModelConfiguration(ModelBuilder)` maps both entities for database-first schemas.
- `EventSourcingOptions`: instance-based configuration (no global state) with `AddEvent<TEvent>()`, `AddEvent(Type)`, `AddEventsFrom(params Assembly[])` (scanning, `[RequiresUnreferencedCode]`), `TypeInfoResolver` and `TimeProvider`. It is shared through DI as a singleton and frozen once an `EventStore` uses it. Build without DI with `new EventStore(db, options)`.
- `AddEventSourcing(options => options.AddEvent<T>())` (trim/AOT friendly) next to `AddEventSourcing(params Assembly[])`; both register `EventSourcingOptions` (singleton) and `EventStore` (scoped).
- Source-generated serialization: set `EventSourcingOptions.TypeInfoResolver` (a `JsonSerializerContext`) to serialize events and snapshotted entities with generated metadata and no reflection over the entity. Persisted state must be `internal` or public with `[JsonInclude]`, the parameterless constructor `internal` or public with `[JsonConstructor]`, and the entity and its context in the same assembly (see README and `test/SourceGenTests.cs`). Without a resolver on a runtime that disallows reflection, snapshots throw an `InvalidOperationException` pointing at `TypeInfoResolver`.
- The library enables the trim and AOT analyzers with 0 warnings and runs with `JsonSerializerIsReflectionEnabledByDefault=false` when a `TypeInfoResolver` is supplied. EF Core itself is not fully trimming/Native-AOT compatible (its `DbContext` constructor is marked `RequiresUnreferencedCode`/`RequiresDynamicCode`, and `EventStoreDbContext` passes that on), so a full Native AOT publish also depends on EF Core's own support.
- `EventStore.LoadAsync<T>` uses the last snapshot automatically when `T` implements `ISnapshot` and replays only later events. A snapshot that cannot be read back (`JsonException`) falls back to a full replay.
- Snapshots of entities are serialized by instance fields in reflection mode (the default): entities need no public setters, and can have a private parameterless constructor and `readonly` fields. `[JsonIgnore]` on a property or `[NonSerialized]` on a field excludes its state.
- `[EventName("...")]` stores an event under a stable name instead of its full type name; events stored under the full name stay readable.
- `ConcurrencyException` (`StreamId`, `ExpectedVersion`, `ActualVersion`) thrown by `EventStore.SaveAsync` when another writer appended first; other write failures keep surfacing as `DbUpdateException`.
- `EventSourcedEntity.NextEventId` (`Version + 1`).
- Field values in snapshots are read with the same private-constructor converter and `IncludeFields` setting as events.

### Fixed
- A snapshot with the same version as the stored one is overwritten instead of failing with an identity conflict; one snapshot is kept per entity.
- Events and snapshot are saved in one transaction (a single `SaveChanges`): both are stored or neither, and a conflict writes neither. Everything is serialized before anything is written, so a snapshot that cannot be serialized saves nothing and leaves the uncommitted events in place.
- Saving an event whose type is not registered throws `InvalidOperationException` before anything is written (it used to be written and then fail on read).
- `EventSourcedEntity.Version` and `UncommittedEvents` are `[JsonIgnore]`; `Version` is restored from the snapshot row's `EntityVersion`.

### Performance
- Events are applied while they stream from the database; the stream is never materialized in memory.
- `LoadAsync` never queries the `Snapshots` table for types that are not `ISnapshot`.
- Entity snapshots read and write fields through emitted delegates (not `FieldInfo`) and a pooled buffer; entities are created by a compiled factory (`ConstructorInfo.Invoke` without dynamic code).

### Changed
- **Breaking:** `EventStore.LoadAsync<T>` returns `T?` (`null` when the stream is empty) instead of Moonad's `Option<T>`; the `Moonad` dependency was removed.
- **Breaking:** `EventDbContext`, `SnapshotDbContext`, `EventModelConfiguration` and `SnapshotModelConfiguration` were removed in favor of `EventStoreDbContext` and `EventStoreModelConfiguration`. The context only stores rows: `ReadStreamAsync`, `ReadStreamFromEventIdAsync`, `AppendToStreamAsync`, `SaveStreamAsync`, `CurrentVersionAsync`, `ByIdAsync` and `UpsertAsync` are no longer public API.
- **Breaking:** `EventTypesRegistry` (and `Initialize`) was removed in favor of `EventSourcingOptions`; `AddEventSourcing()` no longer initializes a static registry. Duplicate event names throw at the first use of the options.
- **Breaking:** `EventStore.LoadFromSnapshotAsync` was removed (`LoadAsync<T>` uses snapshots) and the `EventStore(EventDbContext, SnapshotDbContext)` constructor is now `EventStore(EventStoreDbContext, EventSourcingOptions)`.
- **Breaking:** `Event.OccurredAt` and `Snapshot.Created` are UTC text with sub-second precision (`yyyy-MM-dd HH:mm:ss.fffffff`) from `EventSourcingOptions.TimeProvider`; rows stored without the fraction still read and sort fine.
- **Breaking:** `EventSourcedEntity.Version` is the id of the last applied event and starts at 0 (it used to be the id of the next event, starting at 1). Raise events with `NextEventId` instead of `Version`, and a snapshot's `EntityVersion` is now simply `Version` (it used to be `Version - 1`). Snapshots already stored keep their `EntityVersion` meaning (last applied event), so they stay valid.
- **Breaking:** `RaiseEvent(IEvent)` is now a plain apply-and-record (no generic parameter, no rollback); use `NextEventId` for the `EventId`. `SaveAsync` validates that the pending events have consecutive `EventId`s and throws `InvalidOperationException` before writing anything.
- **Breaking:** `EventStore.SaveAsync` throws `ConcurrencyException` (was `DbUpdateException`) when the stream moved on.
- **Breaking:** `ISnapshot.EntityVersion` is `long` (was `int`), matching `EventSourcedEntity.Version` and the stored column, so no casts are needed.
- **Breaking:** `EventSourcedEntity.UncommittedEvents` is an `IReadOnlyList<IEvent>` (was `IReadOnlyCollection<IEvent>`).
- **Breaking:** entity snapshots are written from fields instead of public properties: logic in property setters no longer runs on restore, and `Version` comes from the snapshot's `EntityVersion`.
- **Breaking:** `EventStore.SaveAsync` throws `InvalidOperationException` before saving anything when a snapshot's `EntityVersion` is not the last applied event (`Version`).

#### Migrating from 3.0.0
The schema is unchanged. Replace the two contexts by `EventStoreDbContext` (`AddDbContext<EventStoreDbContext>`) and the model callbacks by `EventStoreModelConfiguration`; replace `EventTypesRegistry.Initialize(asm)` by `AddEventSourcing(o => o.AddEvent<T>())` (or keep `AddEventSourcing(asm)`); replace `LoadFromSnapshotAsync<T>` by `LoadAsync<T>`.

## [3.0.0] - 2026-11-08

### Changed
- **Breaking:** `ISnapshotable<T>` was removed and merged into `ISnapshot`, which gains `ShouldTakeSnapshot()` (default `false`). The entity is its own snapshot, so `TakeSnapshot()` no longer exists.
- `EventStore.SaveAsync` persists the entity as a snapshot when it implements `ISnapshot` and `ShouldTakeSnapshot()` returns true; it throws `InvalidOperationException` if `EntityVersion` is not the last applied event (`Version - 1`).

## [2.0.0] - 2026-11-08

### Added
- `EventStore` (`LoadAsync`, `LoadFromSnapshotAsync`, `SaveAsync`) with per-type compiled entity factories; registered by `AddEventSourcing`. `SaveAsync` takes and persists a snapshot when the entity is `ISnapshotable<>` and `ShouldTakeSnapshot()` returns true.
- `AddEventSourcing(params Assembly[])` registers events from the given assemblies (the calling assembly by default).

### Changed
- **Breaking:** namespaces now follow the folder structure: `VolcanoBlue.EventSourcing.EventStore` became `VolcanoBlue.EventSourcing` and `VolcanoBlue.EventSourcing.EventStore.Serialization` became `VolcanoBlue.EventSourcing.Serialization`. Update your `using` directives.
- **Breaking:** `EventSourcedEntity.Rehydrate` is public and takes `ImmutableArray<IEvent>`; `ISnapshotable<out T>` is covariant; `SnapshotDbContext.ByIdAsync` takes `entityId`.
- `EventTypesRegistry.Initialize` scans each assembly once instead of ignoring every call after the first, and tolerates types that fail to load.
- `EventSerializer` writes with `IncludeFields`, matching reading.
- `SnapshotDbContext.ByIdAsync` returns the highest snapshot version.

### Fixed
- `SnapshotDbContext.UpsertAsync` no longer fails with an identity conflict when the same context instance replaces a snapshot it saved earlier.
- Snapshots serialize the runtime type, so a snapshot referenced only as `ISnapshot` keeps its state.
- Entities restored from a snapshot with no later events end at `EntityVersion + 1`.

## [1.0.2] - 2026-11-08

### Added
- `EventDbContext` declares an index on `(StreamId, Id)` to serve stream reads.
- README "Performance tips" section and this changelog.

### Changed
- `EventDbContext` reads use a single async `ToListAsync` query instead of a count followed by a blocking enumeration.
- `EventDbContext` disables automatic change detection, and `SaveStreamAsync` clears the change tracker after saving so reused contexts do not accumulate appended events.
- `EventTypesRegistry.Initialize` is thread-safe (double-checked lock and `TryAdd`).
- `PrivateConstructorConverter` deserializes constructor arguments directly from the `JsonElement` (no `GetRawText()` re-parse) and uses a plain array instead of `ArrayPool`.

### Removed
- Unused `EventSerializer.TypesCache`.

## [1.0.0] - 2026-11-07

Commits `f71d952`, `0e4248a`, `f5467d1`.

### Added
- Legacy (database-first) support: `Event` and `Snapshot` are public, and `EventDbContext` / `SnapshotDbContext` accept an optional model configuration callback (`EventModelConfiguration` / `SnapshotModelConfiguration`, resolved from DI) to map existing tables and columns.
- `services.AddEventSourcing()` extension and the `VolcanoBlue.EventSourcing.Extensions` namespace.
- `DatabaseFirstTests` covering mapped event and snapshot tables on `DatabaseFirst.db`.
- Full README documentation (concepts, quick start, API, snapshots, legacy mapping).

### Changed
- **Breaking:** `EventDbContext.ReadStream*Async` return `ImmutableArray<IEvent>`, `SaveStreamAsync` returns `int`, and failures are thrown as exceptions (e.g. `DbUpdateException` on a duplicate `EventId`) instead of `Result<T, IError>`.
- **Breaking:** `SnapshotDbContext.ByIdAsync<T>` returns `Option<T>` (`None` when missing).
- **Breaking:** `EventTypeRegistry` renamed to `EventTypesRegistry`.
- Tests updated to the new API.

### Removed
- **Breaking:** `IError`, `ExceptionalError` and `SnapshotNotFoundError`.
- Unused `Newtonsoft.Json` package reference (serialization already used `System.Text.Json`).

## [0.0.1] - 2026-11-07

Commits `1218ef3`, `b0d63f5`.

### Added
- Initial release: `IEvent`, `EventSourcedEntity`, `ISnapshot`, `ISnapshotable<T>`.
- `EventDbContext` and `SnapshotDbContext` (EF Core) with JSON serialization, `EventTypeRegistry` and `PrivateConstructorConverter`.
- NuGet packaging metadata and icon.
- xUnit tests with in-memory SQLite: event store, snapshot store, snapshots combined with events, and an event-sourced entity.
- GitHub Actions workflow to build, test and publish on `v*` tags.
