# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Acreage is a voxel sandbox game built with **Godot 4.6** and **C# (.NET 10)**. The player reclaims overgrown ruins using heavy machinery, power grids, and horticulture. See `prd.md` for full design and `dev-cycles.md` for the phased development plan.

The project is currently in **Cycle 1** (smooth voxel generation). Cycle 1 is implemented: density-field chunk storage, Marching Cubes meshing, multithreaded background generation, simplex-noise rolling hills, per-vertex material colors, and world streaming around a fly camera.

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

- **`DensityChunk`** — Stores a signed density field (`float[]`) and material IDs (`MaterialType[]`) on a `(SizeX+1) × (SizeY+1) × (SizeZ+1)` grid. Positive density = solid, negative = air, zero = surface. Index formula: `gx + GridSizeX * (gz + GridSizeZ * gy)`.
- **`MaterialType`** — `byte` enum: Air(0), Dirt(1), Grass(2), Stone(3), Concrete(4).
- **`ChunkCoord`** — Value type for chunk grid coordinates with proper equality/hashing.
- **`MeshData`** / **`Vector3f`** / **`Vector2f`** — Engine-agnostic mesh output (vertices, normals, UVs, indices, per-vertex colors).
- **`SimplexNoise`** — Standalone 2D simplex noise (Stefan Gustavson algorithm).
- **`MarchingCubesTables`** — Standard Paul Bourke edge/tri lookup tables.
- **`MarchingCubesMesher`** — Static Marching Cubes mesher. Accepts optional `DensitySampler` and `MaterialSampler` delegates for cross-chunk boundary lookups. Computes per-vertex normals via density gradient interpolation and emits vertex colors from material types.
- **`HillsGenerator`** — Implements `IChunkGenerator`; produces rolling hills via layered simplex noise with depth-based material assignment. New terrain generators should implement `IChunkGenerator`.
- **`ChunkGenerationService`** — Multithreaded worker pool using `System.Threading.Channels`. Deduplicates in-flight requests. Fires `ChunkReady` event when a chunk is generated.
- **`WorldStreamer`** — Manages chunk loading/unloading around a focus point. Coordinates generation requests, performs Marching Cubes meshing (with cross-chunk density/material sampling via neighbor lookup), and fires `ChunkMeshed` events. Handles neighbor re-meshing when a new chunk arrives.

### Godot Integration (`scripts/Main.cs`)

`Main` is a `Node3D` attached to `scenes/Main.tscn`. It wires the voxel runtime to Godot:

1. Creates scene nodes programmatically (fly camera, directional light, chunk container).
2. Initializes `ChunkGenerationService` (half CPU cores) and `WorldStreamer` (render distance 8).
3. Each frame: updates fly camera, calls `WorldStreamer.UpdateFocusPosition`, drains a `ConcurrentQueue<PendingChunk>` to marshal meshed chunks from worker threads to the main thread, and prunes out-of-range mesh nodes.
4. Converts `MeshData` → Godot `ArrayMesh` (with vertex colors) for rendering. Material uses `VertexColorUseAsAlbedo`.

### Threading Model

Worker threads (in `ChunkGenerationService`) generate chunk data and the `WorldStreamer` meshes them. Results are enqueued to a `ConcurrentQueue` and consumed on Godot's main thread in `_Process`. Only the main thread touches Godot scene tree nodes.

## Key Conventions

- The voxel runtime in `src/Voxel/` must remain free of Godot dependencies so it can be tested independently.
- Nullable reference types are enabled (`<Nullable>enable</Nullable>`).
- Implicit usings are enabled.
- Chunk dimensions (16×64×16) and render distance (8) are constants in `Main.cs`.
- Density field grid is `(ChunkSize+1)` on each axis to support Marching Cubes corner sampling without cross-chunk reads for basic geometry.
