using ShippingQuote.Domain;

namespace ShippingQuote.Application.Ports;

public sealed record QuoteRequest(
    double WeightKg,
    double LengthCm,
    double WidthCm,
    double HeightCm,
    int PostalCode);

public sealed record CarrierResult(
    string Carrier,
    bool Ok,
    decimal? AmountArs,
    int? EtaDays,
    string? Error);

public sealed record QuoteResponse(
    Zone Zone,
    double EffectiveWeightKg,
    IReadOnlyList<CarrierResult> Results,
    IReadOnlyList<TraceEntry> Trace);

/// <summary>
/// Puerto primario. Se deja explicito, con una unica implementacion, para que
/// el circuito hexagonal quede visible de punta a punta; con un solo caso de
/// uso lo normal seria inlinear esta llamada en el controller.
/// </summary>
public interface IShippingQuotePort
{
    Task<QuoteResponse> ExecuteAsync(QuoteRequest request, ITracer tracer, CancellationToken ct = default);
}
