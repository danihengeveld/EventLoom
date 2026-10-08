using Microsoft.Extensions.DependencyInjection;

namespace EventLoom.Storage.Conformance;

public abstract partial class StorageConformanceTests
{
    [Test]
    public async Task Created_storage_validates_as_compatible_and_creation_is_idempotent()
    {
        await using var environment = await CreateEnvironmentAsync();
        await using var scope = environment.CreateScope();
        var schema = scope.ServiceProvider.GetRequiredService<IStorageSchema>();

        await schema.EnsureCreatedAsync();
        await schema.EnsureCreatedAsync();
        var validation = await schema.ValidateAsync();

        await Assert.That(validation.CanConnect).IsTrue();
        await Assert.That(validation.MissingCount).IsEqualTo(0);
        await Assert.That(validation.IncompatibleCount).IsEqualTo(0);
        await Assert.That(validation.IsCompatible).IsTrue();
    }
}
