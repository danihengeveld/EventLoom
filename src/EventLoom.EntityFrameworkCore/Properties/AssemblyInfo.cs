using System.Runtime.CompilerServices;

// Friend packages: the relational providers derive from the internal EfStorageDialect and call the shared
// UseEntityFramework registration. Dependencies between EventLoom packages are pinned to exact versions
// (Directory.Build.targets).
[assembly: InternalsVisibleTo("EventLoom.EntityFrameworkCore.PostgreSql")]
[assembly: InternalsVisibleTo("EventLoom.EntityFrameworkCore.Sqlite")]
[assembly: InternalsVisibleTo("EventLoom.EntityFrameworkCore.UnitTests")]
[assembly: InternalsVisibleTo("EventLoom.EntityFrameworkCore.PostgreSql.IntegrationTests")]
[assembly: InternalsVisibleTo("EventLoom.EntityFrameworkCore.Sqlite.IntegrationTests")]
