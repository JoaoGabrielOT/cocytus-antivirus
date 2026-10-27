using System.Diagnostics;
using Cocytus.Maintenance.Native;

namespace Cocytus.Maintenance;

public sealed record DriveUsage(string Name, string Label, string Format, long TotalBytes, long FreeBytes)
{
    public long UsedBytes => TotalBytes - FreeBytes;

    public double UsedPercent => TotalBytes <= 0 ? 0 : 100.0 * UsedBytes / TotalBytes;
}

public sealed record MemoryUsage(ulong TotalBytes, ulong AvailableBytes, uint LoadPercent)
{
    public ulong UsedBytes => TotalBytes - AvailableBytes;
}

/// <summary>Programa agrupado pelo nome do processo (ex.: todas as janelas do navegador juntas).</summary>
public sealed record ProcessUsage(string Name, int Count, long WorkingSetBytes, double CpuPercent);

public sealed record UsageSnapshot(IReadOnlyList<DriveUsage> Drives, MemoryUsage Memory, IReadOnlyList<ProcessUsage> Processes, DateTimeOffset At);

/// <summary>Painel de uso: espaço em disco, memória e programas que mais consomem. Só leitura.</summary>
public static class SystemUsage
{
    /// <param name="cpuSample">Intervalo para medir o uso de CPU (duas leituras do tempo de processador).</param>
    public static async Task<UsageSnapshot> CaptureAsync(TimeSpan cpuSample, CancellationToken cancellationToken)
    {
        var drives = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed)
            .Select(TryRead)
            .OfType<DriveUsage>()
            .ToList();
        var (total, available, load) = MaintenanceNative.GetMemoryStatus();

        var first = CpuTimes();
        var clock = Stopwatch.StartNew();
        await Task.Delay(cpuSample, cancellationToken);
        var second = CpuTimes();
        var elapsed = clock.Elapsed.TotalMilliseconds * Environment.ProcessorCount;

        var processes = new List<ProcessUsage>();
        foreach (var group in Process.GetProcesses().GroupBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase))
        {
            long memory = 0;
            double cpuMs = 0;
            var count = 0;
            foreach (var process in group)
            {
                using (process)
                {
                    if (process.Id == 0)
                    {
                        continue;
                    }

                    count++;
                    try
                    {
                        memory += process.WorkingSet64;
                    }
                    catch (InvalidOperationException)
                    {
                        // O processo terminou.
                    }

                    if (first.TryGetValue(process.Id, out var before) && second.TryGetValue(process.Id, out var after) && after >= before)
                    {
                        cpuMs += (after - before).TotalMilliseconds;
                    }
                }
            }

            if (count > 0)
            {
                processes.Add(new ProcessUsage(group.Key, count, memory, elapsed <= 0 ? 0 : Math.Min(100, 100 * cpuMs / elapsed)));
            }
        }

        return new UsageSnapshot(drives, new MemoryUsage(total, available, load), processes, DateTimeOffset.Now);
    }

    private static DriveUsage? TryRead(DriveInfo drive)
    {
        try
        {
            return drive.IsReady
                ? new DriveUsage(drive.Name, drive.VolumeLabel, drive.DriveFormat, drive.TotalSize, drive.AvailableFreeSpace)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static Dictionary<int, TimeSpan> CpuTimes()
    {
        var times = new Dictionary<int, TimeSpan>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    times[process.Id] = process.TotalProcessorTime;
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // Processo protegido ou já encerrado.
                }
            }
        }

        return times;
    }
}
