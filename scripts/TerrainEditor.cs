using System;
using System.Collections.Generic;
using Acreage.Voxel;
using Godot;

public sealed class TerrainEditor
{
    private const float RaycastDistance = 400f;
    private const float DigRadius = 3.5f;
    private const float FillRadius = 3f;
    private const float ConcreteRadius = 6f;
    private const float BrushStrength = 1.4f;

    private const int ChunkSizeX = 16;
    private const int ChunkSizeY = 64;
    private const int ChunkSizeZ = 16;

    private const string ActionApplyTool = "tool_apply";
    private const string ActionToolDig = "tool_select_dig";
    private const string ActionToolFill = "tool_select_fill";
    private const string ActionToolConcretePad = "tool_select_concrete_pad";

    private readonly Camera3D _camera;
    private readonly CharacterBody3D _player;
    private readonly ChunkRenderer _chunkRenderer;
    private readonly WorldStreamer _streamer;
    private readonly Func<PhysicsDirectSpaceState3D> _getSpaceState;

    public TerrainEditor(
        Camera3D camera,
        CharacterBody3D player,
        ChunkRenderer chunkRenderer,
        WorldStreamer streamer,
        Func<PhysicsDirectSpaceState3D> getSpaceState)
    {
        _camera = camera;
        _player = player;
        _chunkRenderer = chunkRenderer;
        _streamer = streamer;
        _getSpaceState = getSpaceState;
    }

    public EditMode CurrentEditMode { get; set; } = EditMode.Dig;

    public void HandleToolHotkeys()
    {
        if (Input.IsActionJustPressed(ActionToolDig))
        {
            CurrentEditMode = EditMode.Dig;
        }
        else if (Input.IsActionJustPressed(ActionToolFill))
        {
            CurrentEditMode = EditMode.Fill;
        }
        else if (Input.IsActionJustPressed(ActionToolConcretePad))
        {
            CurrentEditMode = EditMode.ConcretePad;
        }

        if (Input.MouseMode == Input.MouseModeEnum.Captured && Input.IsActionJustPressed(ActionApplyTool))
        {
            TryApplyCurrentTool();
        }
    }

    private void TryApplyCurrentTool()
    {
        if (!TryGetTerrainHit(out var hit, out var normal))
        {
            return;
        }

        var center = hit + (normal * 0.25f);
        _ = CurrentEditMode switch
        {
            EditMode.Dig => ApplySphericalBrush(center, DigRadius, BrushStrength, add: false, materialWhenSolid: null),
            EditMode.Fill => ApplySphericalBrush(center, FillRadius, BrushStrength, add: true, materialWhenSolid: MaterialType.Dirt),
            EditMode.ConcretePad => ApplyConcretePad(center, ConcreteRadius, center.Y),
            _ => false,
        };
    }

    private bool TryGetTerrainHit(out Vector3 hitPoint, out Vector3 normal)
    {
        hitPoint = default;
        normal = Vector3.Up;

        var from = _camera.GlobalPosition;
        var to = from + (_camera.GlobalTransform.Basis * Vector3.Forward) * RaycastDistance;
        var query = PhysicsRayQueryParameters3D.Create(from, to);
        query.CollideWithAreas = false;
        query.CollideWithBodies = true;
        query.Exclude = new Godot.Collections.Array<Rid> { _player.GetRid() };

        var result = _getSpaceState().IntersectRay(query);
        if (result.Count == 0)
        {
            return false;
        }

        if (result.TryGetValue("position", out var posVariant))
        {
            hitPoint = posVariant.AsVector3();
        }

        if (result.TryGetValue("normal", out var normalVariant))
        {
            normal = normalVariant.AsVector3().Normalized();
        }

        return true;
    }

    private bool ApplySphericalBrush(Vector3 center, float radius, float strength, bool add, MaterialType? materialWhenSolid)
    {
        var minX = Mathf.FloorToInt(center.X - radius);
        var maxX = Mathf.CeilToInt(center.X + radius);
        var minY = Mathf.FloorToInt(center.Y - radius);
        var maxY = Mathf.CeilToInt(center.Y + radius);
        var minZ = Mathf.FloorToInt(center.Z - radius);
        var maxZ = Mathf.CeilToInt(center.Z + radius);

        var touchedChunks = new HashSet<ChunkCoord>();
        var changed = false;
        for (var gx = minX; gx <= maxX; gx++)
        {
            for (var gy = minY; gy <= maxY; gy++)
            {
                for (var gz = minZ; gz <= maxZ; gz++)
                {
                    var samplePos = new Vector3(gx, gy, gz);
                    var distance = samplePos.DistanceTo(center);
                    if (distance > radius)
                    {
                        continue;
                    }

                    if (!TryGetSample(gx, gy, gz, out var oldDensity, out _))
                    {
                        continue;
                    }

                    var falloff = 1f - (distance / radius);
                    var delta = strength * falloff;
                    var newDensity = add ? oldDensity + delta : oldDensity - delta;
                    var material = newDensity > 0f
                        ? materialWhenSolid ?? MaterialType.Dirt
                        : MaterialType.Air;

                    if (TrySetSample(gx, gy, gz, newDensity, material, touchedChunks))
                    {
                        changed = true;
                    }
                }
            }
        }

        if (changed)
        {
            RemeshTouchedChunks(touchedChunks);
        }

        return changed;
    }

