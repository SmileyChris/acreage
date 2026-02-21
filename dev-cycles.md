Cycle 1: The Bare Metal (Smooth Voxel Generation) — COMPLETE
Goal: Get smooth, infinite terrain rendering efficiently via Marching Cubes.

Tasks:
* Implement density-field chunk storage (DensityChunk with float density + MaterialType arrays).
* Write a Marching Cubes mesher with gradient-interpolated normals and per-vertex material colors.
* Implement 2D simplex noise and a hills terrain generator with layered octaves.
* Reuse multithreaded chunk generation and world streaming from the original blocky system.

Win State: Fly around procedural rolling hills at 60 FPS with smooth, organic terrain and material-colored surfaces (grass/dirt/stone).

Cycle 2: The Physical Interface (Sculpting & Terraforming) — COMPLETE
Goal: Interact with the density field to shape terrain.

Tasks:
* Implement physics raycasting from camera to find terrain hit point on the isosurface.
* Write density modification tools: spherical brush dig (subtract density) and fill (add density with material).
* Trigger mesh rebuild for the modified chunk AND its face neighbors (border samples change).
* Implement manufactured placement: stamp flat density regions with MaterialType.Concrete for smooth-to-flat transitions.
* Add aim indicator (faint line + marker at terrain hit point) for sculpt point visibility.

Win State: Sculpt a drainage trench and flatten a concrete pad.

Cycle 3: The Logistics Rig (The Vehicle)
Goal: Build the primary method of heavy traversal.

Tasks:

Implement Godot's VehicleBody3D node to create a rugged, physics-based side-by-side UTV.

Generate terrain collision shapes from Marching Cubes mesh (ConcavePolygonShape3D from MeshData vertex/index arrays).

Create a physicalized inventory system (e.g., dropping a "scrap metal" item onto the flatbed physically parents the object to the vehicle's physics body).

Win State: Drive a loaded vehicle across rolling, uneven smooth terrain.

Cycle 4: The Entropy Machine (The Overgrowth System)
Goal: Bring the environment to life via density-field evolution.

Tasks:

Create a ticking update loop that operates on surface voxels (density near zero). Exposed surfaces gradually have MaterialType shift (Dirt → organic) and density increases slightly (terrain roughens, becomes overgrown).

Implement the "Secure Node" logic — a powered structure that projects a radius to freeze the overgrowth tick for nearby surface voxels. If power is lost, growth resumes.

Win State: Watch cleared smooth terrain slowly roughen and overgrow, unless a powered node is placed nearby.
