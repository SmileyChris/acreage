Product Requirements Document (PRD) - Prototype Phase
Project Title: Project Acreage (Working Title)
Genre: Solo Voxel Sandbox / Reclamation & Engineering Simulator
Target Platform: PC

1. Elevator Pitch
Project Acreage is a solitary, peaceful, yet highly industrious voxel game where the primary adversary is the relentless encroachment of nature. Set in the lush, overgrown ruins of a collapsed high-tech civilization, the player must use heavy machinery, power grids, and advanced horticulture to carve out an estate, pushing back the wilderness to restore order, cultivate rare flora, and archive the lost digital history of the old world.

2. Core Design Pillars
Entropy vs. Order: Nature is not static. If land is not paved, powered, or actively maintained, the overgrowth reclaims it.

Heavy-Duty Logistics: Inventory space is realistically limited. Progress relies on clearing trails and driving modular utility vehicles (UTVs) to haul heavy loads of timber and scrap back to base.

High-Tech Horticulture: Farming is an engineering challenge. Players build climate-controlled voxel greenhouses to cultivate rare heritage seeds.

Curated Archival: Exploration rewards the player with old-world tech, data drives, and retro hardware to clean up, display, and boot up in their secure base.

3. Recommended Tech Stack for Prototype
Game Engine: Godot 4.x

Language: C#

Rationale: Godot combined with C# provides the fastest path to a playable visual prototype. It handles standard physics and UI out of the box, allowing you to focus your custom engineering efforts purely on the C# voxel chunk generation and the dynamic "overgrowth" algorithms.

4. The Core Gameplay Loop
Scout & Chart: Venture into the dense, high-canopy overgrowth to locate heritage seed pods, rusted scrap chassis, or flat terrain.

Clear & Haul: Use chainsaws and manual tools to cut a path, bring in the UTV, load the flatbed with resources, and drive it back to the safe zone.

Build & Secure: Process the scrap into concrete, steel, and cabling. Lay down foundations and networking infrastructure (Power-over-Ethernet networks) to permanently secure the new chunk of land from the overgrowth.

Cultivate & Automate: Plant recovered seeds in engineered greenhouses and write lightweight, Python-style scripts in the in-game terminals to manage automated irrigation pulses, climate control, and grid health.

5. Prototype Scope: Minimum Viable Product (MVP) Features
To prove the concept is fun, the initial prototype should focus entirely on the following systems within a limited, procedurally generated 2-acre map:

A. The Dynamic Voxel System
Stateful Blocks: A ticking update loop where "Wild Dirt" transitions to "Weeds" and then "Brush" over time.

The "Secure" Radius: Placing man-made blocks (Concrete, Powered Nodes) projects a suppression field that halts the overgrowth timer in adjacent chunks.

B. Traversal and Logistics Interface
The UTV: A basic, drivable physics-based vehicle with a flatbed.

Physicalized Storage: Instead of opening a UI menu to dump 999 logs, the player interacts with voxel crates bolted to the UTV, physically managing their payload limits.

C. Base Networking (The "Nerve Center")
Cabling Mechanics: A tool that allows the player to string visible wires between a central generator and remote nodes.

Basic Logic: If a node loses power (e.g., a falling tree breaks the cable), the connected area loses its "Secure" status and overgrowth resumes.

D. The Compendium & Gardening
The UI Logbook: A sleek, satisfying menu screen that logs discovered plant species and recovered digital artifacts.

The First Harvest: One specific type of rare plant that requires a fully enclosed, powered room to grow, serving as the prototype's primary win-state.
