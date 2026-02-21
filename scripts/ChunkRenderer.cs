using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using Acreage.Voxel;
using Godot;

public sealed class ChunkRenderer
{
    private const double MeshUploadBudgetMs = 3.0;
    private const int MaxMeshDrainsPerFrame = 64;
    private const double CollisionBudgetMs = 1.5;
    private const int MaxCollisionUpdatesPerFrame = 2;

    private readonly int _chunkSizeX;
    private readonly int _chunkSizeY;
    private readonly int _chunkSizeZ;
    private readonly Node3D _chunkRoot;
    private readonly StandardMaterial3D _terrainMaterial;

    private readonly ConcurrentDictionary<ChunkCoord, PendingChunk> _pendingChunks = new();
    private readonly Dictionary<ChunkCoord, DensityChunk> _chunks = new();
    private readonly Dictionary<ChunkCoord, MeshInstance3D> _chunkMeshes = new();
    private readonly Dictionary<ChunkCoord, StaticBody3D> _chunkBodies = new();
    private readonly Dictionary<ChunkCoord, int> _chunkLodSteps = new();
    private readonly Dictionary<int, StandardMaterial3D> _lodOverlayMaterials = new();
    private readonly ConcurrentDictionary<ChunkCoord, CollisionUpdate> _pendingCollisionUpdates = new();
    private int _pruneCounter;

    public ChunkRenderer(Node3D chunkRoot, StandardMaterial3D terrainMaterial, int chunkSizeX, int chunkSizeY, int chunkSizeZ)
    {
        _chunkRoot = chunkRoot;
        _terrainMaterial = terrainMaterial;
        _chunkSizeX = chunkSizeX;
        _chunkSizeY = chunkSizeY;
        _chunkSizeZ = chunkSizeZ;
    }

    public bool EnableCollisionUpdates { get; set; } = true;
    public bool ShowLodOverlay { get; set; }

    public int LastDrainedChunks { get; private set; }
    public double LastMeshUploadMs { get; private set; }
    public int LastDrainedCollisions { get; private set; }
    public double LastCollisionMs { get; private set; }
    public int PendingChunkCount => _pendingChunks.Count;
    public int PendingCollisionCount => _pendingCollisionUpdates.Count;

    public Dictionary<ChunkCoord, DensityChunk> Chunks => _chunks;

    public void OnChunkMeshed(DensityChunk chunk, MeshData mesh, int lodStep)
    {
        _pendingChunks[chunk.Coord] = new PendingChunk(chunk, mesh, lodStep);
    }

    public void DrainPendingChunks()
    {
        var drained = 0;
        var stopwatch = Stopwatch.StartNew();
        foreach (var (coord, pending) in _pendingChunks)
        {
            if (drained >= MaxMeshDrainsPerFrame)
            {
                break;
            }

            if (stopwatch.Elapsed.TotalMilliseconds >= MeshUploadBudgetMs)
            {
                break;
            }

            if (!_pendingChunks.TryRemove(coord, out _))
            {
                continue;
            }

            _chunks[pending.Chunk.Coord] = pending.Chunk;
            _chunkLodSteps[pending.Chunk.Coord] = pending.LodStep;
            UpsertChunkMesh(pending.Chunk.Coord, pending.Mesh, pending.LodStep);
            drained++;
        }

        stopwatch.Stop();
        LastDrainedChunks = drained;
        LastMeshUploadMs = stopwatch.Elapsed.TotalMilliseconds;
    }

    public void DrainPendingCollisionUpdates()
    {
        if (!EnableCollisionUpdates)
        {
            LastDrainedCollisions = 0;
            LastCollisionMs = 0.0;
            return;
        }

        var drained = 0;
        var stopwatch = Stopwatch.StartNew();
        foreach (var (coord, update) in _pendingCollisionUpdates)
        {
            if (drained >= MaxCollisionUpdatesPerFrame)
            {
                break;
            }

            if (stopwatch.Elapsed.TotalMilliseconds >= CollisionBudgetMs)
            {
                break;
            }

            if (!_pendingCollisionUpdates.TryRemove(coord, out var pendingUpdate))
            {
                continue;
            }

            if (pendingUpdate.IsRemove)
            {
                RemoveChunkCollision(coord);
            }
            else
            {
                UpsertChunkCollision(coord, pendingUpdate.MeshData!);
            }

            drained++;
        }

        stopwatch.Stop();
        LastDrainedCollisions = drained;
        LastCollisionMs = stopwatch.Elapsed.TotalMilliseconds;
    }

