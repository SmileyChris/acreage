using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using Acreage.Voxel;
using Godot;

public partial class Main : Node3D
{
    private const int ChunkSizeX = 16;
    private const int ChunkSizeY = 64;
    private const int ChunkSizeZ = 16;
    private static readonly LodTier[] LodTiers =
    [
        new(Step: 1, MaxDistance: 3),
        new(Step: 2, MaxDistance: 6),
        new(Step: 4, MaxDistance: 9),
    ];
    private const float WaterLevel = 10f;
    private const float RaycastDistance = 400f;
    private const float DigRadius = 3.5f;
    private const float FillRadius = 3f;
    private const float ConcreteRadius = 6f;
    private const float BrushStrength = 1.4f;
    private const double MeshUploadBudgetMs = 3.0;
    private const int MaxMeshDrainsPerFrame = 64;
    private const double CollisionBudgetMs = 1.5;
    private const int MaxCollisionUpdatesPerFrame = 2;
    private const string ActionMoveForward = "move_forward";
    private const string ActionMoveBackward = "move_backward";
    private const string ActionMoveLeft = "move_left";
    private const string ActionMoveRight = "move_right";
    private const string ActionSprint = "move_sprint";
    private const string ActionJump = "move_jump";
    private const string ActionToggleMouseCapture = "ui_toggle_mouse_capture";
    private const string ActionApplyTool = "tool_apply";
    private const string ActionToolDig = "tool_select_dig";
    private const string ActionToolFill = "tool_select_fill";
    private const string ActionToolConcretePad = "tool_select_concrete_pad";
    private const string ActionToggleLodOverlay = "debug_toggle_lod_overlay";
    private const string ActionTogglePerfCollision = "perf_toggle_collision";
    private const string ActionTogglePerfLodTransitions = "perf_toggle_lod_transitions";
    private const string ActionTogglePerfNeighborRemesh = "perf_toggle_neighbor_remesh";
    private const string ActionTogglePerfSeamResolve = "perf_toggle_seam_resolve";
    private const string ActionTogglePerfRegionMap = "perf_toggle_region_map";

    private const float Gravity = 30f;
    private const float MaxFallSpeed = 40f;
    private const float JumpImpulse = 10f;
    private const float WalkSpeed = 12f;
    private const float SprintSpeed = 24f;
    private const float PlayerHeight = 1.8f;
    private const float PlayerWidth = 0.8f;
    private static readonly Vector3 CameraArmOffset = new(0f, 3f, 8f);
    private const float PitchMin = -1.2f;
    private const float PitchMax = 0.5f;

    private readonly ConcurrentDictionary<ChunkCoord, PendingChunk> _pendingChunks = new();
    private readonly Dictionary<ChunkCoord, DensityChunk> _chunks = new();
    private readonly Dictionary<ChunkCoord, MeshInstance3D> _chunkMeshes = new();
    private readonly Dictionary<ChunkCoord, StaticBody3D> _chunkBodies = new();
    private readonly Dictionary<ChunkCoord, int> _chunkLodSteps = new();
    private readonly Dictionary<int, StandardMaterial3D> _lodOverlayMaterials = new();
    private readonly ConcurrentDictionary<ChunkCoord, CollisionUpdate> _pendingCollisionUpdates = new();
    private Node3D _chunkRoot = null!;
    private MeshInstance3D _waterPlane = null!;
    private Camera3D _camera = null!;
    private CharacterBody3D _player = null!;
    private Node3D _cameraPivot = null!;
    private Vector3 _velocity;
    private ChunkGenerationService? _generation;
    private WorldStreamer? _streamer;
    private StandardMaterial3D _terrainMaterial = null!;
    private Label _fpsLabel = null!;
    private float _yaw;
    private float _pitch;
    private EditMode _editMode = EditMode.Dig;
    private bool _showLodOverlay;
    private bool _enableCollisionUpdates = true;
    private bool _enableLodTransitions = true;
    private bool _enableNeighborRemesh = true;
    private bool _enableSeamResolve = true;
    private bool _enableRegionMap = true;
    private int _pruneCounter;
    private int _lastDrainedChunks;
    private double _lastMeshUploadMs;
    private int _lastDrainedCollisions;
    private double _lastCollisionMs;
    private float _hudTimer;
    private string _statsLine = string.Empty;
    private readonly Queue<float> _fpsHistory = new();
    private readonly Queue<float> _queueHistory = new();
    private readonly Queue<float> _uploadHistory = new();
    private readonly Queue<float> _collisionHistory = new();

