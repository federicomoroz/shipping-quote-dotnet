using System.Diagnostics;
using ShippingQuote.Application.Ports;
using ShippingQuote.Application.UseCases;
using ShippingQuote.Domain;

namespace ShippingQuote.Tests;

/// <summary>
/// Un transportista falso con latencia controlada. Sirve para medir si el caso
/// de uso consulta a los tres en paralelo o en fila, sin depender de HTTP.
/// </summary>
internal sealed class SlowCarrier(string name, TimeSpan latency, decimal amount = 1000m) : ICarrierPort
{
    private int _calls;

    public string Name { get; } = name;

    public int Calls => Volatile.Read(ref _calls);

    /// <summary>Momento en que arranco la llamada, para verificar solapamiento real.</summary>
    public long StartedAtTicks { get; private set; }

    public async Task<CarrierQuote> GetRateAsync(
        Package package, Zone zone, ITracer tracer, CancellationToken ct = default)
    {
        StartedAtTicks = Stopwatch.GetTimestamp();
        Interlocked.Increment(ref _calls);

        // Task.Delay, no Thread.Sleep: libera el hilo del pool mientras espera
        // en vez de ocuparlo sin hacer nada. Es la diferencia entre un servicio
        // que aguanta mil requests concurrentes y uno que se queda sin hilos.
        await Task.Delay(latency, ct);

        return new CarrierQuote(amount, EtaDays: 3);
    }
}

internal sealed class FailingCarrier(string name, TimeSpan latency) : ICarrierPort
{
    public string Name { get; } = name;

    public async Task<CarrierQuote> GetRateAsync(
        Package package, Zone zone, ITracer tracer, CancellationToken ct = default)
    {
        await Task.Delay(latency, ct);
        throw new CarrierUnavailableException($"{Name} no disponible (503)");
    }
}

internal sealed class InMemoryHistory : IQuoteHistoryPort
{
    private readonly List<QuoteRecordData> _rows = new();

    public IReadOnlyList<QuoteRecordData> Rows => _rows;

