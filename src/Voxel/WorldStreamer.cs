namespace Acreage.Voxel;

public readonly record struct LodTier(int Step, int MaxDistance);

public sealed class WorldStreamer
{
    private readonly ChunkGenerationService _generation;
    private readonly LodTier[] _tiers;
    private readonly ChunkStore? _store;
    private const int LodHysteresisChunks = 1;
    private readonly int _maxDistance;
    private readonly object _lock = new();
    private readonly System.Collections.Generic.Dictionary<ChunkCoord, ChunkRecord> _loaded = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ChunkCoord, DensityChunk> _chunkData = new();
    private long _updateTick;
    private long _profileLodTransitions;
    private long _profileRemeshRequests;
    private long _profileMeshesPublished;
    private long _profileChunksReady;

    public bool EnableLodTransitions { get; set; } = true;
    public int LodTransitionCooldownUpdates { get; set; } = 30;
    public int MaxLodDemotionsPerUpdate { get; set; } = 2;
    public bool EnableNeighborRemeshOnChunkReady { get; set; } = true;
    public bool EnableSeamStepResolve { get; set; } = false;
    public int MaxLodTransitionsPerUpdate { get; set; } = 2;
    public int MaxRemeshesPerUpdate { get; set; } = 16;
    public bool TransitionRemeshIncludesNeighbors { get; set; } = false;

    public WorldStreamer(ChunkGenerationService generation, LodTier[] tiers, ChunkStore? store = null)
    {
        if (tiers.Length == 0)
        {
            throw new System.ArgumentException("At least one LOD tier is required.", nameof(tiers));
        }

        _generation = generation;
        _tiers = tiers;
        _store = store;
        _maxDistance = 0;
        foreach (var tier in tiers)
        {
            if (tier.MaxDistance > _maxDistance)
            {
                _maxDistance = tier.MaxDistance;
            }
        }

        _generation.ChunkReady += OnChunkReady;
    }

    /// <summary>
    /// Fired when a chunk is meshed. Args: chunk, mesh data, LOD step.
    /// </summary>
    public event System.Action<DensityChunk, MeshData, int>? ChunkMeshed;

    public readonly record struct ProfileSnapshot(
        int LodTransitions,
        int RemeshRequests,
        int MeshesPublished,
        int ChunksReady);

    public ProfileSnapshot ConsumeProfileSnapshot()
    {
        return new ProfileSnapshot(
            (int)System.Threading.Interlocked.Exchange(ref _profileLodTransitions, 0),
            (int)System.Threading.Interlocked.Exchange(ref _profileRemeshRequests, 0),
            (int)System.Threading.Interlocked.Exchange(ref _profileMeshesPublished, 0),
            (int)System.Threading.Interlocked.Exchange(ref _profileChunksReady, 0));
    }

    public System.Collections.Generic.IReadOnlyCollection<ChunkCoord> LoadedCoords
    {
        get
        {
            lock (_lock)
            {
                var result = new ChunkCoord[_loaded.Count];
                var index = 0;
                foreach (var coord in _loaded.Keys)
                {
                    result[index++] = coord;
                }

                return result;
            }
        }
    }

