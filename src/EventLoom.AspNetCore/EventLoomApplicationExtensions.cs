using EventLoom.EntityFrameworkCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EventLoom.AspNetCore;

/// <summary>Provides explicit EventLoom application lifecycle operations.</summary>
public static class EventLoomApplicationExtensions
{
    /// <summary>Creates the EventLoom schema for a new development database.</summary>
    /// <remarks>
    /// This helper refuses to run outside the Development environment. It does not
    /// migrate an existing schema; production schema changes belong in reviewed,
    /// deployment-managed EF Core migrations.
    /// </remarks>
    /// <param name="app">The configured web application.</param>
    /// <param name="cancellationToken">Cancels schema initialization.</param>
    /// <returns>A task that completes after schema initialization.</returns>
    public static async Task InitializeEventLoomDevelopmentDatabaseAsync(
        this WebApplication app,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (!app.Environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "EventLoom development database initialization is only available in the Development environment. " +
                "Apply reviewed host-owned EF Core migrations during production deployment.");
        }

        await using var scope = app.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<EventStoreDbContext>();
        await EventStoreSchema.EnsureCreatedAsync(context, cancellationToken);
    }
}
