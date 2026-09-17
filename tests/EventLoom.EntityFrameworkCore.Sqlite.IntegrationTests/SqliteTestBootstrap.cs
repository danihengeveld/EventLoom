using System.Runtime.CompilerServices;

namespace EventLoom.UnitTests;

internal static class SqliteTestBootstrap
{
    [ModuleInitializer]
    internal static void Initialize() =>
        SQLitePCL.Batteries_V2.Init();
}