    public void UpdateFocusPosition(float worldX, float worldY, float worldZ, int chunkSizeX, int chunkSizeY, int chunkSizeZ, float forwardX = 0f, float forwardZ = 0f)
    {
        var center = new ChunkCoord(
            ToChunkCoord(worldX, chunkSizeX),
            ToChunkCoord(worldY, chunkSizeY),
            ToChunkCoord(worldZ, chunkSizeZ));

        var updateTick = System.Threading.Interlocked.Increment(ref _updateTick);
        var wanted = new System.Collections.Generic.HashSet<ChunkCoord>();
        var lodTransitions = 0;
        var maxLodTransitionsPerUpdate = System.Math.Max(0, MaxLodTransitionsPerUpdate);
        var maxLodDemotionsPerUpdate = System.Math.Max(0, MaxLodDemotionsPerUpdate);
        var lodTransitionCooldownUpdates = System.Math.Max(0, LodTransitionCooldownUpdates);
        var maxRemeshesPerUpdate = System.Math.Max(0, MaxRemeshesPerUpdate);
        var highPriorityRemeshCandidates = new System.Collections.Generic.HashSet<ChunkCoord>();
        var lowPriorityRemeshCandidates = new System.Collections.Generic.HashSet<ChunkCoord>();
        var remeshRequests = new System.Collections.Generic.List<(DensityChunk Chunk, int DesiredStep)>();
        var transitionCandidates = new System.Collections.Generic.List<TransitionCandidate>();

        for (var z = -_maxDistance; z <= _maxDistance; z++)
        {
            for (var x = -_maxDistance; x <= _maxDistance; x++)
            {
                var chebyshev = System.Math.Max(System.Math.Abs(x), System.Math.Abs(z));
                if (chebyshev > _maxDistance)
                {
                    continue;
                }

                var coord = new ChunkCoord(center.X + x, 0, center.Z + z);
                wanted.Add(coord);

                lock (_lock)
                {
                    if (_loaded.TryGetValue(coord, out var existing))
                    {
                        var targetStep = GetLodStepWithHysteresis(chebyshev, existing.DesiredLodStep);

                        // Queue transition candidates and apply with a strict
                        // global budget after scanning all coords.
                        if (EnableLodTransitions &&
                            existing.DesiredLodStep != targetStep &&
                            existing.Chunk is not null)
                        {
                            transitionCandidates.Add(new TransitionCandidate(coord, chebyshev, existing.DesiredLodStep, targetStep));
                        }

                        continue;
                    }

                    var step = GetLodStep(chebyshev);
                    _loaded[coord] = ChunkRecord.Pending(step, updateTick);
                }

                var distSq = (float)(x * x + z * z);

                // Bias priority by view direction: chunks in front of the
                // camera generate first. dot=1 (in front) → 1x, dot=-1
                // (behind) → 3x priority penalty.
                var dirLen = System.MathF.Sqrt(distSq);
                if (dirLen > 0.001f && (forwardX != 0f || forwardZ != 0f))
                {
                    var dot = (x * forwardX + z * forwardZ) / dirLen;
                    distSq *= 2f - dot;
                }

                _generation.Enqueue(coord, distSq);
            }
        }

        System.Collections.Generic.List<DensityChunk>? dirtyUnloaded = null;

        lock (_lock)
        {
            var toRemove = new System.Collections.Generic.List<ChunkCoord>();
            foreach (var coord in _loaded.Keys)
            {
                if (!wanted.Contains(coord))
                {
                    toRemove.Add(coord);
                }
            }

            foreach (var coord in toRemove)
            {
                if (_store is not null && _chunkData.TryGetValue(coord, out var unloading) && unloading.IsDirty)
                {
                    dirtyUnloaded ??= new();
                    dirtyUnloaded.Add(unloading);
                }

                _loaded.Remove(coord);
                _chunkData.TryRemove(coord, out _);
            }

            if (EnableLodTransitions && maxLodTransitionsPerUpdate > 0 && transitionCandidates.Count > 0)
            {
                transitionCandidates.Sort((a, b) =>
                {
                    if (a.IsPromotion != b.IsPromotion)
                    {
                        return a.IsPromotion ? -1 : 1;
                    }

                    return a.Distance.CompareTo(b.Distance);
                });

                var demotions = 0;
                foreach (var candidate in transitionCandidates)
                {
                    if (lodTransitions >= maxLodTransitionsPerUpdate)
                    {
                        break;
                    }

                    if (!candidate.IsPromotion &&
                        (maxLodDemotionsPerUpdate <= 0 || demotions >= maxLodDemotionsPerUpdate))
                    {
                        continue;
                    }

                    if (!_loaded.TryGetValue(candidate.Coord, out var current) || current.Chunk is null)
                    {
                        continue;
                    }

                    if (current.DesiredLodStep != candidate.CurrentStep)
                    {
                        continue;
                    }

                    if (updateTick - current.LastTransitionTick < lodTransitionCooldownUpdates)
                    {
                        continue;
                    }

                    _loaded[candidate.Coord] = current with
                    {
                        DesiredLodStep = candidate.TargetStep,
                        LastTransitionTick = updateTick,
                    };

                    lodTransitions++;
                    if (!candidate.IsPromotion)
                    {
                        demotions++;
                    }

                    var targetSet = candidate.IsPromotion ? highPriorityRemeshCandidates : lowPriorityRemeshCandidates;
                    targetSet.Add(candidate.Coord);
                    if (TransitionRemeshIncludesNeighbors)
                    {
                        foreach (var neighbor in GetHorizontalNeighborCoords(candidate.Coord))
                        {
                            targetSet.Add(neighbor);
                        }
                    }
                }
            }

            var highPriorityOrdered = new System.Collections.Generic.List<ChunkCoord>(highPriorityRemeshCandidates);
            highPriorityOrdered.Sort((a, b) => CompareDistanceToCenter(a, b, center));
            foreach (var coord in highPriorityOrdered)
            {
                if (remeshRequests.Count >= maxRemeshesPerUpdate)
                {
                    break;
                }

                if (_loaded.TryGetValue(coord, out var record) && record.Chunk is not null)
                {
                    remeshRequests.Add((record.Chunk, record.DesiredLodStep));
                }
            }

            if (remeshRequests.Count < maxRemeshesPerUpdate)
            {
                var lowPriorityOrdered = new System.Collections.Generic.List<ChunkCoord>(lowPriorityRemeshCandidates);
                lowPriorityOrdered.Sort((a, b) => CompareDistanceToCenter(a, b, center));
                foreach (var coord in lowPriorityOrdered)
                {
                    if (remeshRequests.Count >= maxRemeshesPerUpdate)
                    {
                        break;
                    }

                    if (_loaded.TryGetValue(coord, out var record) && record.Chunk is not null)
                    {
                        remeshRequests.Add((record.Chunk, record.DesiredLodStep));
                    }
                }
            }
        }

        foreach (var request in remeshRequests)
        {
            PublishChunkMesh(request.Chunk, request.DesiredStep);
        }

        if (lodTransitions > 0)
        {
            System.Threading.Interlocked.Add(ref _profileLodTransitions, lodTransitions);
        }

        if (remeshRequests.Count > 0)
        {
            System.Threading.Interlocked.Add(ref _profileRemeshRequests, remeshRequests.Count);
        }

        if (dirtyUnloaded is not null)
        {
            foreach (var chunk in dirtyUnloaded)
            {
                chunk.EnterReadLock();
                try
                {
                    _store!.Save(chunk);
                }
                finally
                {
                    chunk.ExitReadLock();
                }
            }
        }
    }

