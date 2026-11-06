using System.Collections;
using System.Diagnostics;
using System.Globalization;

namespace AltDeck.Services;

/// <summary>GPU use of one process: engine load in percent and dedicated video memory in MB.</summary>
public readonly record struct GpuSample(double Percent, double MemoryMb);

/// <summary>
/// Measures GPU use per process from Windows' own performance counters ("GPU Engine" and "GPU Process Memory"),
/// the same source Task Manager uses. Works for NVIDIA, AMD and Intel on Windows 10 1709 or newer.
/// Reading the counters can take a moment, so it runs on a background thread and the UI only reads the last result.
/// </summary>
public sealed class GpuMonitor
{
    private const string EngineCategory = "GPU Engine";
    private const string MemoryCategory = "GPU Process Memory";

    private readonly object _gate = new();
    private Dictionary<int, GpuSample> _byPid = new();
    private Dictionary<string, CounterSample> _prev = new();
    private int _busy;
    private int _failures;

    /// <summary>False when this PC has no GPU counters (old Windows or driver). Then the UI says so instead of showing 0%.</summary>
    public bool Available { get; private set; } = true;

    /// <summary>Starts a background read (does nothing if the last one is still running).</summary>
    public void Poll()
    {
        if (!Available) return;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        Task.Run(() =>
        {
            try
            {
                Read();
                _failures = 0;
            }
            catch
            {
                // a few hiccups are normal (a process ended while it was being read); only give up when it keeps failing
                if (++_failures >= 5) Available = false;
            }
            finally { Interlocked.Exchange(ref _busy, 0); }
        });
    }

    public bool TryGet(int pid, out GpuSample sample)
    {
        lock (_gate) return _byPid.TryGetValue(pid, out sample);
    }

    private void Read()
    {
        if (!PerformanceCounterCategory.Exists(EngineCategory))
        {
            Available = false;
            return;
        }

        var next = new Dictionary<string, CounterSample>();
        // pid -> engine type (3D, Copy, VideoDecode, ...) -> summed load
        var load = new Dictionary<int, Dictionary<string, double>>();

        var engines = new PerformanceCounterCategory(EngineCategory).ReadCategory();
        if (engines.Contains("Utilization Percentage"))
        {
            foreach (DictionaryEntry de in engines["Utilization Percentage"])
            {
                if (de.Key is not string name || de.Value is not InstanceData data) continue;
                next[name] = data.Sample;
                if (!_prev.TryGetValue(name, out var before)) continue;   // a rate needs two samples

                int pid = PidOf(name);
                if (pid <= 0) continue;

                double v;
                try { v = CounterSample.Calculate(before, data.Sample); }
                catch { continue; }
                if (double.IsNaN(v) || v < 0) continue;

                string type = EngineTypeOf(name);
                if (!load.TryGetValue(pid, out var perType)) load[pid] = perType = new Dictionary<string, double>();
                perType[type] = perType.TryGetValue(type, out var sum) ? sum + v : v;
            }
        }
        _prev = next;

        // video memory per process
        var mem = new Dictionary<int, double>();
        try
        {
            if (PerformanceCounterCategory.Exists(MemoryCategory))
            {
                var m = new PerformanceCounterCategory(MemoryCategory).ReadCategory();
                if (m.Contains("Dedicated Usage"))
                {
                    foreach (DictionaryEntry de in m["Dedicated Usage"])
                    {
                        if (de.Key is not string name || de.Value is not InstanceData data) continue;
                        int pid = PidOf(name);
                        if (pid <= 0) continue;
                        mem[pid] = (mem.TryGetValue(pid, out var cur) ? cur : 0) + data.RawValue / 1048576.0;
                    }
                }
            }
        }
        catch { /* memory is a bonus; the load figure still works without it */ }

        var result = new Dictionary<int, GpuSample>();
        foreach (var pid in load.Keys.Union(mem.Keys))
        {
            // like Task Manager: a process shows the busiest of its engines, not the sum of different kinds
            double pct = load.TryGetValue(pid, out var perType) && perType.Count > 0 ? perType.Values.Max() : 0;
            result[pid] = new GpuSample(Math.Clamp(pct, 0, 100), mem.TryGetValue(pid, out var mb) ? mb : 0);
        }

        lock (_gate) _byPid = result;
    }

    // instance names look like: pid_1234_luid_0x00000000_0x0000D3A5_phys_0_eng_0_engtype_3D
    private static int PidOf(string instance)
    {
        const string key = "pid_";
        int i = instance.IndexOf(key, StringComparison.Ordinal);
        if (i < 0) return 0;
        i += key.Length;
        int j = instance.IndexOf('_', i);
        var text = j < 0 ? instance[i..] : instance[i..j];
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) ? pid : 0;
    }

    private static string EngineTypeOf(string instance)
    {
        const string key = "engtype_";
        int i = instance.IndexOf(key, StringComparison.Ordinal);
        return i < 0 ? "other" : instance[(i + key.Length)..];
    }
}