    private bool ApplyConcretePad(Vector3 center, float radius, float targetHeight)
    {
        var minX = Mathf.FloorToInt(center.X - radius);
        var maxX = Mathf.CeilToInt(center.X + radius);
        var minY = Mathf.FloorToInt(targetHeight - 4f);
        var maxY = Mathf.CeilToInt(targetHeight + 4f);
        var minZ = Mathf.FloorToInt(center.Z - radius);
        var maxZ = Mathf.CeilToInt(center.Z + radius);

        var touchedChunks = new HashSet<ChunkCoord>();
        var changed = false;
        for (var gx = minX; gx <= maxX; gx++)
        {
            for (var gz = minZ; gz <= maxZ; gz++)
            {
                var horizontalDistance = new Vector2(gx, gz).DistanceTo(new Vector2(center.X, center.Z));
                if (horizontalDistance > radius)
                {
                    continue;
                }

                var blend = 1f - (horizontalDistance / radius);
                for (var gy = minY; gy <= maxY; gy++)
                {
                    if (!TryGetSample(gx, gy, gz, out var oldDensity, out _))
                    {
                        continue;
                    }

                    var targetDensity = targetHeight - gy;
                    var newDensity = Mathf.Lerp(oldDensity, targetDensity, blend * 0.8f);
                    var material = newDensity > -0.3f ? MaterialType.Concrete : MaterialType.Air;
                    if (TrySetSample(gx, gy, gz, newDensity, material, touchedChunks))
                    {
                        changed = true;
                    }
                }
            }
        }

        if (changed)
        {
            RemeshTouchedChunks(touchedChunks);
        }

        return changed;
    }

    private bool TryGetSample(int worldGridX, int worldGridY, int worldGridZ, out float density, out MaterialType material)
    {
        density = 0f;
        material = MaterialType.Air;
        var ownersX = GetAxisOwners(worldGridX, ChunkSizeX);
        var ownersY = GetAxisOwners(worldGridY, ChunkSizeY);
        var ownersZ = GetAxisOwners(worldGridZ, ChunkSizeZ);

        foreach (var x in ownersX)
        {
            foreach (var y in ownersY)
            {
                foreach (var z in ownersZ)
                {
                    var coord = new ChunkCoord(x.Chunk, y.Chunk, z.Chunk);
                    if (_chunkRenderer.Chunks.TryGetValue(coord, out var chunk))
                    {
                        chunk.EnterReadLock();
                        try
                        {
                            density = chunk.GetDensity(x.Local, y.Local, z.Local);
                            material = chunk.GetMaterial(x.Local, y.Local, z.Local);
                        }
                        finally
                        {
                            chunk.ExitReadLock();
                        }

                        return true;
                    }
                }
            }
        }

        return false;
    }

    private bool TrySetSample(
        int worldGridX,
        int worldGridY,
        int worldGridZ,
        float density,
        MaterialType material,
        HashSet<ChunkCoord> touchedChunks)
    {
        var changed = false;
        var ownersX = GetAxisOwners(worldGridX, ChunkSizeX);
        var ownersY = GetAxisOwners(worldGridY, ChunkSizeY);
        var ownersZ = GetAxisOwners(worldGridZ, ChunkSizeZ);
        foreach (var x in ownersX)
        {
            foreach (var y in ownersY)
            {
                foreach (var z in ownersZ)
                {
                    var coord = new ChunkCoord(x.Chunk, y.Chunk, z.Chunk);
                    if (!_chunkRenderer.Chunks.TryGetValue(coord, out var chunk))
                    {
                        continue;
                    }

                    chunk.EnterWriteLock();
                    try
                    {
                        chunk.SetDensity(x.Local, y.Local, z.Local, density);
                        chunk.SetMaterial(x.Local, y.Local, z.Local, material);
                    }
                    finally
                    {
                        chunk.ExitWriteLock();
                    }

                    touchedChunks.Add(coord);
                    changed = true;
                }
            }
        }

        return changed;
    }

    private void RemeshTouchedChunks(HashSet<ChunkCoord> touchedChunks)
    {
        var remeshSet = new HashSet<ChunkCoord>(touchedChunks);
        foreach (var coord in touchedChunks)
        {
            remeshSet.Add(new ChunkCoord(coord.X + 1, coord.Y, coord.Z));
            remeshSet.Add(new ChunkCoord(coord.X - 1, coord.Y, coord.Z));
            remeshSet.Add(new ChunkCoord(coord.X, coord.Y, coord.Z + 1));
            remeshSet.Add(new ChunkCoord(coord.X, coord.Y, coord.Z - 1));
        }

        foreach (var coord in remeshSet)
        {
            _streamer.RemeshLoadedChunk(coord);
        }
    }

    private static AxisOwner[] GetAxisOwners(int worldGrid, int chunkSize)
    {
        var chunk = VoxelMath.FloorDiv(worldGrid, chunkSize);
        var local = VoxelMath.PositiveMod(worldGrid, chunkSize);
        if (local != 0)
        {
            return [new AxisOwner(chunk, local)];
        }

        return
        [
            new AxisOwner(chunk, 0),
            new AxisOwner(chunk - 1, chunkSize),
        ];
    }

    private readonly record struct AxisOwner(int Chunk, int Local);

    public enum EditMode : byte
    {
        Dig,
        Fill,
        ConcretePad,
    }
}