    public void SaveAllDirty()
    {
        if (_store is null)
        {
            return;
        }

        foreach (var kvp in _chunkData)
        {
            if (kvp.Value.IsDirty)
            {
                kvp.Value.EnterReadLock();
                try
                {
                    _store.Save(kvp.Value);
                }
                finally
                {
                    kvp.Value.ExitReadLock();
                }
            }
        }
    }

    public bool RemeshLoadedChunk(ChunkCoord coord)
    {
        DensityChunk? chunk = null;
        int step;
        lock (_lock)
        {
            if (_loaded.TryGetValue(coord, out var record) && record.Chunk is not null)
            {
                chunk = record.Chunk;
                step = record.DesiredLodStep;
            }
            else
            {
                return false;
            }
        }

        PublishChunkMesh(chunk, step);
        return true;
    }

    private void OnChunkReady(DensityChunk chunk)
    {
        System.Threading.Interlocked.Increment(ref _profileChunksReady);

        int step;
        lock (_lock)
        {
            if (!_loaded.TryGetValue(chunk.Coord, out var record))
            {
                return;
            }

            step = record.DesiredLodStep;
            _loaded[chunk.Coord] = new ChunkRecord(chunk, null, record.DesiredLodStep, record.MeshedLodStep, record.LastTransitionTick);
            _chunkData[chunk.Coord] = chunk;
        }

        PublishChunkMesh(chunk, step);

        if (!EnableNeighborRemeshOnChunkReady)
        {
            return;
        }

        var neighborChunks = new System.Collections.Generic.List<(DensityChunk Chunk, int Step)>();
        lock (_lock)
        {
            foreach (var coord in GetHorizontalNeighborCoords(chunk.Coord))
            {
                if (_loaded.TryGetValue(coord, out var record) && record.Chunk is not null)
                {
                    neighborChunks.Add((record.Chunk, record.DesiredLodStep));
                }
            }
        }

        foreach (var (neighbor, neighborStep) in neighborChunks)
        {
            PublishChunkMesh(neighbor, neighborStep);
        }
    }

