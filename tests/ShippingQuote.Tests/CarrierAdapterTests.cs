using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using ShippingQuote.Application.Ports;
using ShippingQuote.Domain;
using ShippingQuote.Infrastructure.Carriers;

namespace ShippingQuote.Tests;

public class CarrierAdapterTests
{
    private static HttpClient ClientWith(SimulationOptions options) =>
        new(new SimulatedCarrierHandler(options)) { BaseAddress = new Uri("http://carriers.local") };

    private static ICarrierPort Build(string name, HttpClient client) =>
        CarrierCatalog.Build(
            CarrierCatalog.All.Single(c => c.Name == name),
            client,
            NullLogger<HttpCarrierAdapter>.Instance);

    private static readonly Package Package = Package.Create(2.0, 20, 20, 20);

    [Theory]
    [InlineData("Correo Argentino")]
    [InlineData("OCA")]
    [InlineData("Andreani")]
    public async Task CadaAdaptadorTraduceElDialectoDeSuTransportistaAlPuertoComun(string carrierName)
    {
        // Sin jitter y sin fallas: se compara el contrato, no el azar.
        using var client = ClientWith(new SimulationOptions { Jitter = false, Seed = 1 });
        var carrier = Build(carrierName, client);

        var quote = await carrier.GetRateAsync(Package, Zone.Amba, new TraceRecorder());

        Assert.True(quote.AmountArs > 0);
        Assert.True(quote.EtaDays > 0);
    }

    [Fact]
    public async Task LosTresDevuelvenElMismoTipoAunqueSusApisNoSeParezcan()
    {
        // Correo usa monto/dias_habiles, OCA price/estimated_delivery,
        // Andreani tarifa_pesos/eta_dias. El caso de uso ve un solo CarrierQuote.
        using var client = ClientWith(new SimulationOptions { Jitter = false, Seed = 7 });

        var quotes = await Task.WhenAll(
            CarrierCatalog.All.Select(spec =>
                Build(spec.Name, client).GetRateAsync(Package, Zone.Interior, new TraceRecorder())));

        Assert.Equal(3, quotes.Length);
        Assert.All(quotes, q => Assert.IsType<CarrierQuote>(q));
    }

    [Fact]
    public async Task UnCincuentaYTresSeTraduceAExcepcionDelPuerto()
    {
        using var client = ClientWith(new SimulationOptions
        {
            Jitter = false,
            ForceFailurePaths = new HashSet<string> { "/oca/quote" },
        });
        var carrier = Build("OCA", client);

        var exc = await Assert.ThrowsAsync<CarrierUnavailableException>(
            () => carrier.GetRateAsync(Package, Zone.Amba, new TraceRecorder()));

        // El caso de uso no debe enterarse de que existe HTTP; solo de que el
        // transportista no esta disponible.
        Assert.Contains("OCA", exc.Message);
        Assert.Contains("503", exc.Message);
    }

    [Fact]
    public async Task UnTransportistaQueTardaMasQueElTimeoutDelContratoSeDaPorCaido()
    {
        // El contrato del puerto son 2s; el simulado tarda mas.
        using var client = ClientWith(new SimulationOptions
        {
            Jitter = false,
            Latency = CarrierContract.RequestTimeout + TimeSpan.FromSeconds(1),
        });
        var carrier = Build("Andreani", client);

        var exc = await Assert.ThrowsAsync<CarrierUnavailableException>(
            () => carrier.GetRateAsync(Package, Zone.Amba, new TraceRecorder()));

        Assert.Contains("a tiempo", exc.Message);
    }

    [Fact]
    public async Task ElAdaptadorDejaSuPropioHopEnLaTraza()
    {
        using var client = ClientWith(new SimulationOptions { Jitter = false, Seed = 3 });
        var tracer = new TraceRecorder();

        await Build("OCA", client).GetRateAsync(Package, Zone.Amba, tracer);

        Assert.Contains(tracer.Entries, e => e.Step == "adaptador_secundario" && e.Label == "OCA");
        Assert.Contains(tracer.Entries, e => e.Step == "salida" && e.Label == "OCA");
    }

    [Fact]
    public async Task LaCancelacionDelLlamanteSeDistingueDelTimeoutPropio()
    {
        using var client = ClientWith(new SimulationOptions { Latency = TimeSpan.FromSeconds(10) });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        // Cancelado desde afuera: sale OperationCanceledException, no
        // CarrierUnavailableException. El transportista no fallo; nos fuimos
        // nosotros.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Build("OCA", client).GetRateAsync(Package, Zone.Amba, new TraceRecorder(), cts.Token));
    }

    [Fact]
    public async Task UnEndpointDesconocidoTambienSeReportaComoNoDisponible()
    {
        using var handler = new SimulatedCarrierHandler(new SimulationOptions { Jitter = false });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://carriers.local") };

        var response = await client.PostAsync("/transportista-inexistente", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
