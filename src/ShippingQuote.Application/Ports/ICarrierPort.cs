using ShippingQuote.Domain;

namespace ShippingQuote.Application.Ports;

public sealed record CarrierQuote(decimal AmountArs, int EtaDays);

public sealed class CarrierUnavailableException(string message) : Exception(message);

/// <summary>
/// Puerto secundario: cada transportista real es un adaptador distinto de este
/// contrato. El caso de uso no sabe cuantos hay ni como hablan.
/// </summary>
public interface ICarrierPort
{
    string Name { get; }

    Task<CarrierQuote> GetRateAsync(Package package, Zone zone, ITracer tracer, CancellationToken ct = default);
}

public static class CarrierContract
{
    /// <summary>
    /// Cuanto espera un adaptador antes de dar al transportista por caido.
    /// Vive en el puerto y no en cada adaptador porque es parte del contrato:
    /// cualquier ICarrierPort debe responder o fallar dentro de este plazo.
    /// </summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);
}
