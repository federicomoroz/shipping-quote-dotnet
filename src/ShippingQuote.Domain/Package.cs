namespace ShippingQuote.Domain;

/// <summary>
/// Un bulto a despachar. El peso que se cobra no es el que marca la balanza:
/// es el mayor entre el peso real y el volumetrico, porque un bulto liviano y
/// enorme ocupa lugar en el camion igual que uno pesado y chico.
/// </summary>
public sealed record Package
{
    public const double MaxEffectiveWeightKg = 30.0;

    /// <summary>Centimetros cubicos por kilo. Divisor estandar de la industria.</summary>
    public const double VolumetricDivisor = 5000.0;

    public double WeightKg { get; }
    public double LengthCm { get; }
    public double WidthCm { get; }
    public double HeightCm { get; }

    private Package(double weightKg, double lengthCm, double widthCm, double heightCm)
    {
        WeightKg = weightKg;
        LengthCm = lengthCm;
        WidthCm = widthCm;
        HeightCm = heightCm;
    }

    public double VolumetricWeightKg => LengthCm * WidthCm * HeightCm / VolumetricDivisor;

    public double EffectiveWeightKg => Math.Max(WeightKg, VolumetricWeightKg);

    /// <summary>
    /// Unica puerta de entrada al tipo: el constructor es privado, asi que no
    /// existe forma de tener un Package que viole la invariante de peso.
    /// </summary>
    public static Package Create(double weightKg, double lengthCm, double widthCm, double heightCm)
    {
        var package = new Package(weightKg, lengthCm, widthCm, heightCm);
        if (package.EffectiveWeightKg > MaxEffectiveWeightKg)
        {
            throw new PackageTooHeavyException(
                $"peso efectivo {package.EffectiveWeightKg:F1}kg supera el maximo " +
                $"de {MaxEffectiveWeightKg:F0}kg (real {weightKg:F1}kg, " +
                $"volumetrico {package.VolumetricWeightKg:F1}kg)");
        }

        return package;
    }
}

public sealed class PackageTooHeavyException(string message) : Exception(message);
