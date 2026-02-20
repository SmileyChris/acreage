namespace Acreage.Voxel;

public static class MarchingCubesMesher
{
    public delegate float DensitySampler(int gx, int gy, int gz);
    public delegate MaterialType MaterialSampler(int gx, int gy, int gz);

    public static MeshData Build(
        DensityChunk chunk,
        DensitySampler? densitySampler = null,
        MaterialSampler? materialSampler = null,
        int step = 1)
    {
        var mesh = new MeshData();
        var sizeX = chunk.SizeX;
        var sizeY = chunk.SizeY;
        var sizeZ = chunk.SizeZ;

        // Cache for edge-interpolated vertex indices to avoid duplicates.
        // Key: encoded edge position, Value: vertex index in mesh.
        var edgeVertexCache = new System.Collections.Generic.Dictionary<long, int>();

        var cornerDensity = new float[8];
        var cornerPos = new int[8, 3];

        for (var y = 0; y < sizeY; y += step)
        {
            for (var z = 0; z < sizeZ; z += step)
            {
                for (var x = 0; x < sizeX; x += step)
                {
                    // Sample 8 corners of the cube.
                    var cubeIndex = 0;
                    for (var c = 0; c < 8; c++)
                    {
                        var gx = x + MarchingCubesTables.CornerOffsets[c, 0] * step;
                        var gy = y + MarchingCubesTables.CornerOffsets[c, 1] * step;
                        var gz = z + MarchingCubesTables.CornerOffsets[c, 2] * step;
                        cornerPos[c, 0] = gx;
                        cornerPos[c, 1] = gy;
                        cornerPos[c, 2] = gz;
                        cornerDensity[c] = SampleDensity(chunk, densitySampler, gx, gy, gz);

                        if (cornerDensity[c] > 0f)
                        {
                            cubeIndex |= 1 << c;
                        }
                    }

                    var edgeMask = MarchingCubesTables.EdgeTable[cubeIndex];
                    if (edgeMask == 0)
                    {
                        continue;
                    }

                    // Interpolate vertices on active edges.
                    var edgeVertices = new int[12];
                    for (var e = 0; e < 12; e++)
                    {
                        if ((edgeMask & (1 << e)) == 0)
                        {
                            continue;
                        }

                        var c0 = MarchingCubesTables.EdgeConnections[e, 0];
                        var c1 = MarchingCubesTables.EdgeConnections[e, 1];

                        var edgeKey = EncodeEdge(
                            cornerPos[c0, 0], cornerPos[c0, 1], cornerPos[c0, 2],
                            cornerPos[c1, 0], cornerPos[c1, 1], cornerPos[c1, 2]);

                        if (edgeVertexCache.TryGetValue(edgeKey, out var cachedIndex))
                        {
                            edgeVertices[e] = cachedIndex;
                            continue;
                        }

                        var d0 = cornerDensity[c0];
                        var d1 = cornerDensity[c1];
                        var t = d0 / (d0 - d1);
                        if (t < 0f) t = 0f;
                        if (t > 1f) t = 1f;

                        var px = cornerPos[c0, 0] + t * (cornerPos[c1, 0] - cornerPos[c0, 0]);
                        var py = cornerPos[c0, 1] + t * (cornerPos[c1, 1] - cornerPos[c0, 1]);
                        var pz = cornerPos[c0, 2] + t * (cornerPos[c1, 2] - cornerPos[c0, 2]);

                        // Normal via interpolated gradient.
                        var n0 = ComputeGradient(chunk, densitySampler, cornerPos[c0, 0], cornerPos[c0, 1], cornerPos[c0, 2]);
                        var n1 = ComputeGradient(chunk, densitySampler, cornerPos[c1, 0], cornerPos[c1, 1], cornerPos[c1, 2]);
                        var nx = n0.X + t * (n1.X - n0.X);
                        var ny = n0.Y + t * (n1.Y - n0.Y);
                        var nz = n0.Z + t * (n1.Z - n0.Z);
                        var nLen = System.MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                        if (nLen > 1e-6f)
                        {
                            nx /= nLen;
                            ny /= nLen;
                            nz /= nLen;
                        }

                        // Negate gradient so normal points from solid toward air.
                        nx = -nx;
                        ny = -ny;
                        nz = -nz;

                        // Material: round the interpolated surface position toward
                        // the solid corner. This gives the nearest solid grid point
                        // to the surface, avoiding deep-underground materials at
                        // high LOD steps while staying on the solid side.
                        var solidC = d0 > 0f ? c0 : c1;
                        var airC = d0 > 0f ? c1 : c0;
                        var mx = cornerPos[solidC, 0] <= cornerPos[airC, 0]
                            ? (int)System.MathF.Floor(px) : (int)System.MathF.Ceiling(px);
                        var my = cornerPos[solidC, 1] <= cornerPos[airC, 1]
                            ? (int)System.MathF.Floor(py) : (int)System.MathF.Ceiling(py);
                        var mz = cornerPos[solidC, 2] <= cornerPos[airC, 2]
                            ? (int)System.MathF.Floor(pz) : (int)System.MathF.Ceiling(pz);
                        var mat = SampleMaterial(chunk, materialSampler, mx, my, mz);

                        var vertIndex = mesh.Vertices.Count;
                        mesh.Vertices.Add(new Vector3f(px, py, pz));
                        mesh.Normals.Add(new Vector3f(nx, ny, nz));
                        mesh.Uvs.Add(new Vector2f(px, pz));
                        mesh.Colors.Add(MaterialToColor(mat));

                        edgeVertices[e] = vertIndex;
                        edgeVertexCache[edgeKey] = vertIndex;
                    }

                    // Emit triangles from the tri table.
                    for (var t = 0; t < 16; t += 3)
                    {
                        var e0 = MarchingCubesTables.TriTable[cubeIndex, t];
                        if (e0 == -1)
                        {
                            break;
                        }

                        var e1 = MarchingCubesTables.TriTable[cubeIndex, t + 1];
                        var e2 = MarchingCubesTables.TriTable[cubeIndex, t + 2];

                        mesh.Indices.Add(edgeVertices[e0]);
                        mesh.Indices.Add(edgeVertices[e2]);
                        mesh.Indices.Add(edgeVertices[e1]);
                    }
                }
            }
        }

        return mesh;
    }

