using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ShippingQuote.Infrastructure.Carriers;
using ShippingQuote.Infrastructure.Persistence;
using Testcontainers.MySql;

namespace ShippingQuote.Tests;

/// <summary>
/// Levanta la app real -mismo pipeline, mismo DI, mismos controllers- contra un
/// MySQL de verdad, efimero, en un contenedor que vive lo que dura la corrida.
///
/// No se usa una base falsa en memoria a proposito: los dos bugs mas caros de
/// este proyecto -que SQLite no sabe ordenar por DateTimeOffset, y que se
/// trababa con escrituras concurrentes- solo aparecen cuando el motor es el que
/// de verdad va a estar del otro lado. Un doble en memoria los habria tapado.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly MySqlContainer _mysql = new MySqlBuilder()
        .WithImage("mysql:8.0")
        .WithDatabase("shipping_quote")
        .WithUsername("shipping")
        .WithPassword("shipping")
        .Build();

    public async Task InitializeAsync()
    {
        await _mysql.StartAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _mysql.DisposeAsync();
        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<QuoteDbContext>>();
            services.RemoveAll<QuoteDbContext>();

            var cs = _mysql.GetConnectionString();
            services.AddDbContext<QuoteDbContext>(o =>
                o.UseMySql(cs, new MySqlServerVersion(new Version(8, 0, 36))));

            // Sin latencia ni fallas al azar: un test que falla una de cada
            // siete corridas no es un test, es ruido. Volver a registrar el
            // mismo cliente por nombre pisa el handler que puso Program.
            services.AddHttpClient(Program.CarrierHttpClient, c => c.BaseAddress = new Uri("http://carriers.local"))
                .ConfigurePrimaryHttpMessageHandler(() => new SimulatedCarrierHandler(
                    new SimulationOptions { Latency = TimeSpan.Zero, Jitter = false, Seed = 42 }));
        });
    }
}

public class ApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static object ValidPayload(int postalCode = 1425) => new
    {
        weightKg = 2.0,
        lengthCm = 20.0,
        widthCm = 20.0,
        heightCm = 20.0,
        postalCode,
    };

    [Fact]
    public async Task UnaCotizacionValidaDevuelveLosTresTransportistas()
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/quote", ValidPayload());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(3, body.GetProperty("results").GetArrayLength());
        Assert.Equal("Amba", body.GetProperty("zone").GetString());
    }

    [Fact]
    public async Task LaRespuestaTraeLaTrazaCompletaDelCircuito()
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/quote", ValidPayload());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);

        var steps = body.GetProperty("trace").EnumerateArray()
            .Select(e => e.GetProperty("step").GetString())
            .ToList();

        // Entrada, adaptador primario, puerto primario, caso de uso, dominio,
        // puerto secundario, adaptador secundario y salida: el hexagono entero.
        foreach (var expected in new[]
                 {
                     "entrada", "adaptador_primario", "puerto_primario",
                     "caso_de_uso", "dominio", "puerto_secundario",
                     "adaptador_secundario", "salida",
                 })
        {
            Assert.Contains(expected, steps);
        }
    }

    [Fact]
    public async Task UnBultoDemasiadoPesadoDevuelve422ConElMotivoDelDominio()
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/quote", new
        {
            weightKg = 40.0, lengthCm = 20.0, widthCm = 20.0, heightCm = 20.0, postalCode = 1425,
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Contains("peso efectivo", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task UnBultoEnormeYLivianoTambienSeRechazaPorVolumen()
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/quote", new
        {
            weightKg = 1.0, lengthCm = 150.0, widthCm = 150.0, heightCm = 150.0, postalCode = 1425,
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Contains("volumetrico", body.GetProperty("detail").GetString());
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    [InlineData(500.0)]
    public async Task UnPesoFueraDeLosTechosDeInputLoRechazaLaValidacionDelModelo(double weightKg)
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/quote", new
        {
            weightKg, lengthCm = 20.0, widthCm = 20.0, heightCm = 20.0, postalCode = 1425,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(1425, "Amba")]
    [InlineData(5000, "Interior")]
    [InlineData(9000, "Patagonia")]
    public async Task LaZonaSaleDelCodigoPostal(int postalCode, string expectedZone)
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/quote", ValidPayload(postalCode));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);

        Assert.Equal(expectedZone, body.GetProperty("zone").GetString());
    }

    [Fact]
    public async Task CadaCotizacionQuedaEnElHistorial()
    {
        var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/quote", ValidPayload(8400));

        var history = await client.GetFromJsonAsync<JsonElement>("/api/history", Json);

        Assert.True(history.GetArrayLength() > 0);
        Assert.Contains(
            history.EnumerateArray(),
            r => r.GetProperty("postalCode").GetInt32() == 8400);
    }

    [Fact]
    public async Task VeinteCotizacionesConcurrentesSeResuelvenTodas()
    {
        var client = factory.CreateClient();

        // El pool de conexiones, el DbContext scoped y los HttpClient tienen
        // que aguantar carga en paralelo sin pisarse.
        var responses = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(i => client.PostAsJsonAsync("/api/quote", ValidPayload(1400 + i))));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task ElHealthcheckResponde()
    {
        var response = await factory.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
