using System.Net.Mime;
using System.Text.Json;
using ShippingQuote.Domain;

namespace ShippingQuote.Api.Middleware;

/// <summary>
/// Traduce las excepciones del dominio a respuestas HTTP.
///
/// Vive en el pipeline y no en cada controller por la misma razon por la que el
/// dominio no importa ASP.NET: la regla "un bulto de mas de 30kg no se cotiza"
/// es de negocio, y que eso se comunique como un 422 es una decision de
/// transporte. Un controller nuevo hereda el mapeo sin escribir un try/catch.
///
/// Es, ademas, el patron Chain of Responsibility: cada middleware decide si
/// maneja el request o se lo pasa al siguiente.
/// </summary>
public sealed class DomainExceptionMiddleware(RequestDelegate next, ILogger<DomainExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception exc) when (exc is PackageTooHeavyException or InvalidPostalCodeException)
        {
            // Entrada invalida, no falla del servidor: se registra como
            // informacion y no como error, para no ensuciar las alertas.
            logger.LogInformation("request rechazado por el dominio: {Message}", exc.Message);

            if (context.Response.HasStarted)
            {
                throw;
            }

            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
            context.Response.ContentType = MediaTypeNames.Application.Json;
            await context.Response.WriteAsync(
                JsonSerializer.Serialize(new { detail = exc.Message }));
        }
    }
}

public static class DomainExceptionMiddlewareExtensions
{
    public static IApplicationBuilder UseDomainExceptionHandling(this IApplicationBuilder app) =>
        app.UseMiddleware<DomainExceptionMiddleware>();
}
