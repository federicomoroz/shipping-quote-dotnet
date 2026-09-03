namespace ShippingQuote.Domain;

public enum WeightBracket
{
    Liviano,
    Medio,
    Pesado,
}

/// <summary>
/// Politica de comision propia sobre la tarifa que devuelve el transportista.
///
/// La tarifa del carrier es un precio ajeno; esta es la unica parte del precio
/// final que decide nuestro dominio. Se opera en <see cref="decimal"/> y no en
/// double porque es plata: decimal es base 10, asi que 0.1 + 0.2 da exactamente
/// 0.3, y el redondeo se hace explicito con MidpointRounding.AwayFromZero en
/// lugar del "banker's rounding" que Math.Round aplica por defecto.
/// </summary>
public static class FeePolicy
{
    private static readonly (double Limit, WeightBracket Bracket)[] Brackets =
    new (double, WeightBracket)[]
    {
        (5.0, WeightBracket.Liviano),
        (15.0, WeightBracket.Medio),
        (30.0, WeightBracket.Pesado),
    };

    private static readonly Dictionary<(Zone, WeightBracket), decimal> FeeTable = new()
    {
        [(Zone.Amba, WeightBracket.Liviano)] = 0.06m,
        [(Zone.Amba, WeightBracket.Medio)] = 0.08m,
        [(Zone.Amba, WeightBracket.Pesado)] = 0.10m,
        [(Zone.Interior, WeightBracket.Liviano)] = 0.10m,
        [(Zone.Interior, WeightBracket.Medio)] = 0.13m,
        [(Zone.Interior, WeightBracket.Pesado)] = 0.16m,
        [(Zone.Patagonia, WeightBracket.Liviano)] = 0.15m,
        [(Zone.Patagonia, WeightBracket.Medio)] = 0.20m,
        [(Zone.Patagonia, WeightBracket.Pesado)] = 0.25m,
    };

    public static WeightBracket BracketFor(double effectiveWeightKg)
    {
        foreach (var (limit, bracket) in Brackets)
        {
            if (effectiveWeightKg <= limit)
            {
                return bracket;
            }
        }

        // Inalcanzable mientras Package.Create siga rechazando > 30kg. Si esto
        // dispara, la invariante de dominio se rompio en otro lado.
        throw new InvalidOperationException(
            $"peso efectivo {effectiveWeightKg}kg no entra en ningun bracket definido");
    }

    public static decimal Apply(decimal carrierAmountArs, Zone zone, double effectiveWeightKg)
    {
        var markup = FeeTable[(zone, BracketFor(effectiveWeightKg))];
        var finalAmount = carrierAmountArs * (1m + markup);

        // Se cotiza en pesos enteros, sin centavos.
        return Math.Round(finalAmount, 0, MidpointRounding.AwayFromZero);
    }
}
