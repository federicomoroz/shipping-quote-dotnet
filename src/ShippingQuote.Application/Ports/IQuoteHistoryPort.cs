using ShippingQuote.Domain;

namespace ShippingQuote.Application.Ports;

public sealed record QuoteRecordData(
    DateTimeOffset CreatedAt,
    int PostalCode,
    Zone Zone,
    double EffectiveWeightKg,
    string? BestCarrier,
    decimal? BestAmountArs);

/// <summary>
/// Puerto secundario de persistencia. El caso de uso guarda contra esta
/// interfaz y no sabe que del otro lado hay EF Core y SQLite.
/// </summary>
public interface IQuoteHistoryPort
{
    Task SaveAsync(QuoteRecordData record, CancellationToken ct = default);

    Task<IReadOnlyList<QuoteRecordData>> ListRecentAsync(int limit = 20, CancellationToken ct = default);
}
