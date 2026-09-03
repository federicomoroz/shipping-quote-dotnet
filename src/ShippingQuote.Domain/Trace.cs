using System.Diagnostics;

namespace ShippingQuote.Domain;

/// <summary>Un hop real del circuito hexagonal, con el tiempo desde que entro el request.</summary>
public sealed record TraceEntry(string Step, string Label, string Detail, double ElapsedMs);

/// <summary>
/// Abstraccion de nivel dominio -sin dependencias- que puertos y adaptadores
/// usan para dejar constancia de su propio hop, sin acoplarse a como se
/// acumula la traza.
/// </summary>
public interface ITracer
{
    IReadOnlyList<TraceEntry> Entries { get; }

    void Mark(string step, string label, string detail);
}

/// <summary>Unica implementacion: acumulador con timestamps relativos al inicio del request.</summary>
public sealed class TraceRecorder : ITracer
{
    private readonly List<TraceEntry> _entries = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public IReadOnlyList<TraceEntry> Entries => _entries;

    public void Mark(string step, string label, string detail)
    {
        var elapsedMs = Math.Round(_clock.Elapsed.TotalMilliseconds, 2);
        _entries.Add(new TraceEntry(step, label, detail, elapsedMs));
    }
}
