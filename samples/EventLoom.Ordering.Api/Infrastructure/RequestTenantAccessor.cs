namespace EventLoom.Ordering.Api.Infrastructure;

internal sealed class RequestTenantAccessor(IHttpContextAccessor httpContextAccessor) : ITenantAccessor
{
    public TenantId? TenantId
    {
        get
        {
            var value = httpContextAccessor.HttpContext?.Request.Headers["X-Tenant-ID"].FirstOrDefault();
            return string.IsNullOrWhiteSpace(value) ? null : new TenantId(value);
        }
    }
}

internal static class TenantRequirementMiddleware
{
    public static async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (!context.Request.Headers.TryGetValue("X-Tenant-ID", out var tenant) ||
            string.IsNullOrWhiteSpace(tenant))
        {
            await Results.BadRequest(new { error = "The X-Tenant-ID request header is required." })
                .ExecuteAsync(context);
            return;
        }

        await next(context);
    }
}
