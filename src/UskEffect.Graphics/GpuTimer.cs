using Vortice.Direct3D11;

namespace UskEffect.Graphics;

/// <summary>
/// タイムスタンプクエリで GPU 処理時間を計測する (FR-10)。
/// 数フレーム前の結果をノンブロッキングで回収するため、表示値は数フレーム遅れる。
/// </summary>
public sealed class GpuTimer : IDisposable
{
    private const int FrameLatency = 4;

    private sealed class FrameQueries(ID3D11Device device) : IDisposable
    {
        public readonly ID3D11Query Disjoint = device.CreateQuery(new QueryDescription(QueryType.TimestampDisjoint));
        public readonly ID3D11Query Begin = device.CreateQuery(new QueryDescription(QueryType.Timestamp));
        public readonly ID3D11Query Mid = device.CreateQuery(new QueryDescription(QueryType.Timestamp));
        public readonly ID3D11Query End = device.CreateQuery(new QueryDescription(QueryType.Timestamp));
        public bool Pending;
        public bool HasCompute;

        public void Dispose()
        {
            Disjoint.Dispose();
            Begin.Dispose();
            Mid.Dispose();
            End.Dispose();
        }
    }

    private readonly ID3D11DeviceContext _context;
    private readonly FrameQueries[] _frames;
    private int _current;

    /// <summary>直近に計測できたノイズ生成 (Compute) の時間 [ms]。</summary>
    public double ComputeMilliseconds { get; private set; }

    /// <summary>直近に計測できたフレーム全体の GPU 時間 [ms]。</summary>
    public double FrameMilliseconds { get; private set; }

    public GpuTimer(GpuDevice gpu)
    {
        _context = gpu.Context;
        _frames = Enumerable.Range(0, FrameLatency).Select(_ => new FrameQueries(gpu.Device)).ToArray();
    }

    public void BeginFrame()
    {
        Collect();
        var frame = _frames[_current];
        if (frame.Pending)
            return; // まだ回収できていないスロットは再利用しない

        _context.Begin(frame.Disjoint);
        _context.End(frame.Begin);
    }

    /// <summary>Compute 完了地点を記録する。Compute を走らせないフレームでは呼ばない。</summary>
    public void MarkComputeDone()
    {
        var frame = _frames[_current];
        if (frame.Pending)
            return;
        _context.End(frame.Mid);
        frame.HasCompute = true;
    }

    public void EndFrame()
    {
        var frame = _frames[_current];
        if (!frame.Pending)
        {
            _context.End(frame.End);
            _context.End(frame.Disjoint);
            frame.Pending = true;
        }
        _current = (_current + 1) % FrameLatency;
    }

    private void Collect()
    {
        foreach (var frame in _frames)
        {
            if (!frame.Pending)
                continue;
            if (!_context.GetData(frame.Disjoint, AsyncGetDataFlags.DoNotFlush, out QueryDataTimestampDisjoint disjoint))
                continue;
            if (!_context.GetData(frame.End, AsyncGetDataFlags.DoNotFlush, out ulong end) ||
                !_context.GetData(frame.Begin, AsyncGetDataFlags.DoNotFlush, out ulong begin))
                continue;

            if (!disjoint.Disjoint && disjoint.Frequency > 0)
            {
                double toMs = 1000.0 / disjoint.Frequency;
                FrameMilliseconds = (end - begin) * toMs;
                if (frame.HasCompute && _context.GetData(frame.Mid, AsyncGetDataFlags.DoNotFlush, out ulong mid))
                    ComputeMilliseconds = (mid - begin) * toMs;
            }
            frame.Pending = false;
            frame.HasCompute = false;
        }
    }

    public void Dispose()
    {
        foreach (var frame in _frames)
            frame.Dispose();
    }
}
