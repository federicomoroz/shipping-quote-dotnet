using ShippingQuote.Domain;

namespace ShippingQuote.Tests;

public class PackageTests
{
    [Fact]
    public void PesoVolumetrico_EsElVolumenSobreElDivisorEstandar()
    {
        var package = Package.Create(1.0, 50, 40, 30);

        // 50 * 40 * 30 = 60.000 cm3 / 5.000 = 12 kg
        Assert.Equal(12.0, package.VolumetricWeightKg, precision: 6);
    }

    [Fact]
    public void PesoEfectivo_EsElVolumetricoCuandoElBultoEsGrandeYLiviano()
    {
        var package = Package.Create(1.0, 50, 40, 30);

        Assert.Equal(12.0, package.EffectiveWeightKg, precision: 6);
    }

    [Fact]
    public void PesoEfectivo_EsElRealCuandoElBultoEsChicoYPesado()
    {
        var package = Package.Create(20.0, 10, 10, 10);

        // Volumetrico = 1.000 / 5.000 = 0,2 kg. Manda el real.
        Assert.Equal(20.0, package.EffectiveWeightKg, precision: 6);
    }

    [Fact]
    public void UnBultoQueSuperaElMaximoNoSePuedeConstruir()
    {
        var exc = Assert.Throws<PackageTooHeavyException>(() => Package.Create(35.0, 10, 10, 10));

        // El mensaje explica ambos pesos: quien lo lee tiene que poder saber
        // cual de los dos lo dejo afuera.
        Assert.Contains("35", exc.Message);
        Assert.Contains("30", exc.Message);
    }

    [Fact]
    public void ElLimiteSeRechazaPorVolumenAunqueElPesoRealSeaMinimo()
    {
        // 100 x 100 x 100 = 1.000.000 / 5.000 = 200 kg volumetricos.
        Assert.Throws<PackageTooHeavyException>(() => Package.Create(0.5, 100, 100, 100));
    }

    [Fact]
    public void ExactamenteElMaximoSeAcepta()
    {
        var package = Package.Create(30.0, 10, 10, 10);

        Assert.Equal(30.0, package.EffectiveWeightKg, precision: 6);
    }
}

public class ZoneClassifierTests
{
    [Theory]
    [InlineData(1000, Zone.Amba)]      // primer CP de CABA
    [InlineData(1425, Zone.Amba)]
    [InlineData(1499, Zone.Amba)]      // ultimo de CABA
    [InlineData(1500, Zone.Interior)]  // primero del hueco de interior
    [InlineData(1600, Zone.Amba)]      // GBA vuelve a ser AMBA
    [InlineData(1899, Zone.Amba)]
    [InlineData(1900, Zone.Interior)]
    [InlineData(8299, Zone.Interior)]
    [InlineData(8300, Zone.Patagonia)]
    [InlineData(9420, Zone.Patagonia)]
    [InlineData(9421, Zone.Interior)]  // despues de Patagonia vuelve interior
    [InlineData(9999, Zone.Interior)]
    public void ClasificaCadaBordeDeLaTabla(int postalCode, Zone expected)
    {
        Assert.Equal(expected, ZoneClassifier.Classify(postalCode));
    }

    [Theory]
    [InlineData(999)]
    [InlineData(10000)]
    [InlineData(0)]
    public void UnCodigoFueraDeRangoFalla(int postalCode)
    {
        Assert.Throws<InvalidPostalCodeException>(() => ZoneClassifier.Classify(postalCode));
    }
}

public class FeePolicyTests
{
    [Theory]
    [InlineData(1.0, WeightBracket.Liviano)]
    [InlineData(5.0, WeightBracket.Liviano)]   // el limite entra en el bracket
    [InlineData(5.1, WeightBracket.Medio)]
    [InlineData(15.0, WeightBracket.Medio)]
    [InlineData(15.1, WeightBracket.Pesado)]
    [InlineData(30.0, WeightBracket.Pesado)]
    public void ElBracketSeEligePorPesoEfectivo(double weightKg, WeightBracket expected)
    {
        Assert.Equal(expected, FeePolicy.BracketFor(weightKg));
    }

    [Fact]
    public void AmbaLivianoAplicaSeisPorCiento()
    {
        // 1000 * 1,06 = 1060
        Assert.Equal(1060m, FeePolicy.Apply(1000m, Zone.Amba, 3.0));
    }

    [Fact]
    public void PatagoniaPesadoAplicaVeinticincoPorCiento()
    {
        // 1000 * 1,25 = 1250
        Assert.Equal(1250m, FeePolicy.Apply(1000m, Zone.Patagonia, 20.0));
    }

    [Fact]
    public void ElResultadoSeRedondeaAPesosEnteros()
    {
        var result = FeePolicy.Apply(1234.56m, Zone.Interior, 3.0);

        Assert.Equal(decimal.Round(result, 0), result);
    }

    [Fact]
    public void ElMedioSeRedondeaHaciaArriba_NoConBankersRounding()
    {
        // 0,5 exacto. Math.Round por defecto haria 2 (al par mas cercano);
        // la politica pide 3. Este test es el que se rompe si alguien saca
        // MidpointRounding.AwayFromZero.
        // 2,5 / 1,25 = 2m con markup Patagonia pesado -> exactamente 2,5
        var result = FeePolicy.Apply(2m, Zone.Patagonia, 20.0);

        Assert.Equal(3m, result);
    }

    [Fact]
    public void MasZonaYMasPesoNuncaAbaratan()
    {
        var ambaLiviano = FeePolicy.Apply(1000m, Zone.Amba, 3.0);
        var interiorMedio = FeePolicy.Apply(1000m, Zone.Interior, 10.0);
        var patagoniaPesado = FeePolicy.Apply(1000m, Zone.Patagonia, 20.0);

        Assert.True(ambaLiviano < interiorMedio);
        Assert.True(interiorMedio < patagoniaPesado);
    }
}

public class TraceRecorderTests
{
    [Fact]
    public void RegistraCadaHopEnOrden()
    {
        var tracer = new TraceRecorder();

        tracer.Mark("entrada", "HTTP", "uno");
        tracer.Mark("dominio", "Regla", "dos");

        Assert.Equal(2, tracer.Entries.Count);
        Assert.Equal("uno", tracer.Entries[0].Detail);
        Assert.Equal("dos", tracer.Entries[1].Detail);
    }

    [Fact]
    public void ElTiempoTranscurridoNoRetrocede()
    {
        var tracer = new TraceRecorder();

        tracer.Mark("a", "a", "a");
        tracer.Mark("b", "b", "b");

        Assert.True(tracer.Entries[1].ElapsedMs >= tracer.Entries[0].ElapsedMs);
    }
}
