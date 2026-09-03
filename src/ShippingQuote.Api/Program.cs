using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using ShippingQuote.Api.Middleware;
using ShippingQuote.Application.Ports;
using ShippingQuote.Application.UseCases;
using ShippingQuote.Infrastructure.Carriers;
using ShippingQuote.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

// ── Composition root ────────────────────────────────────────────────────────
// Es el unico lugar del programa donde una interfaz se ata a una clase
// concreta. Ningun otro archivo hace `new` de un adaptador: por eso el caso de
// uso se puede testear sin levantar HTTP ni base de datos.

builder.Services
    .AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// MySQL y no SQLite: SQLite serializa las escrituras, asi que un servicio que
// existe para mostrar trabajo concurrente se quedaria trabado justo en el
// unico punto donde escribe. MySQL da escrituras concurrentes reales y un pool
// de conexiones de verdad.
var connectionString = builder.Configuration.GetConnectionString("Quotes")
    ?? "Server=localhost;Port=3306;Database=shipping_quote;User=root;Password=shipping;";

// Version fijada en vez de AutoDetect: AutoDetect abre una conexion durante el
// arranque, asi que la app no levantaria si la base todavia no esta lista.
var serverVersion = new MySqlServerVersion(new Version(8, 0, 36));

builder.Services.AddDbContext<QuoteDbContext>(o =>
    o.UseMySql(connectionString, serverVersion, my => my
        // Un deadlock o un corte de conexion se reintenta solo, en vez de
        // convertirse en un 500 para el usuario.
        .EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), errorNumbersToAdd: null)));

builder.Services.AddScoped<IQuoteHistoryPort, EfCoreQuoteHistory>();

// Los transportistas hablan HTTP de verdad -serializacion, status codes,
// timeouts- contra un handler que responde en proceso. El mismo binario corre
// en produccion; lo unico que se cambiaria es este handler por el real.
builder.Services
    .AddHttpClient(CarrierHttpClient, c => c.BaseAddress = new Uri("http://carriers.local"))
    .ConfigurePrimaryHttpMessageHandler(() => new SimulatedCarrierHandler(
        new SimulationOptions { Latency = TimeSpan.FromMilliseconds(80) }));

foreach (var spec in CarrierCatalog.All)
{
    var captured = spec;
    builder.Services.AddTransient<ICarrierPort>(sp => CarrierCatalog.Build(
        captured,
        sp.GetRequiredService<IHttpClientFactory>().CreateClient(CarrierHttpClient),
        sp.GetRequiredService<ILogger<HttpCarrierAdapter>>()));
}

builder.Services.AddScoped<IShippingQuotePort, QuoteShippingUseCase>();

var app = builder.Build();

// El orden importa: el manejo de excepciones va primero para envolver a todo
// lo que venga despues.
app.UseDomainExceptionHandling();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

await EnsureDatabaseAsync(app);

app.Run();

static async Task EnsureDatabaseAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<QuoteDbContext>();

    // Migraciones y no EnsureCreated: EnsureCreated crea el esquema una vez y
    // despues no sabe evolucionarlo, asi que el primer cambio de modelo en una
    // base con datos te deja a pie.
    if (db.Database.GetMigrations().Any())
    {
        await db.Database.MigrateAsync();
    }
    else
    {
        await db.Database.EnsureCreatedAsync();
    }
}

public partial class Program
{
    public const string CarrierHttpClient = "carriers";
}
