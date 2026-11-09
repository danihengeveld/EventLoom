using System.Runtime.CompilerServices;

// Friend packages: they use the aggregate replay/pending-event hooks and the snapshot dispatcher. Dependencies
// between EventLoom packages are pinned to exact versions (Directory.Build.targets) so these stay in sync.
[assembly: InternalsVisibleTo("EventLoom.Storage")]
[assembly: InternalsVisibleTo("EventLoom.Hosting")]
[assembly: InternalsVisibleTo("EventLoom.Testing")]
[assembly: InternalsVisibleTo("EventLoom.UnitTests")]
[assembly: InternalsVisibleTo("EventLoom.EntityFrameworkCore.UnitTests")]
[assembly: InternalsVisibleTo("EventLoom.EntityFrameworkCore.Sqlite.IntegrationTests")]
[assembly: InternalsVisibleTo("EventLoom.EntityFrameworkCore.PostgreSql.IntegrationTests")]
