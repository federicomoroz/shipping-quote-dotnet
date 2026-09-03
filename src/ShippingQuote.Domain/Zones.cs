namespace ShippingQuote.Domain;

/// <summary>
/// Zonas logisticas. Es la unica fuente de verdad de los nombres validos:
/// el resto del codigo referencia este enum en vez de repetir strings.
/// </summary>
public enum Zone
{
    Amba,
    Interior,
    Patagonia,
}

public static class ZoneNames
{
    /// <summary>
    /// El nombre con el que la zona viaja por HTTP hacia los transportistas.
    /// Se separa del enum porque el vocabulario externo no tiene por que
    /// coincidir con el interno, y si cambia no deberia obligar a renombrar
    /// un tipo del dominio.
    /// </summary>
    public static string Wire(this Zone zone) => zone switch
    {
        Zone.Amba => "AMBA",
        Zone.Interior => "Interior",
        Zone.Patagonia => "Patagonia",
        _ => throw new ArgumentOutOfRangeException(nameof(zone), zone, "zona sin nombre externo definido"),
    };
}

/// <summary>
/// Clasificacion de codigo postal a zona. Tabla simplificada con fines de demo,
/// pero data-driven: sumar o corregir una zona es una fila, no una rama de if.
/// </summary>
public static class ZoneClassifier
{
    private static readonly (int Start, int End, Zone Zone)[] Table =
    new (int, int, Zone)[]
    {
        (1000, 1499, Zone.Amba),      // CABA
        (1500, 1599, Zone.Interior),
        (1600, 1899, Zone.Amba),      // GBA
        (1900, 8299, Zone.Interior),
        (8300, 9420, Zone.Patagonia),
        (9421, 9999, Zone.Interior),
    };

    public static int MinPostalCode => Table[0].Start;

    public static int MaxPostalCode => Table[^1].End;

    public static Zone Classify(int postalCode)
    {
        foreach (var (start, end, zone) in Table)
        {
            if (postalCode >= start && postalCode <= end)
            {
                return zone;
            }
        }

        throw new InvalidPostalCodeException(
            $"codigo postal {postalCode} fuera de rango ({MinPostalCode}-{MaxPostalCode})");
    }
}

public sealed class InvalidPostalCodeException(string message) : Exception(message);
