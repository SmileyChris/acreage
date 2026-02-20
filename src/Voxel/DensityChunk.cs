namespace Acreage.Voxel;

public sealed class DensityChunk
{
    private readonly float[] _density;
    private readonly MaterialType[] _materials;

    public DensityChunk(ChunkCoord coord, int sizeX = 16, int sizeY = 64, int sizeZ = 16)
    {
        if (sizeX <= 0 || sizeY <= 0 || sizeZ <= 0)
        {
            throw new System.ArgumentOutOfRangeException(nameof(sizeX), "Chunk sizes must be positive.");
        }

        Coord = coord;
        SizeX = sizeX;
        SizeY = sizeY;
        SizeZ = sizeZ;
        GridSizeX = sizeX + 1;
        GridSizeY = sizeY + 1;
        GridSizeZ = sizeZ + 1;

        var length = GridSizeX * GridSizeY * GridSizeZ;
        _density = new float[length];
        _materials = new MaterialType[length];
    }

    public ChunkCoord Coord { get; }

    public int SizeX { get; }
    public int SizeY { get; }
    public int SizeZ { get; }

    public int GridSizeX { get; }
    public int GridSizeY { get; }
    public int GridSizeZ { get; }

    public float GetDensity(int gx, int gy, int gz)
    {
        if (!InBounds(gx, gy, gz))
        {
            return 0f;
        }

        return _density[ToIndex(gx, gy, gz)];
    }

    public void SetDensity(int gx, int gy, int gz, float value)
    {
        if (!InBounds(gx, gy, gz))
        {
            throw new System.ArgumentOutOfRangeException(nameof(gx), "Grid coordinate is outside chunk bounds.");
        }

        _density[ToIndex(gx, gy, gz)] = value;
    }

    public MaterialType GetMaterial(int gx, int gy, int gz)
    {
        if (!InBounds(gx, gy, gz))
        {
            return MaterialType.Air;
        }

        return _materials[ToIndex(gx, gy, gz)];
    }

    public void SetMaterial(int gx, int gy, int gz, MaterialType type)
    {
        if (!InBounds(gx, gy, gz))
        {
            throw new System.ArgumentOutOfRangeException(nameof(gx), "Grid coordinate is outside chunk bounds.");
        }

        _materials[ToIndex(gx, gy, gz)] = type;
    }

    public bool InBounds(int gx, int gy, int gz)
    {
        return gx >= 0 && gx < GridSizeX && gy >= 0 && gy < GridSizeY && gz >= 0 && gz < GridSizeZ;
    }

    private int ToIndex(int gx, int gy, int gz)
    {
        return gx + (GridSizeX * (gz + (GridSizeZ * gy)));
    }
}
