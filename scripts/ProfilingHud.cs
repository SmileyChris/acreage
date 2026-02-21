using System.Collections.Generic;
using Acreage.Voxel;
using Godot;

public sealed class ProfilingHud
{
    private float _hudTimer;
    private string _statsLine = string.Empty;
    private readonly Queue<float> _fpsHistory = new();
    private readonly Queue<float> _queueHistory = new();
    private readonly Queue<float> _uploadHistory = new();
    private readonly Queue<float> _collisionHistory = new();

    public string StatsText => _statsLine;

    public readonly record struct HudSnapshot(
        int QueueCount,
        int InFlightCount,
        int PendingChunkCount,
        int PendingCollisionCount,
        WorldStreamer.ProfileSnapshot Profile,
        int LastDrainedChunks,
        double LastMeshUploadMs,
        int LastDrainedCollisions,
        double LastCollisionMs,
        bool EnableCollision,
        bool EnableLodTransitions,
        bool EnableNeighborRemesh,
        bool EnableSeamResolve,
        bool EnableRegionMap);

    public void Update(float delta, HudSnapshot snapshot)
    {
        _hudTimer += delta;
        if (_hudTimer < 0.2f)
        {
            return;
        }

        _hudTimer = 0f;
        _statsLine =
            $"Q:{snapshot.QueueCount} InFlight:{snapshot.InFlightCount} PendingMain:{snapshot.PendingChunkCount} " +
            $"CollQ:{snapshot.PendingCollisionCount} " +
            $"Ready:{snapshot.Profile.ChunksReady} Pub:{snapshot.Profile.MeshesPublished} " +
            $"LODchg:{snapshot.Profile.LodTransitions} Remesh:{snapshot.Profile.RemeshRequests} " +
            $"Drain:{snapshot.LastDrainedChunks} Upload:{snapshot.LastMeshUploadMs:0.00}ms " +
            $"CollDrain:{snapshot.LastDrainedCollisions} Coll:{snapshot.LastCollisionMs:0.00}ms";
        _statsLine +=
            $"\nPerf C:{OnOff(snapshot.EnableCollision)} T:{OnOff(snapshot.EnableLodTransitions)} N:{OnOff(snapshot.EnableNeighborRemesh)} S:{OnOff(snapshot.EnableSeamResolve)} R:{OnOff(snapshot.EnableRegionMap)}";

        PushHistory(_fpsHistory, (float)Engine.GetFramesPerSecond(), 40);
        PushHistory(_queueHistory, snapshot.QueueCount, 40);
        PushHistory(_uploadHistory, (float)snapshot.LastMeshUploadMs, 40);
        PushHistory(_collisionHistory, (float)snapshot.LastCollisionMs, 40);

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

        const string glyphs = "\u2581\u2582\u2583\u2584\u2585\u2586\u2587\u2588";
        var chars = new char[samples.Count];
        var i = 0;
        foreach (var sample in samples)
        {
            var t = Mathf.Clamp(sample / maxExpected, 0f, 1f);
            var index = Mathf.Clamp((int)System.MathF.Round(t * (glyphs.Length - 1)), 0, glyphs.Length - 1);
            chars[i++] = glyphs[index];
        }

        return new string(chars);
    }

    private static string OnOff(bool value) => value ? "on" : "off";
}