    private int GetLodStep(int chebyshevDistance)
    {
        // Tiers are checked in order. The first tier whose MaxDistance covers
        // the chebyshev distance wins.
        foreach (var tier in _tiers)
        {
            if (chebyshevDistance <= tier.MaxDistance)
            {
                return tier.Step;
            }
        }

        // Fallback to the last tier's step.
        return _tiers[_tiers.Length - 1].Step;
    }

    private int GetLodStepWithHysteresis(int chebyshevDistance, int currentStep)
    {
        var currentTier = GetTierIndexForStep(currentStep);
        if (currentTier < 0)
        {
            return GetLodStep(chebyshevDistance);
        }

        var lowerBound = currentTier == 0 ? 0 : _tiers[currentTier - 1].MaxDistance + 1;
        var upperBound = _tiers[currentTier].MaxDistance;

        if (chebyshevDistance > upperBound + LodHysteresisChunks && currentTier < _tiers.Length - 1)
        {
            return _tiers[currentTier + 1].Step;
        }

        if (chebyshevDistance < lowerBound - LodHysteresisChunks && currentTier > 0)
        {
            return _tiers[currentTier - 1].Step;
        }

        return currentStep;
    }

    private int GetTierIndexForStep(int step)
    {
        for (var i = 0; i < _tiers.Length; i++)
        {
            if (_tiers[i].Step == step)
            {
                return i;
            }
        }

        return -1;
    }

    private static int ToChunkCoord(float world, int chunkSize)
    {
        return (int)System.MathF.Floor(world / chunkSize);
    }

    private static int ChebyshevDistanceToCenter(ChunkCoord coord, ChunkCoord center)
    {
        return System.Math.Max(System.Math.Abs(coord.X - center.X), System.Math.Abs(coord.Z - center.Z));
    }

    private static int CompareDistanceToCenter(ChunkCoord a, ChunkCoord b, ChunkCoord center)
    {
        return ChebyshevDistanceToCenter(a, center).CompareTo(ChebyshevDistanceToCenter(b, center));
    }

    private void PublishChunkMesh(DensityChunk chunk, int desiredStep)
    {
        var step = EnableSeamStepResolve
            // Keep shared borders crack-free by meshing against the finest loaded
            // face-neighbor LOD step.
            ? ResolveMeshStep(chunk.Coord, desiredStep)
            : desiredStep;
        MeshData mesh;
        chunk.EnterReadLock();
        try
        {
            mesh = MarchingCubesMesher.Build(
                chunk,
                (gx, gy, gz) => SampleDensity(chunk, gx, gy, gz),
                (gx, gy, gz) => SampleMaterial(chunk, gx, gy, gz),
                step);
        }
        finally
        {
            chunk.ExitReadLock();
        }

        lock (_lock)
        {
            if (!_loaded.ContainsKey(chunk.Coord))
            {
                return;
            }

            var current = _loaded[chunk.Coord];
            _loaded[chunk.Coord] = new ChunkRecord(chunk, mesh, current.DesiredLodStep, step, current.LastTransitionTick);
        }

        ChunkMeshed?.Invoke(chunk, mesh, step);
        System.Threading.Interlocked.Increment(ref _profileMeshesPublished);
    }

    private int ResolveMeshStep(ChunkCoord coord, int desiredStep)
    {
        var resolved = desiredStep;
        lock (_lock)
        {
            foreach (var neighborCoord in GetHorizontalNeighborCoords(coord))
            {
                if (_loaded.TryGetValue(neighborCoord, out var neighbor))
                {
                    if (neighbor.DesiredLodStep < resolved)
                    {
                        resolved = neighbor.DesiredLodStep;
                    }
                }
            }
        }

        return resolved;
    }