    public Task SaveAsync(QuoteRecordData record, CancellationToken ct = default)
    {
        lock (_rows)
        {
            _rows.Add(record);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<QuoteRecordData>> ListRecentAsync(int limit = 20, CancellationToken ct = default)
    {
        lock (_rows)
        {
            return Task.FromResult<IReadOnlyList<QuoteRecordData>>(_rows.TakeLast(limit).Reverse().ToList());
        }
    }
}

public class ConcurrencyTests
{
    private static readonly QuoteRequest Request = new(2.0, 20, 20, 20, PostalCode: 1425);

    [Fact]
    public async Task LosTransportistasSeConsultanEnParalelo_NoEnFila()
    {
        var latency = TimeSpan.FromMilliseconds(300);
        var carriers = new ICarrierPort[]
        {
            new SlowCarrier("A", latency),
            new SlowCarrier("B", latency),
            new SlowCarrier("C", latency),
        };
        var useCase = new QuoteShippingUseCase(carriers, new InMemoryHistory());

        var clock = Stopwatch.StartNew();
        var response = await useCase.ExecuteAsync(Request, new TraceRecorder());
        clock.Stop();

        Assert.Equal(3, response.Results.Count);
        Assert.All(response.Results, r => Assert.True(r.Ok));

        // En fila serian 900ms. En paralelo, ~300ms. El techo de 700ms deja
        // margen para una maquina cargada sin dejar pasar la version secuencial.
        Assert.True(
            clock.ElapsedMilliseconds < 700,
            $"tardo {clock.ElapsedMilliseconds}ms: parece secuencial, no paralelo");
    }

    [Fact]
    public async Task ElRequestTardaLoQueElMasLento_NoLaSuma()
    {
        var carriers = new ICarrierPort[]
        {
            new SlowCarrier("rapido", TimeSpan.FromMilliseconds(50)),
            new SlowCarrier("medio", TimeSpan.FromMilliseconds(150)),
            new SlowCarrier("lento", TimeSpan.FromMilliseconds(400)),
        };
        var useCase = new QuoteShippingUseCase(carriers, new InMemoryHistory());

        var clock = Stopwatch.StartNew();
        await useCase.ExecuteAsync(Request, new TraceRecorder());
        clock.Stop();

        // La suma es 600ms; el mas lento, 400ms.
        Assert.True(
            clock.ElapsedMilliseconds < 600,
            $"tardo {clock.ElapsedMilliseconds}ms, cerca de la suma de las latencias");
    }

    [Fact]
    public async Task UnTransportistaCaidoNoTumbaLaCotizacion()
    {
        var carriers = new ICarrierPort[]
        {
            new SlowCarrier("OCA", TimeSpan.FromMilliseconds(20), amount: 1000m),
            new FailingCarrier("Correo Argentino", TimeSpan.FromMilliseconds(20)),
            new SlowCarrier("Andreani", TimeSpan.FromMilliseconds(20), amount: 1200m),
        };
        var useCase = new QuoteShippingUseCase(carriers, new InMemoryHistory());

        var response = await useCase.ExecuteAsync(Request, new TraceRecorder());

        Assert.Equal(3, response.Results.Count);
        Assert.Equal(2, response.Results.Count(r => r.Ok));

        var failed = Assert.Single(response.Results, r => !r.Ok);
        Assert.Equal("Correo Argentino", failed.Carrier);
        Assert.Contains("503", failed.Error);
    }

    [Fact]
    public async Task ElCaidoNoImpideGuardarElMejorPrecioDeLosQueSiRespondieron()
    {
        var history = new InMemoryHistory();
        var carriers = new ICarrierPort[]
        {
            new SlowCarrier("OCA", TimeSpan.Zero, amount: 2000m),
            new FailingCarrier("Correo Argentino", TimeSpan.Zero),
            new SlowCarrier("Andreani", TimeSpan.Zero, amount: 1000m),
        };

        await new QuoteShippingUseCase(carriers, history).ExecuteAsync(Request, new TraceRecorder());

        var saved = Assert.Single(history.Rows);
        Assert.Equal("Andreani", saved.BestCarrier);
    }

    [Fact]
    public async Task CadaTransportistaSeConsultaUnaSolaVezPorRequest()
    {
        var carriers = new[]
        {
            new SlowCarrier("A", TimeSpan.Zero),
            new SlowCarrier("B", TimeSpan.Zero),
        };

        await new QuoteShippingUseCase(carriers, new InMemoryHistory())
            .ExecuteAsync(Request, new TraceRecorder());

        Assert.All(carriers, c => Assert.Equal(1, c.Calls));
    }

    [Fact]
    public async Task CancelarElRequestCortaLasLlamadasEnVuelo()
    {
        var carriers = new ICarrierPort[] { new SlowCarrier("lento", TimeSpan.FromSeconds(30)) };
        var useCase = new QuoteShippingUseCase(carriers, new InMemoryHistory());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var clock = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => useCase.ExecuteAsync(Request, new TraceRecorder(), cts.Token));
        clock.Stop();

        // Si el token no estuviera enhebrado hasta el adaptador, esto tardaria
        // 30 segundos en vez de cortar al toque.
        Assert.True(clock.ElapsedMilliseconds < 5000, $"la cancelacion no se propago: {clock.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task VariosRequestsConcurrentesNoSePisanEntreSi()
    {
        var useCase = new QuoteShippingUseCase(
            new ICarrierPort[] { new SlowCarrier("A", TimeSpan.FromMilliseconds(30)) },
            new InMemoryHistory());

        // Cada request trae su propio tracer: si hubiera estado compartido
        // mutable en el caso de uso, las trazas se mezclarian.
        var responses = await Task.WhenAll(
            Enumerable.Range(1000, 40).Select(cp =>
                useCase.ExecuteAsync(Request with { PostalCode = cp }, new TraceRecorder())));

        Assert.Equal(40, responses.Length);
        Assert.All(responses, r => Assert.Single(r.Results));

        // Cada traza tiene que hablar solo de su propio codigo postal.
        foreach (var (response, cp) in responses.Zip(Enumerable.Range(1000, 40)))
        {
            Assert.Contains(response.Trace, e => e.Detail.Contains($"CP {cp} "));
            Assert.DoesNotContain(response.Trace, e => e.Detail.Contains($"CP {cp + 1} "));
        }
    }
}
