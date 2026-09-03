using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ShippingQuote.Infrastructure.Carriers;

/// <summary>
/// Simulacion de las tres APIs externas, montada como un HttpMessageHandler.
///
/// Es el equivalente exacto del ASGITransport de la version Python: el
/// HttpClient hace un POST de verdad, con serializacion, status codes y
/// deserializacion reales, pero nunca sale a la red. El adaptador que se
/// ejercita en los tests es el mismo binario que corre en produccion; lo unico
/// intercambiado es el ultimo eslabon.
/// </summary>
public sealed class SimulatedCarrierHandler(SimulationOptions? options = null) : HttpMessageHandler
{
    private readonly SimulationOptions _options = options ?? new SimulationOptions();
    private readonly Random _random = new(options?.Seed ?? Environment.TickCount);
    private readonly object _lock = new();

    private sealed record Profile(
        double BasePriceArs,
        double PricePerKgArs,
        IReadOnlyDictionary<string, double> ZoneExtraArs,
        IReadOnlyDictionary<string, int> EtaDaysByZone,
        double FailureProbability,
        Func<JsonElement, (double WeightKg, string Zone)> ReadRequest,
        Func<double, int, object> WriteResponse);

    private static readonly Dictionary<string, double> ZoneExtra =
        new() { ["AMBA"] = 0, ["Interior"] = 600, ["Patagonia"] = 1800 };

    private static readonly Dictionary<string, Profile> Profiles = new()
    {
        ["/correo-argentino/cotizar"] = new Profile(
            1400, 165, ZoneExtra,
            new Dictionary<string, int> { ["AMBA"] = 5, ["Interior"] = 8, ["Patagonia"] = 12 },
            // El unico que se cae a proposito: el caso de uso tiene que
            // devolver dos cotizaciones de tres sin romperse.
            FailureProbability: 0.15,
            j => (j.GetProperty("peso_kg").GetDouble(), j.GetProperty("zona").GetString()!),
            (price, eta) => new { monto = price, dias_habiles = eta }),

        ["/oca/quote"] = new Profile(
            1700, 190, ZoneExtra,
            new Dictionary<string, int> { ["AMBA"] = 3, ["Interior"] = 6, ["Patagonia"] = 9 },
            FailureProbability: 0,
            j => (j.GetProperty("weight").GetDouble(), j.GetProperty("region").GetString()!),
            (price, eta) => new { price, estimated_delivery = eta }),

        ["/andreani/tarifar"] = new Profile(
            1900, 175, ZoneExtra,
            new Dictionary<string, int> { ["AMBA"] = 2, ["Interior"] = 5, ["Patagonia"] = 8 },
            FailureProbability: 0,
            j => (j.GetProperty("kg").GetDouble(), j.GetProperty("zona_andreani").GetString()!),
            (price, eta) => new { tarifa_pesos = price, eta_dias = eta }),
    };

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        if (!Profiles.TryGetValue(path, out var profile))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        if (_options.Latency > TimeSpan.Zero)
        {
            await Task.Delay(_options.Latency, cancellationToken);
        }

        if (_options.ForceFailurePaths.Contains(path) || NextDouble() < profile.FailureProbability)
        {
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }

        var payload = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var (weightKg, zone) = profile.ReadRequest(payload);

        if (!profile.ZoneExtraArs.TryGetValue(zone, out var extra))
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        }

        var jitter = _options.Jitter ? 0.92 + NextDouble() * 0.16 : 1.0;
        var price = Math.Round((profile.BasePriceArs + profile.PricePerKgArs * weightKg + extra) * jitter, 2);

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(profile.WriteResponse(price, profile.EtaDaysByZone[zone])),
        };
    }

    // Random no es thread-safe y los tres transportistas se consultan en paralelo.
    private double NextDouble()
    {
        lock (_lock)
        {
            return _random.NextDouble();
        }
    }
}

public sealed class SimulationOptions
{
    /// <summary>Semilla fija para que un test que dependa del azar sea reproducible.</summary>
    public int? Seed { get; init; }

    /// <summary>Latencia simulada. Cero en tests, para no pagarla en cada corrida.</summary>
    public TimeSpan Latency { get; init; } = TimeSpan.Zero;

    /// <summary>Variacion de precio. Se apaga en los tests que comparan montos exactos.</summary>
    public bool Jitter { get; init; } = true;

    /// <summary>Endpoints que fallan siempre, para ejercitar el camino de error sin depender del azar.</summary>
    public IReadOnlySet<string> ForceFailurePaths { get; init; } = new HashSet<string>();
}
