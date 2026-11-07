using Microsoft.EntityFrameworkCore;

namespace VolcanoBlue.EventSourcing.EventStore
{
    /// <summary>
    /// Callback invoked at the end of <see cref="EventDbContext"/> model creation.
    /// Register it in DI to map the <see cref="Event"/> entity onto an existing schema.
    /// Distinct from <see cref="SnapshotModelConfiguration"/> so each context only receives its own mapping.
    /// </summary>
    public delegate void EventModelConfiguration(ModelBuilder modelBuilder);

    /// <summary>
    /// Callback invoked at the end of <see cref="SnapshotDbContext"/> model creation.
    /// Register it in DI to map the <see cref="Snapshot"/> entity onto an existing schema.
    /// </summary>
    public delegate void SnapshotModelConfiguration(ModelBuilder modelBuilder);
}
