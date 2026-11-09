namespace EventLoom.EntityFrameworkCore.UnitTests;

public sealed class ProjectionKeyTests
{
    [Test]
    public async Task Invalid_names_and_versions_are_rejected_at_construction()
    {
        await Assert.That(() => new ProjectionKey(" ", 1)).Throws<ArgumentException>();
        await Assert.That(() => new ProjectionKey(null!, 1)).Throws<ArgumentNullException>();
        await Assert.That(() => new ProjectionKey("orders", 0)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task With_expressions_cannot_bypass_validation()
    {
        var key = new ProjectionKey("orders", 1);

        await Assert.That(() => key with { Version = -1 }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => key with { Name = "" }).Throws<ArgumentException>();
        await Assert.That(key with { Version = 2 }).IsEqualTo(new ProjectionKey("orders", 2));
    }
}
