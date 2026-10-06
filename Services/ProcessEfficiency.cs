using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Museek.Services;

/// <summary>
/// "Efficiency mode": below-normal priority + Windows EcoQoS (power throttling), so the overlay runs on
/// efficiency cores at low clocks and never competes with your game / browser. Best-effort, silent on failure.
/// </summary>
public static class ProcessEfficiency
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE { public uint Version; public uint ControlMask; public uint StateMask; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, int size);

    private const int ProcessPowerThrottling = 4;
    private const uint CurrentVersion = 1, ExecutionSpeed = 1;

    public static void Apply(bool on)
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            p.PriorityClass = on ? ProcessPriorityClass.BelowNormal : ProcessPriorityClass.Normal;
            var state = new PROCESS_POWER_THROTTLING_STATE
            {
                Version = CurrentVersion,
                ControlMask = ExecutionSpeed,
                StateMask = on ? ExecutionSpeed : 0,
            };
            SetProcessInformation(p.Handle, ProcessPowerThrottling, ref state, Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
            Log.Info($"Efficiency mode {(on ? "on" : "off")}");
        }
        catch (Exception ex) { Log.Info($"Efficiency mode not applied: {ex.Message}"); }
    }
}
