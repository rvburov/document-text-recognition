using System.ComponentModel;
using System.Diagnostics;
using System.Security;

namespace DocumentTextRecognition.Core.Rename;

/// <summary>
/// Accounting for OCR children owned by this process. Entries exist only while their client
/// is alive; retired children contribute a CPU total, not retained Process instances.
/// The benchmark samples this together with the parent's own counters.
/// </summary>
internal static class WorkerProcessMetrics
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<long, Entry> Active = [];
    private static long _nextId, _startedCount;
    private static double _retiredCpuSeconds;
    private static long _retiredWithoutFinalCpuCount;
    private static long _sampleFailures;

    internal readonly record struct Snapshot(double TotalChildCpuSeconds, long CurrentWorkingSetBytes,
        int CurrentThreads, int ActiveCount, long StartedCount, long RetiredWithoutFinalCpuCount,
        long SampleFailures);

    /// <summary>
    /// Register immediately after a successful Process.Start. The caller retains ownership
    /// of the Process and must complete this registration before disposing it.
    /// </summary>
    internal static Registration Register(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        lock (Gate)
        {
            var id = ++_nextId;
            var entry = new Entry(process);
            Sample(entry);
            Active.Add(id, entry);
            _startedCount++;
            return new Registration(id);
        }
    }

    /// <summary>
    /// CPU is cumulative since each child's start. Current RSS/threads sum only currently
    /// accessible live children. ActiveCount counts registered clients, including the short
    /// exit-to-cleanup interval. A missing OS counter never aborts OCR.
    /// </summary>
    internal static Snapshot Capture()
    {
        lock (Gate)
        {
            var cpu = _retiredCpuSeconds;
            long workingSet = 0;
            var threads = 0;
            foreach (var entry in Active.Values)
            {
                Sample(entry);
                cpu += entry.LastCpuSeconds;
                workingSet += entry.CurrentWorkingSetBytes;
                threads += entry.CurrentThreads;
            }
            return new Snapshot(cpu, workingSet, threads, Active.Count, _startedCount,
                _retiredWithoutFinalCpuCount, _sampleFailures);
        }
    }

    /// <summary>Bounded diagnostic snapshot for lifecycle checks; no retired PID history.</summary>
    internal static int[] GetActiveProcessIds()
    {
        lock (Gate) return Active.Values.Select(entry => entry.ProcessId).ToArray();
    }

    internal sealed class Registration(long id) : IDisposable
    {
        /// <summary>
        /// Preserve the best available final CPU reading: child's graceful-stop report,
        /// OS counter, or last successful sample. On Linux /proc may disappear before the
        /// parent observes exit; without either final reading, CPU can miss the last sample
        /// interval. RetiredWithoutFinalCpuCount explicitly records that case.
        /// </summary>
        internal void Complete(double? finalChildCpuSeconds = null)
        {
            lock (Gate)
            {
                if (!Active.Remove(id, out var entry)) return;
                var finalAvailable = Sample(entry);
                if (finalChildCpuSeconds is { } reported && double.IsFinite(reported) && reported >= 0)
                {
                    entry.LastCpuSeconds = Math.Max(entry.LastCpuSeconds, reported);
                    finalAvailable = true;
                }
                if (!finalAvailable) _retiredWithoutFinalCpuCount++;
                _retiredCpuSeconds += entry.LastCpuSeconds;
            }
        }

        public void Dispose() => Complete();
    }

    private sealed class Entry(Process process)
    {
        internal readonly Process Process = process;
        internal readonly int ProcessId = process.Id;
        internal double LastCpuSeconds;
        internal long CurrentWorkingSetBytes;
        internal int CurrentThreads;
    }

    // Return whether this invocation obtained an OS CPU reading, independently of whether
    // RSS/thread counters remained available during a concurrent child exit.
    private static bool Sample(Entry entry)
    {
        entry.CurrentWorkingSetBytes = 0;
        entry.CurrentThreads = 0;
        var cpuAvailable = false;
        try
        {
            entry.Process.Refresh();
            var cpu = entry.Process.TotalProcessorTime.TotalSeconds;
            if (double.IsFinite(cpu) && cpu >= 0)
            {
                entry.LastCpuSeconds = Math.Max(entry.LastCpuSeconds, cpu);
                cpuAvailable = true;
            }
            if (!entry.Process.HasExited)
            {
                entry.CurrentWorkingSetBytes = Math.Max(0, entry.Process.WorkingSet64);
                entry.CurrentThreads = entry.Process.Threads.Count;
            }
        }
        catch (Exception ex) when (Unavailable(ex))
        {
            _sampleFailures++;
        }
        return cpuAvailable;
    }

    private static bool Unavailable(Exception ex) => ex is InvalidOperationException or
        Win32Exception or NotSupportedException or SecurityException or IOException;
}
