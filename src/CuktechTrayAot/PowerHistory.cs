using System;

namespace CuktechTray;

/// <summary>一拍采样：时间（单调时钟，秒）+ 四路瓦数。瓦数 &lt; 0 表示这一拍那一路没有数据（断线 / 合并时被并进去的那一路）。</summary>
internal readonly struct PowerPoint
{
    public readonly double T;
    public readonly double W0, W1, W2, W3;

    public PowerPoint(double t, double w0, double w1, double w2, double w3)
    {
        T = t;
        W0 = w0;
        W1 = w1;
        W2 = w2;
        W3 = w3;
    }

    public double At(int i) => i switch { 0 => W0, 1 => W1, 2 => W2, _ => W3 };
}

/// <summary>
/// 内存里的功率历史，面板第四个页签（曲线）用它画四条线。
///
/// 为什么**不写盘**：用户要的是「看一眼刚才的走势」，不是长期记录；真要留数据请用
/// 「单口采集」——那条路每秒一行、结束能导出 CSV。写盘还会带来清理、隐私、权限三堆事，
/// 而这块只是给面板看一眼的。
///
/// 容量：1 Hz × 30 分钟 = 1800 点，到顶丢最老的。每点 5 个 double ≈ 40 B，合计约 72 KB。
/// 时间戳用 Environment.TickCount64（单调时钟），不用 DateTime：改系统时间、夏令时都不会让曲线打结。
/// </summary>
internal static class PowerHistory
{
    /// <summary>最多留多少个点（1 Hz 下 30 分钟）。</summary>
    public const int Capacity = 1800;

    /// <summary>两拍之间太近就不记：设备推送偶尔会连着来两次，记下来只是让曲线多几个重复点。</summary>
    public const double MinStepSeconds = 0.4;

    private static readonly object Gate = new();
    private static readonly List<PowerPoint> Pts = new(Capacity + 16);
    private static double _lastT = double.MinValue;

    /// <summary>当前单调时钟（秒）。</summary>
    public static double Now => Environment.TickCount64 / 1000.0;

    /// <summary>记一拍。四路各自的瓦数按下面的规矩取：</summary>
    public static void Add(Snapshot snap)
    {
        if (snap is null) return;
        double t = Now;
        lock (Gate)
        {
            if (_lastT > double.MinValue && t - _lastT < MinStepSeconds) return;
            if (t < _lastT) { Pts.Clear(); _lastT = double.MinValue; }   // 时钟回退（极端情况）就当重来
            Pts.Add(new PowerPoint(t, Value(snap, 0), Value(snap, 1), Value(snap, 2), Value(snap, 3)));
            if (Pts.Count > Capacity) Pts.RemoveRange(0, Pts.Count - Capacity);
            _lastT = t;
        }
    }

    /// <summary>
    /// 某一拍这一路的瓦数。返回 -1 = 这一拍画不出点（没连上 / 没有读数 / C3+A 合并时被并进去的那一路），
    /// 曲线在那里断开而不是连一条直线过去 —— 连过去会让「拔了线」看着像「功率平滑下降」。
    /// </summary>
    private static double Value(Snapshot snap, int i)
    {
        if (!snap.Connected) return -1;
        var p = snap.Ports[i];
        if (!p.HasData) return -1;
        if (snap.IsMergedFollower(i)) return -1;
        return Math.Max(0, p.Watts);
    }

    public static void Clear()
    {
        lock (Gate) { Pts.Clear(); _lastT = double.MinValue; }
    }

    /// <summary>取最近 windowSeconds 秒内的点（拷贝一份，调用方随便读）。</summary>
    public static List<PowerPoint> Snapshot(double windowSeconds)
    {
        if (windowSeconds < 1) windowSeconds = 1;
        double from = Now - windowSeconds;
        var list = new List<PowerPoint>();
        lock (Gate)
        {
            // 点最多 1800 个，直接扫一遍就够快，不用二分
            for (int i = 0; i < Pts.Count; i++)
            {
                if (Pts[i].T >= from) list.Add(Pts[i]);
            }
        }
        return list;
    }

    /// <summary>--render-chart 用：造一段固定形状的假数据（同一条曲线每次渲染都一样，基线才能比）。</summary>
    public static void SeedDemo()
    {
        lock (Gate)
        {
            Pts.Clear();
            double now = Now;
            var rnd = new Random(20261127);
            for (int i = 180; i >= 0; i--)
            {
                double phase = (180 - i) / 180.0;
                double baseW = 42 + 28 * Math.Sin(phase * Math.PI * 2.1);
                double w0 = Math.Max(0, Math.Round(baseW + rnd.NextDouble() * 4 - 2, 1));
                double w2 = phase > 0.35 && phase < 0.82 ? Math.Round(16 + 7 * Math.Sin(phase * 11), 1) : 0;
                Pts.Add(new PowerPoint(now - i, w0, 0, w2, -1));
            }
            _lastT = now;
        }
    }
}
