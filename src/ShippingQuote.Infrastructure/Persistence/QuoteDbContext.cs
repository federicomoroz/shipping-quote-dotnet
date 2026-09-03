using Microsoft.EntityFrameworkCore;
using ShippingQuote.Domain;

namespace ShippingQuote.Infrastructure.Persistence;

/// <summary>
/// Fila de historial. Es un tipo de persistencia, separado del
/// <c>QuoteRecordData</c> del puerto a proposito: el esquema de la base puede
/// cambiar sin arrastrar al dominio, que es todo el punto de tener un puerto.
/// </summary>
public sealed class QuoteRecord
{
    public int Id { get; set; }

    /// <summary>
    /// MySQL no tiene un tipo con zona horaria, asi que se guarda UTC y la
    /// conversion desde y hacia <c>DateTimeOffset</c> vive en el adaptador.
    /// El nombre lleva el sufijo Utc para que nadie lo lea como hora local.
    /// </summary>
    public DateTime CreatedAtUtc { get; set; }

    public int PostalCode { get; set; }

    public Zone Zone { get; set; }

    public double EffectiveWeightKg { get; set; }

    public string? BestCarrier { get; set; }

    /// <summary>Plata: <c>decimal</c>, nunca <c>double</c>.</summary>
    public decimal? BestAmountArs { get; set; }
}

public sealed class QuoteDbContext(DbContextOptions<QuoteDbContext> options) : DbContext(options)
{
    public DbSet<QuoteRecord> QuoteRecords => Set<QuoteRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var record = modelBuilder.Entity<QuoteRecord>();
        record.ToTable("quote_records");
        record.HasKey(r => r.Id);

        // DECIMAL nativo de MySQL: base 10, sin el error de representacion
        // binaria de DOUBLE. Doce digitos y dos decimales alcanzan para pesos.
        record.Property(r => r.BestAmountArs).HasColumnType("decimal(12,2)");

        // datetime(6): precision de microsegundos. Sin el (6), MySQL trunca a
        // segundos y dos cotizaciones del mismo segundo dejan de poder
        // ordenarse entre si.
        record.Property(r => r.CreatedAtUtc).HasColumnType("datetime(6)");

        // La zona se guarda como texto y no como el entero del enum: si manana
        // se reordenan los valores del enum, las filas viejas seguirian
        // diciendo lo mismo.
        record.Property(r => r.Zone).HasConversion<string>().HasMaxLength(16);

        record.Property(r => r.BestCarrier).HasMaxLength(64);

        // El historial se lee siempre por fecha descendente.
        record.HasIndex(r => r.CreatedAtUtc);
    }
}