    private static float SampleDensity(DensityChunk chunk, DensitySampler? sampler, int gx, int gy, int gz)
    {
        if (chunk.InBounds(gx, gy, gz))
        {
            return chunk.GetDensity(gx, gy, gz);
        }

        return sampler?.Invoke(gx, gy, gz) ?? 0f;
    }

    private static MaterialType SampleMaterial(DensityChunk chunk, MaterialSampler? sampler, int gx, int gy, int gz)
    {
        if (chunk.InBounds(gx, gy, gz))
        {
            return chunk.GetMaterial(gx, gy, gz);
        }

        return sampler?.Invoke(gx, gy, gz) ?? MaterialType.Air;
    }

    private static Vector3f ComputeGradient(DensityChunk chunk, DensitySampler? sampler, int gx, int gy, int gz)
    {
        var dx = SampleDensity(chunk, sampler, gx + 1, gy, gz) - SampleDensity(chunk, sampler, gx - 1, gy, gz);
        var dy = SampleDensity(chunk, sampler, gx, gy + 1, gz) - SampleDensity(chunk, sampler, gx, gy - 1, gz);
        var dz = SampleDensity(chunk, sampler, gx, gy, gz + 1) - SampleDensity(chunk, sampler, gx, gy, gz - 1);
        return new Vector3f(dx, dy, dz);
    }

    private static long EncodeEdge(int ax, int ay, int az, int bx, int by, int bz)
    {
        // Ensure consistent ordering so both directions map to the same key.
        if (ax > bx || (ax == bx && ay > by) || (ax == bx && ay == by && az > bz))
        {
            (ax, bx) = (bx, ax);
            (ay, by) = (by, ay);
            (az, bz) = (bz, az);
        }

        // Pack two grid positions into a single long.
        // Each coordinate fits in 10 bits (0-1024), so 30 bits per point.
        return ((long)ax & 0x3FF)
             | (((long)ay & 0x3FF) << 10)
             | (((long)az & 0x3FF) << 20)
             | (((long)bx & 0x3FF) << 30)
             | (((long)by & 0x3FF) << 40)
             | (((long)bz & 0x3FF) << 50);
    }

    private static Vector3f MaterialToColor(MaterialType mat)
    {
        return mat switch
        {
            MaterialType.Grass => new Vector3f(0.35f, 0.55f, 0.25f),
            MaterialType.Dirt => new Vector3f(0.55f, 0.36f, 0.20f),
            MaterialType.Stone => new Vector3f(0.50f, 0.50f, 0.50f),
            MaterialType.Concrete => new Vector3f(0.72f, 0.72f, 0.70f),
            _ => new Vector3f(1f, 1f, 1f),
        };
    }
}
