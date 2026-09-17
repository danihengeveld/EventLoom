using System.Runtime.CompilerServices;

namespace EventLoom.UnitTests;

internal static class SqliteTestBootstrap
{
    [ModuleInitializer]
    internal static void Initialize() =>
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
}
