namespace Acreage.Voxel;

public sealed class ChunkGenerationService : System.IDisposable
{
    private readonly IChunkGenerator _generator;
    private readonly System.Collections.Generic.PriorityQueue<ChunkCoord, float> _queue = new();
    private readonly object _queueLock = new();
    private readonly System.Threading.SemaphoreSlim _semaphore = new(0);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ChunkCoord, byte> _inFlight = new();
    private readonly System.Collections.Generic.List<System.Threading.Tasks.Task> _workers;
    private readonly System.Threading.CancellationTokenSource _cts = new();
    private readonly int _sizeX;
    private readonly int _sizeY;
    private readonly int _sizeZ;
    private int _stopped;

    public ChunkGenerationService(IChunkGenerator generator, int workerCount, int chunkSizeX = 16, int chunkSizeY = 64, int chunkSizeZ = 16)
    {
        if (workerCount <= 0)
        {
            throw new System.ArgumentOutOfRangeException(nameof(workerCount), "Must have at least one worker.");
        }

        _generator = generator;
        _sizeX = chunkSizeX;
        _sizeY = chunkSizeY;
        _sizeZ = chunkSizeZ;
        _workers = new System.Collections.Generic.List<System.Threading.Tasks.Task>(workerCount);

        for (var i = 0; i < workerCount; i++)
        {
            _workers.Add(System.Threading.Tasks.Task.Run(WorkerLoop));
        }
    }

    public event System.Action<DensityChunk>? ChunkReady;

    public int InFlightCount => _inFlight.Count;

    public int QueueCount
    {
        get
        {
            lock (_queueLock)
            {
                return _queue.Count;
            }
        }
    }

    public bool Enqueue(ChunkCoord coord, float priority = float.MaxValue)
    {
        if (!_inFlight.TryAdd(coord, 0))
        {
            return false;
        }

        lock (_queueLock)
        {
            _queue.Enqueue(coord, priority);
        }

        _semaphore.Release();
        return true;
    }

    public async System.Threading.Tasks.Task StopAsync()
    {
        if (System.Threading.Interlocked.Exchange(ref _stopped, 1) == 1)
        {
            return;
        }

        _cts.Cancel();
        try
        {
            await System.Threading.Tasks.Task.WhenAll(_workers).ConfigureAwait(false);
        }
        catch (System.OperationCanceledException)
        {
            // Expected on shutdown.
        }
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
        _cts.Dispose();
        _semaphore.Dispose();
    }

    private async System.Threading.Tasks.Task WorkerLoop()
    {
        try
        {
            while (true)
            {
                await _semaphore.WaitAsync(_cts.Token).ConfigureAwait(false);

                ChunkCoord coord;
                lock (_queueLock)
                {
                    if (!_queue.TryDequeue(out coord, out _))
                    {
                        continue;
                    }
                }

                DensityChunk? chunk = null;
                try
                {
                    chunk = _generator.Generate(coord, _sizeX, _sizeY, _sizeZ);
                }
                catch
                {
                    // Keep workers alive even if one chunk generation fails.
                }
                finally
                {
                    _inFlight.TryRemove(coord, out _);
                }

                if (chunk is not null)
                {
                    ChunkReady?.Invoke(chunk);
                }
            }
        }
        catch (System.OperationCanceledException)
        {
            // Shutdown path.
        }
    }
}
