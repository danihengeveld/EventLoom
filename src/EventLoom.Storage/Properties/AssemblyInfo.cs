using System.Runtime.CompilerServices;

// Friend package: Hosting composes the engine and its workers use the internal projection and outbox stores.
// Dependencies between EventLoom packages are pinned to exact versions (Directory.Build.targets).
[assembly: InternalsVisibleTo("EventLoom.Hosting")]
[assembly: InternalsVisibleTo("EventLoom.EntityFrameworkCore.UnitTests")]
[assembly: InternalsVisibleTo("EventLoom.EntityFrameworkCore.Sqlite.IntegrationTests")]
[assembly: InternalsVisibleTo("EventLoom.EntityFrameworkCore.PostgreSql.IntegrationTests")]
