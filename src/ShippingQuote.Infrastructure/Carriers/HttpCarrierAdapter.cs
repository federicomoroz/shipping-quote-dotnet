using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ShippingQuote.Application.Ports;
using ShippingQuote.Domain;

namespace ShippingQuote.Infrastructure.Carriers;

/// <summary>
/// ICarrierPort generico para cualquier transportista que hable HTTP+JSON.
///
/// Cada transportista aporta solo <em>datos</em>: su endpoint y dos funciones
/// de traduccion. El timeout, el manejo de errores y la traza son identicos
/// para todos y viven una sola vez aca. Es composicion: tres transportistas
/// son tres instancias de esta clase, no tres subclases casi iguales.
/// </summary>
public sealed class HttpCarrierAdapter(
    string name,
    HttpClient client,
    string endpointPath,
    Func<Package, Zone, object> buildRequest,
    Func<JsonElement, CarrierQuote> parseResponse,
    ILogger<HttpCarrierAdapter> logger) : ICarrierPort
{
    public string Name { get; } = name;

    public async Task<CarrierQuote> GetRateAsync(
        Package package, Zone zone, ITracer tracer, CancellationToken ct = default)
    {
        tracer.Mark("adaptador_secundario", Name, $"traduciendo Package -> POST {endpointPath}");

        // El timeout del contrato se aplica como cancelacion encadenada, no
        // pisando HttpClient.Timeout: asi respeta tambien la cancelacion que
        // venga del request HTTP entrante.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CarrierContract.RequestTimeout);

        HttpResponseMessage response;
        try
        {
            response = await client.PostAsJsonAsync(endpointPath, buildRequest(package, zone), timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            tracer.Mark("salida", Name, "timeout");
            logger.LogWarning("{Carrier} no respondio a tiempo", Name);
            throw new CarrierUnavailableException($"{Name} no respondio a tiempo");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode;
                tracer.Mark("salida", Name, $"la API devolvio {code}");
                logger.LogWarning("{Carrier} no disponible: HTTP {Status}", Name, code);
                throw new CarrierUnavailableException($"{Name} no disponible ({code})");
            }

            var body = await response.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
            var quote = parseResponse(body);
            tracer.Mark("salida", Name, $"respuesta: {body.GetRawText()}");
            return quote;
        }
    }
}
