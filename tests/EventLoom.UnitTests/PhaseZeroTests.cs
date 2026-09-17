namespace EventLoom.UnitTests;

public sealed class PhaseZeroTests
{
    [Test]
    public async Task Repository_builds_with_TUnit()
    {
        var assemblyName = typeof(PhaseZeroTests).Assembly.GetName().Name;

        await Assert.That(assemblyName).IsEqualTo("EventLoom.UnitTests");
    }
}