# Cycle 1 Implementation (Smooth Voxel Generation)

This repository includes a portable C# voxel runtime in `src/Voxel` for Cycle 1, using a Marching Cubes density-field system for organic terrain.

## Implemented

- Density-field chunk storage: `DensityChunk` with parallel `float[]` density and `MaterialType[]` material arrays on a `(SizeX+1) × (SizeY+1) × (SizeZ+1)` grid.
- Marching Cubes meshing: `MarchingCubesMesher.Build(DensityChunk)` generates smooth isosurface meshes with per-vertex normals via density gradient interpolation.
- Background generation: `ChunkGenerationService` uses worker tasks and a channel queue.
- Hills terrain source: `HillsGenerator` uses layered simplex noise for rolling terrain with material assignment by depth (Grass/Dirt/Stone).
- Streaming shell: `WorldStreamer` requests and unloads chunks around a moving focus, with cross-chunk density/material sampling for seamless boundaries.
- Per-vertex material colors: `MarchingCubesMesher` emits vertex colors based on `MaterialType`; rendered via `VertexColorUseAsAlbedo`.

## Files

- `src/Voxel/MaterialType.cs` — Material enum (Air, Dirt, Grass, Stone, Concrete)
- `src/Voxel/ChunkCoord.cs` — Chunk grid coordinate value type
- `src/Voxel/DensityChunk.cs` — Density-field chunk storage
- `src/Voxel/MeshData.cs` — Engine-agnostic mesh output (vertices, normals, UVs, indices, colors)
- `src/Voxel/SimplexNoise.cs` — Standalone 2D simplex noise
- `src/Voxel/MarchingCubesTables.cs` — Standard Paul Bourke lookup tables
- `src/Voxel/MarchingCubesMesher.cs` — Marching Cubes mesher with gradient normals and vertex colors
- `src/Voxel/IChunkGenerator.cs` — Generator interface
- `src/Voxel/HillsGenerator.cs` — Simplex noise terrain generator
- `src/Voxel/ChunkGenerationService.cs` — Multithreaded generation worker pool
- `src/Voxel/WorldStreamer.cs` — Chunk streaming and cross-chunk sampling

## Godot Integration

`scripts/Main.cs` wires the runtime to Godot:

- Creates a fly camera + sun.
- Starts `ChunkGenerationService` and `WorldStreamer`.
- Streams chunks around camera position each frame.
- Marshals chunk mesh results from worker threads to the main thread via queue.
- Converts `MeshData` to Godot `ArrayMesh` (with vertex colors) and renders per chunk.
- Unloads chunk nodes when they leave render distance.

### Controls

- Look: mouse (when captured)
- Move: `W/A/S/D`
- Vertical: `Q/E`
- Fast move: `Shift`
- Toggle cursor capture: `Esc`

## Notes

- Runtime is intentionally engine-agnostic so it can be tested outside Godot.
- Validate by opening in the desktop Godot editor; `godot --headless` is unsupported.
