# Acreage

A voxel sandbox where you reclaim overgrown ruins using heavy machinery, power grids, and horticulture. Built with **Godot 4.6** and **C# (.NET 10)**.

## Current State

Cycles 1-2 are complete: smooth voxel terrain via Marching Cubes with multithreaded world streaming, terrain editing tools (dig, fill, concrete pad), chunk persistence (edited terrain saves to disk and reloads across sessions), water, procedural sky, and a third-person box character that walks on the terrain.

### Controls

| Key | Action |
|-----|--------|
| WASD | Move |
| Shift | Sprint |
| Space | Jump |
| Mouse | Look |
| LMB | Apply tool |
| 1 / 2 / 3 | Select tool: dig / fill / concrete pad |
| Esc | Toggle mouse capture |

### Debug/Perf Toggles

| Key | Toggle |
|-----|--------|
| \` | LOD overlay |
| F5 | Collision updates |
| F6 | LOD transitions |
| F7 | Neighbor remesh |
| F8 | Seam resolve |
| F9 | Region map |

## Building

Requires **.NET 10 SDK** and **Godot 4.6** (mono/C# build).

```bash
dotnet build Acreage.csproj
```

Run through the Godot editor or CLI:

```bash
godot --path .
```

## Architecture

The voxel runtime (`src/Voxel/`) is a pure C# library with no Godot dependencies. It handles density-field chunk storage, Marching Cubes meshing, simplex noise terrain generation, and multithreaded world streaming. The Godot integration layer (`scripts/Main.cs`) wires this to the scene tree, converting engine-agnostic mesh data into Godot `ArrayMesh` nodes and collision bodies.

See `CLAUDE.md` for detailed architecture notes and `prd.md` for the full design vision.

## Development Cycles

1. **Smooth Voxel Generation** — Density fields, Marching Cubes, world streaming *(complete)*
2. **Sculpting & Terraforming** — Terrain editing tools, collision bodies *(complete)*
3. **The Logistics Rig** — Physics-based UTV vehicle
4. **The Entropy Machine** — Dynamic overgrowth system