    public void ClearAllCollisionBodies()
    {
        foreach (var body in _chunkBodies.Values)
        {
            body.QueueFree();
        }

        _chunkBodies.Clear();
    }

    public void ClearPendingCollisionUpdates()
    {
        _pendingCollisionUpdates.Clear();
    }

    public void RefreshLodOverlay()
    {
        foreach (var (coord, mesh) in _chunkMeshes)
        {
            if (_chunkLodSteps.TryGetValue(coord, out var lodStep))
            {
                ApplyChunkLodOverlay(mesh, lodStep);
            }
            else
            {
                mesh.MaterialOverlay = null;
            }
        }
    }

    public void PruneUnloadedMeshes(WorldStreamer streamer)
    {
        // Only prune every 30 frames to avoid per-frame allocation of ~2400 coords.
        if (++_pruneCounter < 30)
        {
            return;
        }

        _pruneCounter = 0;
        var loaded = new HashSet<ChunkCoord>(streamer.LoadedCoords);
        var stale = new List<ChunkCoord>();
        foreach (var coord in _chunkMeshes.Keys)
        {
            if (!loaded.Contains(coord))
            {
                stale.Add(coord);
            }
        }

        foreach (var coord in stale)
        {
            if (_chunkMeshes.TryGetValue(coord, out var meshInstance))
            {
                meshInstance.QueueFree();
                _chunkMeshes.Remove(coord);
            }

            if (_chunkBodies.TryGetValue(coord, out var body))
            {
                body.QueueFree();
                _chunkBodies.Remove(coord);
            }

            _chunks.Remove(coord);
            _chunkLodSteps.Remove(coord);
            _pendingCollisionUpdates.TryRemove(coord, out _);
        }
    }

    private void UpsertChunkMesh(ChunkCoord coord, MeshData meshData, int lodStep)
    {
        if (!_chunkMeshes.TryGetValue(coord, out var meshInstance))
        {
            meshInstance = new MeshInstance3D
            {
                Name = $"Chunk_{coord.X}_{coord.Y}_{coord.Z}",
                Position = new Vector3(
                    coord.X * _chunkSizeX,
                    coord.Y * _chunkSizeY,
                    coord.Z * _chunkSizeZ),
                MaterialOverride = _terrainMaterial,
            };
            _chunkRoot.AddChild(meshInstance);
            _chunkMeshes[coord] = meshInstance;
        }

        var godotMesh = BuildGodotMesh(meshData);
        meshInstance.Mesh = godotMesh;
        meshInstance.Visible = godotMesh.GetSurfaceCount() > 0;
        ApplyChunkLodOverlay(meshInstance, lodStep);

        if (lodStep == 1)
        {
            if (EnableCollisionUpdates)
            {
                _pendingCollisionUpdates[coord] = CollisionUpdate.Add(meshData);
            }
        }
        else
        {
            if (EnableCollisionUpdates && _chunkBodies.ContainsKey(coord))
            {
                _pendingCollisionUpdates[coord] = CollisionUpdate.Remove();
            }
        }
    }

    private void RemoveChunkCollision(ChunkCoord coord)
    {
        if (_chunkBodies.TryGetValue(coord, out var body))
        {
            body.QueueFree();
            _chunkBodies.Remove(coord);
        }
    }

    private void ApplyChunkLodOverlay(MeshInstance3D mesh, int lodStep)
    {
        if (!ShowLodOverlay)
        {
            mesh.MaterialOverlay = null;
            return;
        }

        mesh.MaterialOverlay = GetLodOverlayMaterial(lodStep);
    }

