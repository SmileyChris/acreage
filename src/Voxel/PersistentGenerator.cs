namespace Acreage.Voxel;

public sealed class PersistentGenerator : IChunkGenerator
{
    private readonly IChunkGenerator _inner;
    private readonly ChunkStore _store;

    public PersistentGenerator(IChunkGenerator inner, ChunkStore store)
    {
        _inner = inner;
        _store = store;
    }

    public DensityChunk Generate(ChunkCoord coord, int sizeX, int sizeY, int sizeZ)
    {
        var loaded = _store.TryLoad(coord, sizeX, sizeY, sizeZ);
        if (loaded is not null)
        {
            return loaded;
        }

        return _inner.Generate(coord, sizeX, sizeY, sizeZ);
    }
}
