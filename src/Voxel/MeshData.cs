namespace Acreage.Voxel;

public sealed class MeshData
{
    public System.Collections.Generic.List<Vector3f> Vertices { get; } = new();

    public System.Collections.Generic.List<Vector3f> Normals { get; } = new();

    public System.Collections.Generic.List<Vector2f> Uvs { get; } = new();

    public System.Collections.Generic.List<int> Indices { get; } = new();

    public System.Collections.Generic.List<Vector3f> Colors { get; } = new();
}

public readonly struct Vector3f
{
    public Vector3f(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public float X { get; }
    public float Y { get; }
    public float Z { get; }

    public static Vector3f operator +(Vector3f a, Vector3f b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static Vector3f operator *(float scalar, Vector3f v) => new(scalar * v.X, scalar * v.Y, scalar * v.Z);
}

public readonly struct Vector2f
{
    public Vector2f(float x, float y)
    {
        X = x;
        Y = y;
    }

    public float X { get; }
    public float Y { get; }
}