    public override void _Ready()
    {
        SetupInputActions();
        SetupSceneNodes();
        SetupVoxelRuntime();
        Input.MouseMode = Input.MouseModeEnum.Captured;
        GD.Print("Acreage: voxel generation runtime started.");
        GD.Print("Terrain edit controls: LMB apply, 1 dig, 2 fill, 3 concrete pad.");
        GD.Print("Debug: backtick toggles LOD overlay.");
        GD.Print("Perf toggles: F5 collision, F6 LOD transitions, F7 neighbor remesh, F8 seam resolve, F9 region map.");
    }

    public override void _ExitTree()
    {
        if (_streamer is not null)
        {
            _streamer.ChunkMeshed -= OnChunkMeshed;
        }

        _generation?.Dispose();
    }

    public override void _Process(double delta)
    {
        HandleActionHotkeys();
        HandleCharacterMovement((float)delta);

        var forward = _camera.GlobalTransform.Basis * Vector3.Forward;
        _streamer?.UpdateFocusPosition(
            _camera.GlobalPosition.X,
            _camera.GlobalPosition.Y,
            _camera.GlobalPosition.Z,
            ChunkSizeX,
            ChunkSizeY,
            ChunkSizeZ,
            forward.X,
            forward.Z);

        _waterPlane.GlobalPosition = new Vector3(_camera.GlobalPosition.X, WaterLevel, _camera.GlobalPosition.Z);

        DrainPendingChunks();
        DrainPendingCollisionUpdates();
        UpdateProfilingHud((float)delta);
        PruneUnloadedMeshes();
        _fpsLabel.Text = $"FPS: {Engine.GetFramesPerSecond()} | Tool: {_editMode}\n{_statsLine}";
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseMotion motion && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            const float sensitivity = 0.0025f;
            _yaw -= motion.Relative.X * sensitivity;
            _pitch -= motion.Relative.Y * sensitivity;
            _pitch = Mathf.Clamp(_pitch, PitchMin, PitchMax);
            _player.Rotation = new Vector3(0f, _yaw, 0f);
            _cameraPivot.Rotation = new Vector3(_pitch, 0f, 0f);
        }
    }

    private void SetupSceneNodes()
    {
        _chunkRoot = new Node3D { Name = "Chunks" };
        AddChild(_chunkRoot);

        _player = new CharacterBody3D
        {
            Name = "Player",
            Position = new Vector3(0f, 28f, 0f),
            FloorSnapLength = 0.5f,
            FloorMaxAngle = Mathf.DegToRad(50f),
            SafeMargin = 0.1f,
        };
        AddChild(_player);

        var playerMesh = new MeshInstance3D
        {
            Name = "PlayerMesh",
            Mesh = new BoxMesh
            {
                Size = new Vector3(PlayerWidth, PlayerHeight, PlayerWidth),
            },
            Position = new Vector3(0f, PlayerHeight * 0.5f, 0f),
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.3f, 0.5f, 0.9f),
            },
        };
        _player.AddChild(playerMesh);

        var playerShape = new CollisionShape3D
        {
            Name = "PlayerShape",
            Shape = new BoxShape3D
            {
                Size = new Vector3(PlayerWidth, PlayerHeight, PlayerWidth),
            },
            Position = new Vector3(0f, PlayerHeight * 0.5f, 0f),
        };
        _player.AddChild(playerShape);

        _cameraPivot = new Node3D
        {
            Name = "CameraPivot",
            Position = new Vector3(0f, PlayerHeight * 0.85f, 0f),
        };
        _player.AddChild(_cameraPivot);

        _camera = new Camera3D
        {
            Name = "FlyCamera",
            Current = true,
            Position = CameraArmOffset,
            Near = 0.05f,
            Far = 3000f,
        };
        _cameraPivot.AddChild(_camera);

        var sun = new DirectionalLight3D
        {
            Name = "Sun",
            LightEnergy = 1.4f,
            Rotation = new Vector3(Mathf.DegToRad(-55f), Mathf.DegToRad(35f), 0f),
        };
        AddChild(sun);

        var canvas = new CanvasLayer { Name = "HUD" };
        AddChild(canvas);
        _fpsLabel = new Label
        {
            Position = new Vector2(10, 10),
            Text = "FPS: --",
        };
        _fpsLabel.AddThemeFontSizeOverride("font_size", 20);
        canvas.AddChild(_fpsLabel);

        _terrainMaterial = new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            Roughness = 1.0f,
        };

        // Procedural sky
        var skyMaterial = new ProceduralSkyMaterial();
        var sky = new Sky { SkyMaterial = skyMaterial };
        var environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = sky,
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            ReflectedLightSource = Godot.Environment.ReflectionSource.Sky,
        };
        var worldEnv = new WorldEnvironment
        {
            Name = "WorldEnvironment",
            Environment = environment,
        };
        AddChild(worldEnv);

        // Water plane
        var waterShader = GD.Load<Shader>("res://shaders/water.gdshader");
        var waterMaterial = new ShaderMaterial { Shader = waterShader };
        var planeMesh = new PlaneMesh
        {
            Size = new Vector2(2000f, 2000f),
            SubdivideWidth = 64,
            SubdivideDepth = 64,
        };
        _waterPlane = new MeshInstance3D
        {
            Name = "Water",
            Mesh = planeMesh,
            MaterialOverride = waterMaterial,
            Position = new Vector3(0f, WaterLevel, 0f),
        };
        AddChild(_waterPlane);
    }

    private void SetupVoxelRuntime()
    {
        var generator = new HillsGenerator();
        HillsGenerator.UseRegionMap = _enableRegionMap;
        var workerCount = Mathf.Max(1, System.Environment.ProcessorCount / 2);
        _generation = new ChunkGenerationService(generator, workerCount, ChunkSizeX, ChunkSizeY, ChunkSizeZ);
        _streamer = new WorldStreamer(_generation, LodTiers);
        ApplyStreamerPerfSettings();
        _streamer.ChunkMeshed += OnChunkMeshed;
    }

    private void ApplyStreamerPerfSettings()
    {
        if (_streamer is null)
        {
            return;
        }

        _streamer.EnableLodTransitions = _enableLodTransitions;
        _streamer.EnableNeighborRemeshOnChunkReady = _enableNeighborRemesh;
        _streamer.EnableSeamStepResolve = _enableSeamResolve;
    }

    private void SetupInputActions()
    {
        EnsureActionWithPhysicalKey(ActionMoveForward, Key.W);
        EnsureActionWithPhysicalKey(ActionMoveBackward, Key.S);
        EnsureActionWithPhysicalKey(ActionMoveLeft, Key.A);
        EnsureActionWithPhysicalKey(ActionMoveRight, Key.D);
        EnsureActionWithPhysicalKey(ActionSprint, Key.Shift);
        EnsureActionWithPhysicalKey(ActionJump, Key.Space);
        EnsureActionWithPhysicalKey(ActionToggleMouseCapture, Key.Escape);
        EnsureActionWithPhysicalKey(ActionToolDig, Key.Key1);
        EnsureActionWithPhysicalKey(ActionToolFill, Key.Key2);
        EnsureActionWithPhysicalKey(ActionToolConcretePad, Key.Key3);
        EnsureActionWithPhysicalKey(ActionToggleLodOverlay, Key.Quoteleft);
        EnsureActionWithPhysicalKey(ActionTogglePerfCollision, Key.F5);
        EnsureActionWithPhysicalKey(ActionTogglePerfLodTransitions, Key.F6);
        EnsureActionWithPhysicalKey(ActionTogglePerfNeighborRemesh, Key.F7);
        EnsureActionWithPhysicalKey(ActionTogglePerfSeamResolve, Key.F8);
        EnsureActionWithPhysicalKey(ActionTogglePerfRegionMap, Key.F9);
        EnsureActionWithMouseButton(ActionApplyTool, MouseButton.Left);
    }

    private void HandleActionHotkeys()
    {
        if (Input.IsActionJustPressed(ActionToggleMouseCapture))
        {
            Input.MouseMode = Input.MouseMode == Input.MouseModeEnum.Captured
                ? Input.MouseModeEnum.Visible
                : Input.MouseModeEnum.Captured;
        }

        if (Input.IsActionJustPressed(ActionToggleLodOverlay))
        {
            _showLodOverlay = !_showLodOverlay;
            RefreshLodOverlay();
            GD.Print($"LOD overlay: {(_showLodOverlay ? "ON" : "OFF")}");
        }

        if (Input.IsActionJustPressed(ActionTogglePerfCollision))
        {
            _enableCollisionUpdates = !_enableCollisionUpdates;
            if (!_enableCollisionUpdates)
            {
                _pendingCollisionUpdates.Clear();
                ClearAllCollisionBodies();
            }

            GD.Print($"Perf: collision updates {(_enableCollisionUpdates ? "ON" : "OFF")}");
        }

        if (Input.IsActionJustPressed(ActionTogglePerfLodTransitions))
        {
            _enableLodTransitions = !_enableLodTransitions;
            ApplyStreamerPerfSettings();
            GD.Print($"Perf: LOD transitions {(_enableLodTransitions ? "ON" : "OFF")}");
        }

        if (Input.IsActionJustPressed(ActionTogglePerfNeighborRemesh))
        {
            _enableNeighborRemesh = !_enableNeighborRemesh;
            ApplyStreamerPerfSettings();
            GD.Print($"Perf: neighbor remesh {(_enableNeighborRemesh ? "ON" : "OFF")}");
        }

        if (Input.IsActionJustPressed(ActionTogglePerfSeamResolve))
        {
            _enableSeamResolve = !_enableSeamResolve;
            ApplyStreamerPerfSettings();
            GD.Print($"Perf: seam resolve {(_enableSeamResolve ? "ON" : "OFF")}");
        }

        if (Input.IsActionJustPressed(ActionTogglePerfRegionMap))
        {
            _enableRegionMap = !_enableRegionMap;
            HillsGenerator.UseRegionMap = _enableRegionMap;
            GD.Print($"Perf: region map {(_enableRegionMap ? "ON" : "OFF")} (new chunks)");
        }

        if (Input.IsActionJustPressed(ActionToolDig))
        {
            _editMode = EditMode.Dig;
        }
        else if (Input.IsActionJustPressed(ActionToolFill))
        {
            _editMode = EditMode.Fill;
        }
        else if (Input.IsActionJustPressed(ActionToolConcretePad))
        {
            _editMode = EditMode.ConcretePad;
        }

        if (Input.MouseMode == Input.MouseModeEnum.Captured && Input.IsActionJustPressed(ActionApplyTool))
        {
            TryApplyCurrentTool();
        }
    }

    private static void EnsureActionWithPhysicalKey(string action, Key key)
    {
        if (!InputMap.HasAction(action))
        {
            InputMap.AddAction(action);
        }

        var keyEvent = new InputEventKey { PhysicalKeycode = key };
        if (!InputMap.ActionHasEvent(action, keyEvent))
        {
            InputMap.ActionAddEvent(action, keyEvent);
        }
    }

    private static void EnsureActionWithMouseButton(string action, MouseButton button)
    {
        if (!InputMap.HasAction(action))
        {
            InputMap.AddAction(action);
        }

        var mouseEvent = new InputEventMouseButton { ButtonIndex = button };
        if (!InputMap.ActionHasEvent(action, mouseEvent))
        {
            InputMap.ActionAddEvent(action, mouseEvent);
        }
    }

    private void TryApplyCurrentTool()
    {
        if (_streamer is null || !TryGetTerrainHit(out var hit, out var normal))
        {
            return;
        }

        var center = hit + (normal * 0.25f);
        var changed = _editMode switch
        {
            EditMode.Dig => ApplySphericalBrush(center, DigRadius, BrushStrength, add: false, materialWhenSolid: null),
            EditMode.Fill => ApplySphericalBrush(center, FillRadius, BrushStrength, add: true, materialWhenSolid: MaterialType.Dirt),
            EditMode.ConcretePad => ApplyConcretePad(center, ConcreteRadius, center.Y),
            _ => false,
        };

        if (changed)
        {
            // Mesh updates are queued through ChunkMeshed and applied on the next frame.
        }
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

        var result = GetWorld3D().DirectSpaceState.IntersectRay(query);
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
                    if (_chunks.TryGetValue(coord, out var chunk))
                    {
                        density = chunk.GetDensity(x.Local, y.Local, z.Local);
                        material = chunk.GetMaterial(x.Local, y.Local, z.Local);
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
                    if (!_chunks.TryGetValue(coord, out var chunk))
                    {
                        continue;
                    }

                    chunk.SetDensity(x.Local, y.Local, z.Local, density);
                    chunk.SetMaterial(x.Local, y.Local, z.Local, material);
                    touchedChunks.Add(coord);
                    changed = true;
                }
            }
        }

        return changed;
    }

    private void RemeshTouchedChunks(HashSet<ChunkCoord> touchedChunks)
    {
        if (_streamer is null)
        {
            return;
        }

        var remeshSet = new HashSet<ChunkCoord>(touchedChunks);
        foreach (var coord in touchedChunks)
        {
            remeshSet.Add(new ChunkCoord(coord.X + 1, coord.Y, coord.Z));
            remeshSet.Add(new ChunkCoord(coord.X - 1, coord.Y, coord.Z));
            remeshSet.Add(new ChunkCoord(coord.X, coord.Y + 1, coord.Z));
            remeshSet.Add(new ChunkCoord(coord.X, coord.Y - 1, coord.Z));
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
        var chunk = FloorDiv(worldGrid, chunkSize);
        var local = PositiveMod(worldGrid, chunkSize);
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

    private static int FloorDiv(int value, int divisor)
    {
        var result = value / divisor;
        var remainder = value % divisor;
        if (remainder != 0 && ((value < 0) ^ (divisor < 0)))
        {
            result--;
        }

        return result;
    }

    private static int PositiveMod(int value, int modulus)
    {
        var mod = value % modulus;
        return mod < 0 ? mod + modulus : mod;
    }

    private void OnChunkMeshed(DensityChunk chunk, MeshData mesh, int lodStep)
    {
        _pendingChunks[chunk.Coord] = new PendingChunk(chunk, mesh, lodStep);
    }

    private void DrainPendingChunks()
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
        _lastDrainedChunks = drained;
        _lastMeshUploadMs = stopwatch.Elapsed.TotalMilliseconds;
    }

    private void DrainPendingCollisionUpdates()
    {
        if (!_enableCollisionUpdates)
        {
            _lastDrainedCollisions = 0;
            _lastCollisionMs = 0.0;
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
        _lastDrainedCollisions = drained;
        _lastCollisionMs = stopwatch.Elapsed.TotalMilliseconds;
    }

    private void UpdateProfilingHud(float delta)
    {
        _hudTimer += delta;
        if (_hudTimer < 0.2f)
        {
            return;
        }

        _hudTimer = 0f;
        var queue = _generation?.QueueCount ?? 0;
        var inFlight = _generation?.InFlightCount ?? 0;
        var profile = _streamer?.ConsumeProfileSnapshot() ?? new WorldStreamer.ProfileSnapshot(0, 0, 0, 0);
        _statsLine =
            $"Q:{queue} InFlight:{inFlight} PendingMain:{_pendingChunks.Count} " +
            $"CollQ:{_pendingCollisionUpdates.Count} " +
            $"Ready:{profile.ChunksReady} Pub:{profile.MeshesPublished} " +
            $"LODchg:{profile.LodTransitions} Remesh:{profile.RemeshRequests} " +
            $"Drain:{_lastDrainedChunks} Upload:{_lastMeshUploadMs:0.00}ms " +
            $"CollDrain:{_lastDrainedCollisions} Coll:{_lastCollisionMs:0.00}ms";
        _statsLine +=
            $"\nPerf C:{OnOff(_enableCollisionUpdates)} T:{OnOff(_enableLodTransitions)} N:{OnOff(_enableNeighborRemesh)} S:{OnOff(_enableSeamResolve)} R:{OnOff(_enableRegionMap)}";

        PushHistory(_fpsHistory, (float)Engine.GetFramesPerSecond(), 40);
        PushHistory(_queueHistory, queue, 40);
        PushHistory(_uploadHistory, (float)_lastMeshUploadMs, 40);
        PushHistory(_collisionHistory, (float)_lastCollisionMs, 40);

        _statsLine +=
            $"\nFPS   {BuildSparkline(_fpsHistory, 120f)}" +
            $"\nQueue {BuildSparkline(_queueHistory, 80f)}" +
            $"\nUpload {BuildSparkline(_uploadHistory, 12f)}" +
            $"\nColl   {BuildSparkline(_collisionHistory, 6f)}";
    }

    private static void PushHistory(Queue<float> history, float value, int maxSamples)
    {
        history.Enqueue(value);
        while (history.Count > maxSamples)
        {
            history.Dequeue();
        }
    }

    private static string BuildSparkline(Queue<float> samples, float maxExpected)
    {
        if (samples.Count == 0 || maxExpected <= 0f)
        {
            return string.Empty;
        }

        const string glyphs = "▁▂▃▄▅▆▇█";
        var chars = new char[samples.Count];
        var i = 0;
        foreach (var sample in samples)
        {
            var t = Mathf.Clamp(sample / maxExpected, 0f, 1f);
            var index = Mathf.Clamp((int)MathF.Round(t * (glyphs.Length - 1)), 0, glyphs.Length - 1);
            chars[i++] = glyphs[index];
        }

        return new string(chars);
    }

    private static string OnOff(bool value) => value ? "on" : "off";

    private void UpsertChunkMesh(ChunkCoord coord, MeshData meshData, int lodStep)
    {
        if (!_chunkMeshes.TryGetValue(coord, out var meshInstance))
        {
            meshInstance = new MeshInstance3D
            {
                Name = $"Chunk_{coord.X}_{coord.Y}_{coord.Z}",
                Position = new Vector3(
                    coord.X * ChunkSizeX,
                    coord.Y * ChunkSizeY,
                    coord.Z * ChunkSizeZ),
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
            if (_enableCollisionUpdates)
            {
                _pendingCollisionUpdates[coord] = CollisionUpdate.Add(meshData);
            }
        }
        else
        {
            if (_enableCollisionUpdates && _chunkBodies.ContainsKey(coord))
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

    private void ClearAllCollisionBodies()
    {
        foreach (var body in _chunkBodies.Values)
        {
            body.QueueFree();
        }

        _chunkBodies.Clear();
    }

    private void RefreshLodOverlay()
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

    private void ApplyChunkLodOverlay(MeshInstance3D mesh, int lodStep)
    {
        if (!_showLodOverlay)
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
                    coord.X * ChunkSizeX,
                    coord.Y * ChunkSizeY,
                    coord.Z * ChunkSizeZ),
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

    private ArrayMesh BuildGodotMesh(MeshData meshData)
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

    private void PruneUnloadedMeshes()
    {
        if (_streamer is null)
        {
            return;
        }

        // Only prune every 30 frames to avoid per-frame allocation of ~2400 coords.
        if (++_pruneCounter < 30)
        {
            return;
        }

        _pruneCounter = 0;
        var loaded = new HashSet<ChunkCoord>(_streamer.LoadedCoords);
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

    private void HandleCharacterMovement(float delta)
    {
        if (_player.IsOnFloor())
        {
            _velocity.Y = 0f;
            if (Input.IsActionJustPressed(ActionJump))
            {
                _velocity.Y = JumpImpulse;
            }
        }
        else
        {
            _velocity.Y -= Gravity * delta;
            if (_velocity.Y < -MaxFallSpeed)
            {
                _velocity.Y = -MaxFallSpeed;
            }
        }

        var input = Vector2.Zero;
        if (Input.IsActionPressed(ActionMoveForward))
        {
            input.Y -= 1f;
        }

        if (Input.IsActionPressed(ActionMoveBackward))
        {
            input.Y += 1f;
        }

        if (Input.IsActionPressed(ActionMoveLeft))
        {
            input.X -= 1f;
        }

        if (Input.IsActionPressed(ActionMoveRight))
        {
            input.X += 1f;
        }

        var speed = Input.IsActionPressed(ActionSprint) ? SprintSpeed : WalkSpeed;
        var yawBasis = Basis.Identity.Rotated(Vector3.Up, _yaw);
        var direction = yawBasis * new Vector3(input.X, 0f, input.Y);
        if (direction.LengthSquared() > 0f)
        {
            direction = direction.Normalized();
        }

        _velocity.X = direction.X * speed;
        _velocity.Z = direction.Z * speed;

        _player.Velocity = _velocity;
        _player.MoveAndSlide();
        _velocity = _player.Velocity;
    }

    private readonly record struct PendingChunk(DensityChunk Chunk, MeshData Mesh, int LodStep);
    private readonly record struct CollisionUpdate(MeshData? MeshData, bool IsRemove)
    {
        public static CollisionUpdate Add(MeshData meshData) => new(meshData, false);
        public static CollisionUpdate Remove() => new(null, true);
    }

    private readonly record struct AxisOwner(int Chunk, int Local);

    private enum EditMode : byte
    {
        Dig,
        Fill,
        ConcretePad,
    }
}
