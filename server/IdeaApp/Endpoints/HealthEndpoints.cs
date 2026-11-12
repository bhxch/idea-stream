using IdeaApp.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IdeaApp.Endpoints;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/health", () =>
            Results.Json(new HealthResponse("ok"), IdeaJsonContext.Default.HealthResponse))
            .Produces<HealthResponse>(StatusCodes.Status200OK);
        return endpoints;
    }
}
