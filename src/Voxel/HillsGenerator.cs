namespace Acreage.Voxel;

public sealed class HillsGenerator : IChunkGenerator
{
    public bool UseRegionMap { get; set; }

    public HillsGenerator(bool useRegionMap = true)
    {
        UseRegionMap = useRegionMap;
    }

    public DensityChunk Generate(ChunkCoord coord, int sizeX, int sizeY, int sizeZ)
    {
        var chunk = new DensityChunk(coord, sizeX, sizeY, sizeZ);

        if (!UseRegionMap)
        {
            GenerateLegacy(chunk, coord, sizeX, sizeY, sizeZ);
            return chunk;
        }

        for (var gz = 0; gz <= sizeZ; gz++)
        {
            var worldZ = (coord.Z * sizeZ) + gz;
            for (var gx = 0; gx <= sizeX; gx++)
            {
                var worldX = (coord.X * sizeX) + gx;
                var region = SampleRegion(worldX, worldZ);

                for (var gy = 0; gy <= sizeY; gy++)
                {
                    var worldY = (coord.Y * sizeY) + gy;
                    var density = region.Height - worldY;

                    chunk.SetDensity(gx, gy, gz, density);
                    chunk.SetMaterial(gx, gy, gz, ChooseMaterial(worldY, region));
                }
            }
        }

        return chunk;
    }

    private static void GenerateLegacy(DensityChunk chunk, ChunkCoord coord, int sizeX, int sizeY, int sizeZ)
    {
        for (var gy = 0; gy <= sizeY; gy++)
        {
            var worldY = (coord.Y * sizeY) + gy;
            for (var gz = 0; gz <= sizeZ; gz++)
            {
                var worldZ = (coord.Z * sizeZ) + gz;
                for (var gx = 0; gx <= sizeX; gx++)
                {
                    var worldX = (coord.X * sizeX) + gx;

                    var height = ComputeLegacyHeight(worldX, worldZ);
                    var density = height - worldY;

                    chunk.SetDensity(gx, gy, gz, density);
                    chunk.SetMaterial(gx, gy, gz, ChooseLegacyMaterial(worldY, height));
                }
            }
        }
    }

    private static float ComputeLegacyHeight(float worldX, float worldZ)
    {
        var large = SimplexNoise.Evaluate(worldX * 0.008f, worldZ * 0.008f) * 12f;
        var medium = SimplexNoise.Evaluate(worldX * 0.03f, worldZ * 0.03f) * 3f;
        var small = SimplexNoise.Evaluate(worldX * 0.1f, worldZ * 0.1f) * 0.8f;
        return 12f + large + medium + small;
    }

    private static MaterialType ChooseLegacyMaterial(float worldY, float surfaceHeight)
    {
        var depth = surfaceHeight - worldY;
        if (depth < 0f)
        {
            return MaterialType.Air;
        }

        if (depth < 1.5f)
        {
            return MaterialType.Grass;
        }

        if (depth < 6f)
        {
            return MaterialType.Dirt;
        }

        return MaterialType.Stone;
    }

