using ShippingQuote.Application.Ports;
using ShippingQuote.Domain;

namespace ShippingQuote.Application.UseCases;

/// <summary>Estado que los pasos van completando a medida que avanza el request.</summary>
public sealed class QuoteContext(QuoteRequest request, ITracer tracer)
{
    public QuoteRequest Request { get; } = request;

    public ITracer Tracer { get; } = tracer;

    public Package? Package { get; set; }

    public Zone? Zone { get; set; }
}

/// <summary>
/// Un paso del pipeline. Sumar una regla nueva es una clase nueva registrada
/// en la lista, sin tocar las que ya funcionan.
/// </summary>
public interface IPipelineStep
{
    Task ExecuteAsync(QuoteContext ctx, CancellationToken ct = default);
}

/// <summary>Dominio: valida el bulto y calcula el peso efectivo (real vs. volumetrico).</summary>
public sealed class ValidateEligibilityStep : IPipelineStep
{
    public Task ExecuteAsync(QuoteContext ctx, CancellationToken ct = default)
    {
        var r = ctx.Request;
        var package = Package.Create(r.WeightKg, r.LengthCm, r.WidthCm, r.HeightCm);
        ctx.Package = package;

        ctx.Tracer.Mark(
            "dominio",
            nameof(ValidateEligibilityStep),
            $"peso real {r.WeightKg:F1}kg / volumetrico {package.VolumetricWeightKg:F1}kg " +
            $"-> efectivo {package.EffectiveWeightKg:F1}kg");

        return Task.CompletedTask;
    }
}

/// <summary>Dominio: mapea el codigo postal a una zona logistica.</summary>
public sealed class ClassifyZoneStep : IPipelineStep
{
    public Task ExecuteAsync(QuoteContext ctx, CancellationToken ct = default)
    {
        var zone = ZoneClassifier.Classify(ctx.Request.PostalCode);
        ctx.Zone = zone;

        ctx.Tracer.Mark(
            "dominio",
            nameof(ClassifyZoneStep),
            $"CP {ctx.Request.PostalCode} -> zona {zone.Wire()}");

        return Task.CompletedTask;
    }
}