    private float SampleDensity(DensityChunk origin, int gx, int gy, int gz)
    {
        if (origin.InBounds(gx, gy, gz))
        {
            return origin.GetDensity(gx, gy, gz);
        }

        var offsetX = VoxelMath.FloorDiv(gx, origin.SizeX);
        var offsetY = VoxelMath.FloorDiv(gy, origin.SizeY);
        var offsetZ = VoxelMath.FloorDiv(gz, origin.SizeZ);
        var localX = VoxelMath.PositiveMod(gx, origin.SizeX);
        var localY = VoxelMath.PositiveMod(gy, origin.SizeY);
        var localZ = VoxelMath.PositiveMod(gz, origin.SizeZ);
        var neighborCoord = new ChunkCoord(
            origin.Coord.X + offsetX,
            origin.Coord.Y + offsetY,
            origin.Coord.Z + offsetZ);

        if (_chunkData.TryGetValue(neighborCoord, out var neighbor))
        {
            return neighbor.GetDensity(localX, localY, localZ);
        }

        if (neighborCoord.Y < 0)
        {
            return 1000f;
        }

        return 0f;
    }

    private MaterialType SampleMaterial(DensityChunk origin, int gx, int gy, int gz)
    {
        if (origin.InBounds(gx, gy, gz))
        {
            return origin.GetMaterial(gx, gy, gz);
        }

        var offsetX = VoxelMath.FloorDiv(gx, origin.SizeX);
        var offsetY = VoxelMath.FloorDiv(gy, origin.SizeY);
        var offsetZ = VoxelMath.FloorDiv(gz, origin.SizeZ);
        var localX = VoxelMath.PositiveMod(gx, origin.SizeX);
        var localY = VoxelMath.PositiveMod(gy, origin.SizeY);
        var localZ = VoxelMath.PositiveMod(gz, origin.SizeZ);
        var neighborCoord = new ChunkCoord(
            origin.Coord.X + offsetX,
            origin.Coord.Y + offsetY,
            origin.Coord.Z + offsetZ);

        if (_chunkData.TryGetValue(neighborCoord, out var neighbor))
        {
            return neighbor.GetMaterial(localX, localY, localZ);
        }

        if (neighborCoord.Y < 0)
        {
            return MaterialType.Stone;
        }

        return MaterialType.Air;
    }

    private static ChunkCoord[] GetFaceNeighborCoords(ChunkCoord coord)
    {
        return
        [
            new ChunkCoord(coord.X + 1, coord.Y, coord.Z),
            new ChunkCoord(coord.X - 1, coord.Y, coord.Z),
            new ChunkCoord(coord.X, coord.Y + 1, coord.Z),
            new ChunkCoord(coord.X, coord.Y - 1, coord.Z),
            new ChunkCoord(coord.X, coord.Y, coord.Z + 1),
            new ChunkCoord(coord.X, coord.Y, coord.Z - 1),
        ];
    }

    private static ChunkCoord[] GetHorizontalNeighborCoords(ChunkCoord coord)
    {
        return
        [
            new ChunkCoord(coord.X + 1, coord.Y, coord.Z),
            new ChunkCoord(coord.X - 1, coord.Y, coord.Z),
            new ChunkCoord(coord.X, coord.Y, coord.Z + 1),
            new ChunkCoord(coord.X, coord.Y, coord.Z - 1),
        ];
    }

    private readonly record struct TransitionCandidate(ChunkCoord Coord, int Distance, int CurrentStep, int TargetStep)
    {
        public bool IsPromotion => TargetStep < CurrentStep;
    }

    private readonly record struct ChunkRecord(DensityChunk? Chunk, MeshData? Mesh, int DesiredLodStep, int MeshedLodStep, long LastTransitionTick)
    {
        public static ChunkRecord Pending(int lodStep, long updateTick) => new(null, null, lodStep, lodStep, updateTick);
    }
}