    private static RegionSample SampleRegion(float worldX, float worldZ)
    {
        // Macro region controls broad elevation bands and silhouette.
        var macroBase = SimplexNoise.Evaluate(worldX * 0.0012f, worldZ * 0.0012f) * 24f;
        var macroShape = (1f - System.MathF.Abs(SimplexNoise.Evaluate(worldX * 0.002f, worldZ * 0.002f))) * 16f;

        // Domain warp to break up uniform rolling-noise patterns.
        var warpX = SimplexNoise.Evaluate((worldX + 173f) * 0.0028f, (worldZ - 611f) * 0.0028f) * 34f;
        var warpZ = SimplexNoise.Evaluate((worldX - 487f) * 0.0028f, (worldZ + 239f) * 0.0028f) * 34f;
        var sx = worldX + warpX;
        var sz = worldZ + warpZ;

        // Region map channels to support biome-like variation.
        var moisture = ToUnit(SimplexNoise.Evaluate((worldX + 4000f) * 0.0009f, (worldZ - 3000f) * 0.0009f));
        var temperature = ToUnit(SimplexNoise.Evaluate((worldX - 8000f) * 0.0008f, (worldZ + 6000f) * 0.0008f));
        var ruggedness = ToUnit(SimplexNoise.Evaluate((worldX + 1200f) * 0.0014f, (worldZ + 900f) * 0.0014f));

        // Soft biome weights.
        var wetlandWeight = SmoothStep(0.55f, 0.8f, moisture) * (1f - SmoothStep(0.7f, 0.95f, ruggedness));
        var drylandWeight = SmoothStep(0.55f, 0.85f, temperature) * (1f - SmoothStep(0.45f, 0.7f, moisture));
        var highlandWeight = SmoothStep(0.55f, 0.9f, ruggedness);
        var grasslandWeight = Clamp01(1f - System.MathF.Max(wetlandWeight, System.MathF.Max(drylandWeight, highlandWeight)));

        var ampLarge = (grasslandWeight * 10f) + (wetlandWeight * 6f) + (drylandWeight * 8f) + (highlandWeight * 14f);
        var ampMedium = (grasslandWeight * 3.5f) + (wetlandWeight * 2f) + (drylandWeight * 3f) + (highlandWeight * 6.5f);
        var ampSmall = (grasslandWeight * 0.9f) + (wetlandWeight * 0.5f) + (drylandWeight * 1.1f) + (highlandWeight * 2f);

        var large = SimplexNoise.Evaluate(sx * 0.006f, sz * 0.006f) * ampLarge;
        var medium = SimplexNoise.Evaluate(sx * 0.02f, sz * 0.02f) * ampMedium;
        var small = SimplexNoise.Evaluate(sx * 0.08f, sz * 0.08f) * ampSmall;

        var baseHeight = 16f + macroBase + macroShape;
        var height = baseHeight + large + medium + small;

        // Gentle wetland flattening.
        var wetFlatten = SmoothStep(0.45f, 0.8f, wetlandWeight);
        height = Lerp(height, 10f + (macroBase * 0.35f), wetFlatten * 0.35f);

        // Highland uplift and slight terracing.
        var highlandLift = SmoothStep(0.5f, 0.85f, highlandWeight);
        height += highlandLift * 10f;
        var terraced = System.MathF.Round(height / 4f) * 4f;
        height = Lerp(height, terraced, highlandLift * 0.2f);

        return new RegionSample(height, moisture, temperature, ruggedness);
    }

    private static MaterialType ChooseMaterial(float worldY, RegionSample region)
    {
        var depth = region.Height - worldY;
        if (depth < 0f)
        {
            return MaterialType.Air;
        }

        if (depth < 1.2f)
        {
            return ChooseSurfaceMaterial(region);
        }

        if (depth < 5f)
        {
            // Drier/higher-rugged regions transition toward rock quicker.
            if (region.Ruggedness > 0.7f || (region.Temperature > 0.65f && region.Moisture < 0.4f))
            {
                return MaterialType.Stone;
            }

            return MaterialType.Dirt;
        }

        return MaterialType.Stone;
    }

    private static MaterialType ChooseSurfaceMaterial(RegionSample region)
    {
        if (region.Ruggedness > 0.78f)
        {
            return MaterialType.Stone;
        }

        if (region.Moisture > 0.42f)
        {
            return MaterialType.Grass;
        }

        return MaterialType.Dirt;
    }

    private static float ToUnit(float noise)
    {
        return Clamp01((noise * 0.5f) + 0.5f);
    }

    private static float Clamp01(float v)
    {
        if (v < 0f)
        {
            return 0f;
        }

        return v > 1f ? 1f : v;
    }

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        if (edge1 <= edge0)
        {
            return x < edge0 ? 0f : 1f;
        }

        var t = Clamp01((x - edge0) / (edge1 - edge0));
        return t * t * (3f - (2f * t));
    }

    private static float Lerp(float a, float b, float t)
    {
        return a + ((b - a) * Clamp01(t));
    }

    private readonly record struct RegionSample(float Height, float Moisture, float Temperature, float Ruggedness);
}
