using System.Runtime.CompilerServices;
using SQLitePCL;

namespace EventLoom.UnitTests;

internal static class SqliteTestBootstrap
{
    [ModuleInitializer]
    internal static void Initialize() =>
        Batteries_V2.Init();
}
