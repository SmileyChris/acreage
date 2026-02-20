namespace Acreage.Voxel;

public readonly struct ChunkCoord : System.IEquatable<ChunkCoord>
{
    public ChunkCoord(int x, int y, int z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public int X { get; }
    public int Y { get; }
    public int Z { get; }

    public bool Equals(ChunkCoord other) => X == other.X && Y == other.Y && Z == other.Z;

    public override bool Equals(object? obj) => obj is ChunkCoord other && Equals(other);

    public override int GetHashCode() => System.HashCode.Combine(X, Y, Z);

    public static bool operator ==(ChunkCoord left, ChunkCoord right) => left.Equals(right);

    public static bool operator !=(ChunkCoord left, ChunkCoord right) => !(left == right);

    public override string ToString() => $"({X}, {Y}, {Z})";
}
