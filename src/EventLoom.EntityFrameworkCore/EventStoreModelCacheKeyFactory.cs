using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace EventLoom.EntityFrameworkCore;

internal sealed class EventStoreModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime)
    {
        if (context is not EventStoreDbContext eventStoreContext)
        {
            return (context.GetType(), designTime);
        }

        var options = eventStoreContext.Configuration;
        return (
            context.GetType(),
            options.Schema,
            options.TablePrefix,
            options.UseSchema,
            eventStoreContext.ModelConfiguration,
            designTime);
    }
}
