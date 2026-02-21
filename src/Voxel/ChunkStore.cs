namespace Acreage.Voxel;

public sealed class ChunkStore
{
    private const byte CurrentVersion = 1;
    private readonly string _dir;

    public ChunkStore(string saveDirectory)
    {
        _dir = saveDirectory;
    }

    public void Save(DensityChunk chunk)
    {
        System.IO.Directory.CreateDirectory(_dir);
        var path = GetPath(chunk.Coord);
        using var stream = System.IO.File.Create(path);
        using var writer = new System.IO.BinaryWriter(stream);

        writer.Write(CurrentVersion);
        writer.Write(chunk.GridSizeX);
        writer.Write(chunk.GridSizeY);
        writer.Write(chunk.GridSizeZ);

        System.ReadOnlySpan<float> densitySpan = chunk.RawDensity;
        writer.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(densitySpan));

        System.ReadOnlySpan<MaterialType> materialSpan = chunk.RawMaterials;
        writer.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(materialSpan));

        chunk.IsDirty = false;
    }

    public DensityChunk? TryLoad(ChunkCoord coord, int sizeX, int sizeY, int sizeZ)
    {
        var path = GetPath(coord);
        if (!System.IO.File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = System.IO.File.OpenRead(path);
            using var reader = new System.IO.BinaryReader(stream);

            var version = reader.ReadByte();
            if (version != CurrentVersion)
            {
                return null;
            }

            var gridX = reader.ReadInt32();
            var gridY = reader.ReadInt32();
            var gridZ = reader.ReadInt32();

            if (gridX != sizeX + 1 || gridY != sizeY + 1 || gridZ != sizeZ + 1)
            {
                return null;
            }

            var chunk = new DensityChunk(coord, sizeX, sizeY, sizeZ);

            stream.ReadExactly(System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                chunk.RawDensity.AsSpan()));
            stream.ReadExactly(System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                chunk.RawMaterials.AsSpan()));

            return chunk;
        }
        catch (System.IO.IOException)
        {
            return null;
        }
    }

    private string GetPath(ChunkCoord coord)
    {
        return System.IO.Path.Combine(_dir, $"{coord.X}_{coord.Y}_{coord.Z}.chunk");
    }
}
