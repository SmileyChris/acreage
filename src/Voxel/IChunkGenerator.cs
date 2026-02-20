namespace Acreage.Voxel;

public interface IChunkGenerator
{
    DensityChunk Generate(ChunkCoord coord, int sizeX, int sizeY, int sizeZ);
}
