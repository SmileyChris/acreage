# Performance Priority Order (LOD Transition Pipeline)

This document lists the next optimization steps in order of expected real-world gain for the current Acreage pipeline.

## 1. Near-Only Collision Updates (Highest Gain)

### What
Only create/update `ConcavePolygonShape3D` collision for chunks within a tight radius around the camera (for example, Chebyshev distance <= 2-3 chunks).

### Why it helps
Concave collision updates are expensive on the main thread. Even with throttling, repeated updates during movement can consume frame budget quickly.

### Tradeoff
Far terrain will not have up-to-date collision. That is usually acceptable for fly camera gameplay and for anything beyond immediate interaction range.

## 2. Disable Seam Step Resolve for Far Chunks

### What
Keep seam-safe meshing near the player, but skip seam step forcing at distance (use desired LOD step directly for far chunks).

### Why it helps
Current seam resolving can force finer meshing than desired. That multiplies triangle count and meshing cost where visual benefit is low.

### Tradeoff
Possible minor cracks in far terrain. Usually not noticeable at distance.

## 3. Transition Remesh Cooldown (Separate from LOD Decision Cooldown)

### What
Track last remesh tick per chunk and prevent re-meshing the same chunk too often during LOD transition churn.

### Why it helps
Even when LOD decisions are stable, transition scheduling can still repeatedly hit the same chunks while moving quickly.

### Tradeoff
Slight delay before a chunk visually updates to newest desired detail.

## 4. Demotions on Unload Only

### What
Allow live promotions (coarse -> fine) near camera, but avoid live demotions (fine -> coarse). Let demotion happen naturally when chunks unload/reload.

### Why it helps
Most visible quality wins come from promotions. Demotions consume CPU but add little player-facing value during traversal.

### Tradeoff
Some chunks may remain higher detail longer than necessary until streamed out.

## 5. Pure Millisecond Budget for Main-Thread Mesh Apply

### What
Drive mesh apply entirely by frame-time budget (ms) instead of a fixed count + ms combination.

### Why it helps
Chunk mesh complexity varies heavily. A pure time budget produces more stable frame pacing under bursty updates.

### Tradeoff
Throughput may drop under heavy load, increasing visual update latency. This is usually preferable to FPS collapse.

## Notes

- Current profiling strongly indicates movement-time LOD transition work is the dominant spike source.
- Keep using the runtime perf toggles for A/B verification after each change.
- Recommended implementation sequence: 1 -> 2 -> 3 -> 4 -> 5.
