# Shipping Quote — ASP.NET Core

Cotizador de envíos con arquitectura hexagonal, portado desde
[la versión en Python/FastAPI](https://github.com/federicomoroz/shipping-quote).

Cada request consulta a tres transportistas **en paralelo**, atraviesa el
hexágono completo —entrada, adaptador primario, puerto, caso de uso, dominio,
puerto secundario, adaptador secundario y salida— y devuelve la traza de esos
hops en la propia respuesta. Uno de los transportistas se cae a propósito: la
respuesta trae dos cotizaciones de tres en vez de un error.

```
POST /api/quote
{ "weightKg": 2, "lengthCm": 20, "widthCm": 20, "heightCm": 20, "postalCode": 1425 }
```

## Concurrencia

Es el punto del proyecto, así que está verificado y no afirmado.

Los tres transportistas se consultan con `Task.WhenAll`: el request tarda lo
que el más lento, no la suma de los tres. Con 300 ms de latencia cada uno,
secuencial serían 900 ms; el test falla si pasa de 700.

```csharp
var results = await Task.WhenAll(
    _carriers.Select(c => QuoteFromAsync(c, package, zone, tracer, ct)));
```

Lo que sostiene eso en el resto del código:

| Decisión | Por qué |
|---|---|
| `async`/`await` de punta a punta | Ningún `.Result` ni `.Wait()`: nada bloquea un hilo del pool esperando I/O |
| `Task.Delay`, nunca `Thread.Sleep` | Esperar sin ocupar un hilo. La diferencia entre aguantar mil requests concurrentes y quedarse sin hilos |
| `CancellationToken` enhebrado hasta el adaptador | Si el cliente corta, se cortan las llamadas en vuelo. Hay un test que lo prueba con un carrier de 30 s |
| `CreateLinkedTokenSource` para el timeout | El timeout del contrato no pisa la cancelación del request entrante: respeta las dos |
| `lock` sobre el `Random` compartido | `Random` no es thread-safe y los tres carriers entran a la vez |
| Cada request con su propio `TraceRecorder` | Sin estado mutable compartido en el caso de uso. Un test lanza 40 requests concurrentes y verifica que ninguna traza se mezcle |
| `AsNoTracking` en las lecturas | El change tracker no guarda copias de filas que nadie va a modificar |

## Arquitectura

Cuatro proyectos, y las dependencias apuntan siempre hacia adentro:

```
ShippingQuote.Domain           sin dependencias — Package, Zone, FeePolicy, Trace
      ▲
ShippingQuote.Application      puertos y casos de uso
      ▲
ShippingQuote.Infrastructure   adaptadores secundarios — HTTP, EF Core
      ▲
ShippingQuote.Api              adaptador primario — controllers, middleware, DI
```

El dominio no conoce ASP.NET, ni EF Core, ni HTTP. `QuoteShippingUseCase` recibe
un `IEnumerable<ICarrierPort>` y no sabe cuántos transportistas hay ni que
hablan HTTP: sumar un cuarto es una entrada en `CarrierCatalog` y una línea en
el composition root.

**Composición sobre herencia.** Los tres transportistas son tres instancias de
`HttpCarrierAdapter`, no tres subclases. Cada uno aporta solo datos —su endpoint
y dos funciones de traducción—; el timeout, el manejo de errores y la traza
viven una sola vez.

**El middleware traduce el dominio a HTTP.** Que un bulto de más de 30 kg no se
cotice es una regla de negocio; que eso se comunique como un 422 es una decisión
de transporte. Por eso el mapeo vive en `DomainExceptionMiddleware` y no en un
`try/catch` por controller. Es Chain of Responsibility: cada middleware decide
si maneja el request o se lo pasa al siguiente.

**Plata en `decimal`, nunca `double`.** `decimal` es base 10, así que no arrastra
error de representación binaria. El redondeo es explícito con
`MidpointRounding.AwayFromZero`, porque el default de `Math.Round` es banker's
rounding y redondearía 2,5 a 2. Hay un test que se rompe si alguien lo saca.

## Tests

```bash
dotnet test
```

Dos proyectos, separados por lo que necesitan para correr:

| Proyecto | Qué cubre | Depende de |
|---|---|---|
| `ShippingQuote.Tests` | dominio, pipeline, adaptadores, concurrencia | nada externo |
| `ShippingQuote.Api.Tests` | la API de punta a punta | Docker (MySQL vía Testcontainers) |

La separación no es cosmética: los unit tests corren sin ASP.NET ni Docker, así
que son rápidos; los de integración levantan un MySQL 8.0 efímero que vive lo
que dura la corrida.

**Nada de bases falsas en memoria.** Los dos bugs más caros de este proyecto
—que SQLite no sabe ordenar por `DateTimeOffset`, y que se trababa con
escrituras concurrentes— solo aparecen cuando el motor es el que de verdad va a
estar del otro lado. Un doble en memoria los habría tapado.

Medición del paralelismo en la última corrida:

```
3 transportistas x 300ms de latencia   ->  306 ms   (en fila serían 900)
latencias de 50 / 150 / 400ms          ->  399 ms   (la suma sería 600)
```

Los de integración levantan la app real —mismo DI, mismo pipeline, mismos
controllers, mismas migraciones— contra MySQL en un contenedor. Lo único
sustituido son los transportistas, para que sean deterministas.

Los transportistas simulados se montan como un `HttpMessageHandler` propio: el
`HttpClient` hace un POST real, con serialización, status codes y
deserialización reales, pero nunca sale a la red. El adaptador que se testea es
el mismo binario que corre en producción.

## Correr

```bash
docker compose up --build
```

API en `:8080`, Swagger en `/swagger`, healthcheck en `/health`. El esquema se
aplica solo con las migraciones de EF Core al arrancar.

Sin Docker, contra un MySQL propio:

```bash
export ConnectionStrings__Quotes="Server=localhost;Database=shipping_quote;User=root;Password=..."
dotnet run --project src/ShippingQuote.Api
```

## Base de datos

MySQL, no SQLite. SQLite serializa las escrituras, así que un servicio que
existe para mostrar trabajo concurrente se trabaría justo en el único punto
donde escribe: con veinte requests en paralelo devolvía `database is locked`.

Lo que eso permite hacer bien:

- `decimal(12,2)` nativo para la plata, en vez de guardarla como texto
- `datetime(6)` con precisión de microsegundos, para que dos cotizaciones del
  mismo segundo se puedan ordenar entre sí
- `EnableRetryOnFailure`: un deadlock o un corte se reintenta solo en vez de
  volverse un 500
- Migraciones de EF Core, no `EnsureCreated`: `EnsureCreated` crea el esquema
  una vez y después no sabe evolucionarlo

La zona se guarda como texto y no como el entero del enum: si mañana se
reordenan los valores, las filas viejas siguen diciendo lo mismo.

## Stack

.NET 8 · ASP.NET Core · EF Core 8 + MySQL (Pomelo) · `IHttpClientFactory` ·
xUnit · Testcontainers · Docker