    private StandardMaterial3D GetLodOverlayMaterial(int lodStep)
    {
        if (_lodOverlayMaterials.TryGetValue(lodStep, out var existing))
        {
            return existing;
        }

        var color = lodStep switch
        {
            1 => new Color(0.4f, 1.0f, 0.4f, 0.35f),
            2 => new Color(1.0f, 0.9f, 0.3f, 0.35f),
            4 => new Color(1.0f, 0.45f, 0.35f, 0.35f),
            _ => new Color(0.7f, 0.6f, 1.0f, 0.35f),
        };

        var material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            AlbedoColor = color,
        };

        _lodOverlayMaterials[lodStep] = material;
        return material;
    }

    private void UpsertChunkCollision(ChunkCoord coord, MeshData meshData)
    {
        if (!_chunkBodies.TryGetValue(coord, out var body))
        {
            body = new StaticBody3D
            {
                Name = $"ChunkBody_{coord.X}_{coord.Y}_{coord.Z}",
                Position = new Vector3(
                    coord.X * _chunkSizeX,
                    coord.Y * _chunkSizeY,
                    coord.Z * _chunkSizeZ),
            };

            var shapeNode = new CollisionShape3D { Name = "Shape" };
            body.AddChild(shapeNode);
            _chunkRoot.AddChild(body);
            _chunkBodies[coord] = body;
        }

        var collisionShape = body.GetNode<CollisionShape3D>("Shape");
        if (meshData.Vertices.Count == 0 || meshData.Indices.Count < 3)
        {
            collisionShape.Shape = null;
            return;
        }

        var faces = new Vector3[meshData.Indices.Count];
        for (var i = 0; i < meshData.Indices.Count; i++)
        {
            var vertex = meshData.Vertices[meshData.Indices[i]];
            faces[i] = new Vector3(vertex.X, vertex.Y, vertex.Z);
        }

        var shape = new ConcavePolygonShape3D();
        shape.SetFaces(faces);
        collisionShape.Shape = shape;
    }

    private static ArrayMesh BuildGodotMesh(MeshData meshData)
    {
        var arrayMesh = new ArrayMesh();
        if (meshData.Vertices.Count == 0)
        {
            return arrayMesh;
        }

        var vertices = new Vector3[meshData.Vertices.Count];
        var normals = new Vector3[meshData.Normals.Count];
        var uvs = new Vector2[meshData.Uvs.Count];
        var indices = new int[meshData.Indices.Count];
        var colors = new Color[meshData.Colors.Count];

        for (var i = 0; i < meshData.Vertices.Count; i++)
        {
            var v = meshData.Vertices[i];
            vertices[i] = new Vector3(v.X, v.Y, v.Z);
        }

        for (var i = 0; i < meshData.Normals.Count; i++)
        {
            var n = meshData.Normals[i];
            normals[i] = new Vector3(n.X, n.Y, n.Z);
        }

        for (var i = 0; i < meshData.Uvs.Count; i++)
        {
            var uv = meshData.Uvs[i];
            uvs[i] = new Vector2(uv.X, uv.Y);
        }

        for (var i = 0; i < meshData.Indices.Count; i++)
        {
            indices[i] = meshData.Indices[i];
        }

        for (var i = 0; i < meshData.Colors.Count; i++)
        {
            var c = meshData.Colors[i];
            colors[i] = new Color(c.X, c.Y, c.Z);
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        arrays[(int)Mesh.ArrayType.Index] = indices;
        arrays[(int)Mesh.ArrayType.Color] = colors;

        arrayMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return arrayMesh;
    }

    public readonly record struct PendingChunk(DensityChunk Chunk, MeshData Mesh, int LodStep);

    public readonly record struct CollisionUpdate(MeshData? MeshData, bool IsRemove)
    {
        public static CollisionUpdate Add(MeshData meshData) => new(meshData, false);
        public static CollisionUpdate Remove() => new(null, true);
    }
}
