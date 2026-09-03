using ShippingQuote.Application.Ports;
using ShippingQuote.Domain;

namespace ShippingQuote.Application.UseCases;

/// <summary>
/// El centro del hexagono. Recibe los transportistas por constructor como una
/// coleccion de <see cref="ICarrierPort"/>: no sabe cuantos son, quienes son,
/// ni que hablan HTTP. Sumar un cuarto es registrarlo en el composition root.
/// </summary>
public sealed class QuoteShippingUseCase(
    IEnumerable<ICarrierPort> carriers,
    IQuoteHistoryPort history) : IShippingQuotePort
{
    private readonly IReadOnlyList<ICarrierPort> _carriers = carriers.ToList();
    private readonly IReadOnlyList<IPipelineStep> _steps =
        new IPipelineStep[] { new ValidateEligibilityStep(), new ClassifyZoneStep() };

    public async Task<QuoteResponse> ExecuteAsync(
        QuoteRequest request, ITracer tracer, CancellationToken ct = default)
    {
        tracer.Mark("caso_de_uso", nameof(QuoteShippingUseCase), "inicio");

        var ctx = new QuoteContext(request, tracer);
        foreach (var step in _steps)
        {
            await step.ExecuteAsync(ctx, ct);
        }

        // Despues del pipeline ambos estan poblados; si no, es un bug de
        // programacion y no una condicion de runtime que valga la pena manejar.
        var package = ctx.Package ?? throw new InvalidOperationException("el pipeline no produjo un Package");
        var zone = ctx.Zone ?? throw new InvalidOperationException("el pipeline no produjo una Zone");

        // Todos los transportistas se consultan en paralelo: el request tarda
        // lo que el mas lento, no la suma de los tres.
        var results = await Task.WhenAll(_carriers.Select(c => QuoteFromAsync(c, package, zone, tracer, ct)));

        var best = results.Where(r => r.Ok).MinBy(r => r.AmountArs);
        await history.SaveAsync(
            new QuoteRecordData(
                DateTimeOffset.UtcNow,
                request.PostalCode,
                zone,
                package.EffectiveWeightKg,
                best?.Carrier,
                best?.AmountArs),
            ct);

        return new QuoteResponse(zone, package.EffectiveWeightKg, results, tracer.Entries.ToList());
    }

    /// <summary>
    /// Un transportista caido devuelve un resultado fallido, no tumba la
    /// cotizacion: el usuario prefiere dos precios de tres antes que un error.
    /// </summary>
    private static async Task<CarrierResult> QuoteFromAsync(
        ICarrierPort carrier, Package package, Zone zone, ITracer tracer, CancellationToken ct)
    {
        tracer.Mark("puerto_secundario", nameof(ICarrierPort), $"-> {carrier.Name}");

        CarrierQuote quote;
        try
        {
            quote = await carrier.GetRateAsync(package, zone, tracer, ct);
        }
        catch (CarrierUnavailableException exc)
        {
            return new CarrierResult(carrier.Name, Ok: false, AmountArs: null, EtaDays: null, Error: exc.Message);
        }

        var finalAmount = FeePolicy.Apply(quote.AmountArs, zone, package.EffectiveWeightKg);
        tracer.Mark(
            "dominio",
            nameof(FeePolicy),
            $"{carrier.Name}: ${quote.AmountArs:F0} + comision -> ${finalAmount:F0}");

        return new CarrierResult(carrier.Name, Ok: true, finalAmount, quote.EtaDays, Error: null);
    }
}
