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
    private const string ActionMoveForward = "move_forward";
    private const string ActionMoveBackward = "move_backward";
    private const string ActionMoveLeft = "move_left";
    private const string ActionMoveRight = "move_right";
    private const string ActionSprint = "move_sprint";
    private const string ActionJump = "move_jump";
    private const string ActionToggleMouseCapture = "ui_toggle_mouse_capture";
    private const string ActionToggleLodOverlay = "debug_toggle_lod_overlay";
    private const string ActionTogglePerfCollision = "perf_toggle_collision";
    private const string ActionTogglePerfLodTransitions = "perf_toggle_lod_transitions";
    private const string ActionTogglePerfNeighborRemesh = "perf_toggle_neighbor_remesh";
    private const string ActionTogglePerfSeamResolve = "perf_toggle_seam_resolve";
    private const string ActionTogglePerfRegionMap = "perf_toggle_region_map";

    private const float PlayerHeight = 1.8f;
    private const float PlayerWidth = 0.8f;
    private static readonly Vector3 CameraArmOffset = new(0f, 3f, 8f);

    private Node3D _chunkRoot = null!;
    private ChunkRenderer _chunkRenderer = null!;
    private MeshInstance3D _waterPlane = null!;
    private Camera3D _camera = null!;
    private CharacterBody3D _player = null!;
    private Node3D _cameraPivot = null!;
    private PlayerController _playerController = null!;
    private HillsGenerator? _generator;
    private ChunkGenerationService? _generation;
    private WorldStreamer? _streamer;
    private Label _fpsLabel = null!;
    private ProfilingHud _profilingHud = new();
    private TerrainEditor _terrainEditor = null!;
    private bool _enableCollisionUpdates = true;
    private bool _enableLodTransitions = true;
    private bool _enableNeighborRemesh = true;
    private bool _enableSeamResolve = false;
    private bool _enableRegionMap = true;

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
            _streamer.ChunkMeshed -= _chunkRenderer.OnChunkMeshed;
        }

        _generation?.Dispose();
    }

    public override void _Process(double delta)
    {
        HandleActionHotkeys();
        _playerController.Update((float)delta);

        var forward = _camera.GlobalTransform.Basis * Vector3.Forward;
        _streamer?.UpdateFocusPosition(
            _player.GlobalPosition.X,
            _player.GlobalPosition.Y,
            _player.GlobalPosition.Z,
            ChunkSizeX,
            ChunkSizeY,
            ChunkSizeZ,
            forward.X,
            forward.Z);

        _waterPlane.GlobalPosition = new Vector3(_camera.GlobalPosition.X, WaterLevel, _camera.GlobalPosition.Z);

        _chunkRenderer.DrainPendingChunks();
        _chunkRenderer.DrainPendingCollisionUpdates();
        _profilingHud.Update((float)delta, new ProfilingHud.HudSnapshot(
            _generation?.QueueCount ?? 0,
            _generation?.InFlightCount ?? 0,
            _chunkRenderer.PendingChunkCount,
            _chunkRenderer.PendingCollisionCount,
            _streamer?.ConsumeProfileSnapshot() ?? new WorldStreamer.ProfileSnapshot(0, 0, 0, 0),
            _chunkRenderer.LastDrainedChunks,
            _chunkRenderer.LastMeshUploadMs,
            _chunkRenderer.LastDrainedCollisions,
            _chunkRenderer.LastCollisionMs,
            _enableCollisionUpdates,
            _enableLodTransitions,
            _enableNeighborRemesh,
            _enableSeamResolve,
            _enableRegionMap));
        if (_streamer is not null)
        {
            _chunkRenderer.PruneUnloadedMeshes(_streamer);
        }
        _fpsLabel.Text = $"FPS: {Engine.GetFramesPerSecond()} | Tool: {_terrainEditor.CurrentEditMode}\n{_profilingHud.StatsText}";
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseMotion motion && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            _playerController.HandleMouseMotion(motion);
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
        _playerController = new PlayerController(_player, _cameraPivot);

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

        var terrainMaterial = new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            Roughness = 1.0f,
        };
        _chunkRenderer = new ChunkRenderer(_chunkRoot, terrainMaterial, ChunkSizeX, ChunkSizeY, ChunkSizeZ);

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
        _generator = new HillsGenerator(_enableRegionMap);
        var workerCount = Mathf.Max(1, System.Environment.ProcessorCount / 2);
        _generation = new ChunkGenerationService(_generator, workerCount, ChunkSizeX, ChunkSizeY, ChunkSizeZ);
        _streamer = new WorldStreamer(_generation, LodTiers);
        ApplyStreamerPerfSettings();
        _streamer.ChunkMeshed += _chunkRenderer.OnChunkMeshed;
        _terrainEditor = new TerrainEditor(_camera, _player, _chunkRenderer, _streamer, () => GetWorld3D().DirectSpaceState);
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
        EnsureActionWithPhysicalKey("tool_select_dig", Key.Key1);
        EnsureActionWithPhysicalKey("tool_select_fill", Key.Key2);
        EnsureActionWithPhysicalKey("tool_select_concrete_pad", Key.Key3);
        EnsureActionWithPhysicalKey(ActionToggleLodOverlay, Key.Quoteleft);
        EnsureActionWithPhysicalKey(ActionTogglePerfCollision, Key.F5);
        EnsureActionWithPhysicalKey(ActionTogglePerfLodTransitions, Key.F6);
        EnsureActionWithPhysicalKey(ActionTogglePerfNeighborRemesh, Key.F7);
        EnsureActionWithPhysicalKey(ActionTogglePerfSeamResolve, Key.F8);
        EnsureActionWithPhysicalKey(ActionTogglePerfRegionMap, Key.F9);
        EnsureActionWithMouseButton("tool_apply", MouseButton.Left);
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
            _chunkRenderer.ShowLodOverlay = !_chunkRenderer.ShowLodOverlay;
            _chunkRenderer.RefreshLodOverlay();
            GD.Print($"LOD overlay: {(_chunkRenderer.ShowLodOverlay ? "ON" : "OFF")}");
        }

        if (Input.IsActionJustPressed(ActionTogglePerfCollision))
        {
            _enableCollisionUpdates = !_enableCollisionUpdates;
            _chunkRenderer.EnableCollisionUpdates = _enableCollisionUpdates;
            if (!_enableCollisionUpdates)
            {
                _chunkRenderer.ClearPendingCollisionUpdates();
                _chunkRenderer.ClearAllCollisionBodies();
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
            if (_generator is not null)
            {
                _generator.UseRegionMap = _enableRegionMap;
            }
            GD.Print($"Perf: region map {(_enableRegionMap ? "ON" : "OFF")} (new chunks)");
        }

        _terrainEditor.HandleToolHotkeys();
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
}
