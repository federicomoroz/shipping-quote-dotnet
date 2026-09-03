using Microsoft.EntityFrameworkCore;
using ShippingQuote.Application.Ports;

namespace ShippingQuote.Infrastructure.Persistence;

/// <summary>
/// Adaptador secundario de persistencia. Traduce entre el tipo del puerto y la
/// entidad de EF Core en las dos direcciones, incluida la conversion de
/// <c>DateTimeOffset</c> a UTC: el dominio habla en instantes con offset, MySQL
/// solo sabe de <c>datetime</c>, y reconciliar eso es trabajo del adaptador.
/// </summary>
public sealed class EfCoreQuoteHistory(QuoteDbContext db) : IQuoteHistoryPort
{
    public async Task SaveAsync(QuoteRecordData record, CancellationToken ct = default)
    {
        db.QuoteRecords.Add(new QuoteRecord
        {
            CreatedAtUtc = record.CreatedAt.UtcDateTime,
            PostalCode = record.PostalCode,
            Zone = record.Zone,
            EffectiveWeightKg = record.EffectiveWeightKg,
            BestCarrier = record.BestCarrier,
            BestAmountArs = record.BestAmountArs,
        });

        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<QuoteRecordData>> ListRecentAsync(int limit = 20, CancellationToken ct = default)
    {
        // AsNoTracking: es una lectura y nada se va a modificar, asi que no
        // hace falta que el change tracker guarde una copia de cada fila.
        var rows = await db.QuoteRecords
            .AsNoTracking()
            .OrderByDescending(r => r.CreatedAtUtc)
            .ThenByDescending(r => r.Id)
            .Take(limit)
            .ToListAsync(ct);

        return rows.Select(r => new QuoteRecordData(
            new DateTimeOffset(DateTime.SpecifyKind(r.CreatedAtUtc, DateTimeKind.Utc)),
            r.PostalCode,
            r.Zone,
            r.EffectiveWeightKg,
            r.BestCarrier,
            r.BestAmountArs)).ToList();
    }
}
