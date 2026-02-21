# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Acreage is a voxel sandbox game built with **Godot 4.6** and **C# (.NET 10)**. The player reclaims overgrown ruins using heavy machinery, power grids, and horticulture. See `prd.md` for full design and `dev-cycles.md` for the phased development plan.

Cycles 1–2 are complete. The engine has density-field chunk storage, Marching Cubes meshing, multithreaded background generation, simplex-noise rolling hills, per-vertex material colors, world streaming around a third-person character, terrain sculpting tools (dig, fill, concrete pad), collision bodies, and chunk persistence (edited chunks save to disk and reload across sessions).

## Build & Run

There is no test suite yet. Build and run through the Godot editor:

```bash
# Build the C# solution (requires .NET 10 SDK + Godot 4.6)
dotnet build Acreage.csproj

# Run via Godot CLI (requires desktop environment; --headless won't work)
godot --path .
```

The project file is `Acreage.csproj` (Godot.NET.Sdk/4.6.1, net10.0). The Godot project entry point is `project.godot` with main scene `scenes/Main.tscn`.

## Architecture

### Voxel Runtime (`src/Voxel/`)

Engine-agnostic C# library with no Godot dependencies. All types are in the `Acreage.Voxel` namespace.

- **`DensityChunk`** — Stores a signed density field (`float[]`) and material IDs (`MaterialType[]`) on a `(SizeX+1) × (SizeY+1) × (SizeZ+1)` grid. Positive density = solid, negative = air, zero = surface. Index formula: `gx + GridSizeX * (gz + GridSizeZ * gy)`. Has `IsDirty` flag set by `TerrainEditor` when voxels are modified; `internal` raw array accessors (`RawDensity`, `RawMaterials`) support bulk serialization.
- **`MaterialType`** — `byte` enum: Air(0), Dirt(1), Grass(2), Stone(3), Concrete(4).
- **`ChunkCoord`** — Value type for chunk grid coordinates with proper equality/hashing.
- **`MeshData`** / **`Vector3f`** / **`Vector2f`** — Engine-agnostic mesh output (vertices, normals, UVs, indices, per-vertex colors).
- **`SimplexNoise`** — Standalone 2D simplex noise (Stefan Gustavson algorithm).
- **`MarchingCubesTables`** — Standard Paul Bourke edge/tri lookup tables.
- **`MarchingCubesMesher`** — Static Marching Cubes mesher. Accepts optional `DensitySampler` and `MaterialSampler` delegates for cross-chunk boundary lookups. Computes per-vertex normals via density gradient interpolation and emits vertex colors from material types.
- **`HillsGenerator`** — Implements `IChunkGenerator`; produces rolling hills via layered simplex noise with depth-based material assignment. New terrain generators should implement `IChunkGenerator`.
- **`ChunkStore`** — Binary read/write for chunks to disk. Engine-agnostic (`System.IO`). Saves one file per chunk at `{dir}/{x}_{y}_{z}.chunk` with a version byte, grid dimensions, then raw density floats and material bytes via `MemoryMarshal` for zero-copy bulk I/O. ~92 KB per chunk.
- **`PersistentGenerator`** — `IChunkGenerator` decorator. Checks `ChunkStore.TryLoad()` first; falls back to the inner generator if no saved file exists. Runs on worker threads.
- **`ChunkGenerationService`** — Multithreaded worker pool using `PriorityQueue` + `SemaphoreSlim`. Deduplicates in-flight requests. Fires `ChunkReady` event when a chunk is generated.
- **`WorldStreamer`** — Manages chunk loading/unloading around a focus point. Coordinates generation requests, performs Marching Cubes meshing (with cross-chunk density/material sampling via neighbor lookup), and fires `ChunkMeshed` events. Handles neighbor re-meshing when a new chunk arrives. Accepts an optional `ChunkStore` to save dirty chunks on unload and provides `SaveAllDirty()` for flushing on quit.

### Godot Integration (`scripts/`)

- **`Main`** (`Main.cs`) — `Node3D` attached to `scenes/Main.tscn`. Wires the voxel runtime to Godot:
  1. Creates scene nodes programmatically (third-person camera, player body, directional light, water plane, chunk container, aim indicator).
  2. Initializes `ChunkStore` (save dir: `OS.GetUserDataDir()/chunks`), wraps `HillsGenerator` in `PersistentGenerator`, creates `ChunkGenerationService` (half CPU cores) and `WorldStreamer` (LOD tiers at distances 3/6/9). Flushes all dirty chunks via `SaveAllDirty()` on exit.
  3. Each frame: updates player controller, calls `WorldStreamer.UpdateFocusPosition`, drains pending chunks/collisions to the main thread, updates the aim indicator, and prunes out-of-range mesh nodes.
  4. Converts `MeshData` → Godot `ArrayMesh` (with vertex colors) for rendering. Material uses `VertexColorUseAsAlbedo`.
- **`TerrainEditor`** (`TerrainEditor.cs`) — Terrain sculpting tools. Raycasts from the camera to find the terrain hit point, then modifies densities in a spherical brush (dig/fill) or stamps flat concrete pads. Handles cross-chunk boundary edits, triggers remeshing of affected chunks plus their face neighbors, and marks modified chunks as dirty for persistence.
- **`ChunkRenderer`** (`ChunkRenderer.cs`) — Manages Godot `MeshInstance3D` and `StaticBody3D` nodes for loaded chunks. Drains meshed/collision data from worker threads on the main thread.
- **`PlayerController`** (`PlayerController.cs`) — Third-person character controller with WASD movement, sprint, jump, and mouse-look camera pivot.

### Threading Model

Worker threads (in `ChunkGenerationService`) generate chunk data and the `WorldStreamer` meshes them. Results are enqueued to a `ConcurrentQueue` and consumed on Godot's main thread in `_Process`. Only the main thread touches Godot scene tree nodes.

## Key Conventions

- The voxel runtime in `src/Voxel/` must remain free of Godot dependencies so it can be tested independently.
- Nullable reference types are enabled (`<Nullable>enable</Nullable>`).
- Implicit usings are enabled.
- Chunk dimensions (16×64×16) are constants in `Main.cs`. LOD tiers define render distances (3/6/9 chunks at step 1/2/4).
- Density field grid is `(ChunkSize+1)` on each axis to support Marching Cubes corner sampling without cross-chunk reads for basic geometry.
