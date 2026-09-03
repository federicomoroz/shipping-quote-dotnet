using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using ShippingQuote.Application.Ports;
using ShippingQuote.Domain;

namespace ShippingQuote.Api.Controllers;

/// <summary>
/// Adaptador primario. Traduce JSON a QuoteRequest, invoca el puerto y
/// devuelve. No conoce ningun transportista ni la base de datos.
/// </summary>
[ApiController]
[Route("api")]
public sealed class QuotesController(IShippingQuotePort useCase, IQuoteHistoryPort history) : ControllerBase
{
    [HttpPost("quote")]
    [ProducesResponseType(typeof(QuoteResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<QuoteResponse>> PostQuote(QuotePayload payload, CancellationToken ct)
    {
        var tracer = new TraceRecorder();
        tracer.Mark("entrada", "HTTP", $"POST /api/quote peso={payload.WeightKg}kg CP={payload.PostalCode}");
        tracer.Mark("adaptador_primario", nameof(QuotesController), "traduciendo JSON -> QuoteRequest");

        var request = new QuoteRequest(
            payload.WeightKg, payload.LengthCm, payload.WidthCm, payload.HeightCm, payload.PostalCode);

        tracer.Mark("puerto_primario", nameof(IShippingQuotePort), "invocando caso de uso");

        // Sin try/catch: las excepciones de dominio las traduce el middleware.
        return await useCase.ExecuteAsync(request, tracer, ct);
    }

    [HttpGet("history")]
    public async Task<ActionResult<IReadOnlyList<QuoteRecordData>>> GetHistory(CancellationToken ct) =>
        Ok(await history.ListRecentAsync(ct: ct));
}

/// <summary>
/// Techos de sanidad del input HTTP, a proposito mas permisivos que las reglas
/// de negocio: asi el que rechaza un bulto de 40kg es el dominio, con su
/// mensaje explicando peso real contra volumetrico, y no un 400 generico.
/// </summary>
public sealed record QuotePayload
{
    public const double MaxInputWeightKg = 100.0;
    public const double MaxInputDimensionCm = 200.0;

    [Range(double.Epsilon, MaxInputWeightKg)]
    public double WeightKg { get; init; }

    [Range(double.Epsilon, MaxInputDimensionCm)]
    public double LengthCm { get; init; }

    [Range(double.Epsilon, MaxInputDimensionCm)]
    public double WidthCm { get; init; }

    [Range(double.Epsilon, MaxInputDimensionCm)]
    public double HeightCm { get; init; }

    [Range(1000, 9999)]
    public int PostalCode { get; init; }
}
