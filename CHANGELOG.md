# Changelog

All notable changes to `VolcanoBlue.EventSourcing` are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses [Semantic Versioning](https://semver.org/).

## [4.0.0] - 2026-11-08

### Added
- Snapshots of entities are serialized by instance fields: entities need no public setters, and can have a private parameterless constructor and `readonly` fields. `[JsonIgnore]` on a property or `[NonSerialized]` on a field excludes its state.

### Fixed
- `EventDbContext.SaveStreamAsync` clears the change tracker even when saving fails, so a retry does not resend rejected events.
- `RaiseEvent` rolls back `Version` and the uncommitted list when `ProcessEvent` throws.
- `SnapshotDbContext.UpsertAsync` overwrites a stored snapshot with the same version instead of failing with an identity conflict.

### Performance
- Entity snapshots read and write fields through emitted delegates (not `FieldInfo`) and a pooled buffer; appending events uses an indexed loop.

### Changed
- **Breaking:** `ISnapshot.EntityVersion` is `long` (was `int`), matching `EventSourcedEntity.Version` and the stored column, so no casts are needed.
- **Breaking:** `EventSourcedEntity.UncommittedEvents` is an `IReadOnlyList<IEvent>` (was `IReadOnlyCollection<IEvent>`).
- **Breaking:** entity snapshots are written from fields instead of public properties: logic in property setters no longer runs on restore, and `Version` comes from the snapshot's `EntityVersion`.
- **Breaking:** `EventStore.SaveAsync` throws `InvalidOperationException` before saving anything when a snapshot's `EntityVersion` is not the last applied event (`Version - 1`).
- **Breaking:** `EventTypesRegistry.Initialize` throws when two assemblies declare event types with the same full name, instead of silently keeping the first.

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
