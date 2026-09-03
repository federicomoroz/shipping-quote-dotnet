using System.Text.Json;
using Microsoft.Extensions.Logging;
using ShippingQuote.Application.Ports;
using ShippingQuote.Domain;

namespace ShippingQuote.Infrastructure.Carriers;

/// <summary>
/// Los tres transportistas, definidos como datos.
///
/// Cada uno tiene su propio vocabulario -peso_kg / weight / kg, zona / region /
/// zona_andreani- porque son APIs ajenas que no se pusieron de acuerdo. Traducir
/// ese vocabulario al del dominio es exactamente el trabajo del adaptador, y es
/// lo unico que cambia entre uno y otro.
/// </summary>
public static class CarrierCatalog
{
    public sealed record CarrierSpec(
        string Name,
        string EndpointPath,
        Func<Package, Zone, object> BuildRequest,
        Func<JsonElement, CarrierQuote> ParseResponse);

    public static readonly IReadOnlyList<CarrierSpec> All =
    new CarrierSpec[]
    {
        new CarrierSpec(
            "Correo Argentino",
            "/correo-argentino/cotizar",
            (p, z) => new { peso_kg = p.EffectiveWeightKg, zona = z.Wire() },
            json => new CarrierQuote(
                json.GetProperty("monto").GetDecimal(),
                json.GetProperty("dias_habiles").GetInt32())),

        new CarrierSpec(
            "OCA",
            "/oca/quote",
            (p, z) => new { weight = p.EffectiveWeightKg, region = z.Wire() },
            json => new CarrierQuote(
                json.GetProperty("price").GetDecimal(),
                json.GetProperty("estimated_delivery").GetInt32())),

        new CarrierSpec(
            "Andreani",
            "/andreani/tarifar",
            (p, z) => new { kg = p.EffectiveWeightKg, zona_andreani = z.Wire() },
            json => new CarrierQuote(
                json.GetProperty("tarifa_pesos").GetDecimal(),
                json.GetProperty("eta_dias").GetInt32())),
    };

    public static ICarrierPort Build(CarrierSpec spec, HttpClient client, ILogger<HttpCarrierAdapter> logger) =>
        new HttpCarrierAdapter(spec.Name, client, spec.EndpointPath, spec.BuildRequest, spec.ParseResponse, logger);
}
