using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TaskMonitoring.EmployeeDesktop;

public sealed class ForegroundActivityCollector
{
    public CapturedApplicationActivity? TryCapture(EmployeeMonitoringPolicyResponse policy)
    {
        if (!policy.IsEnabled || policy.Applications.Count == 0)
        {
            return null;
        }

        try
        {
            var window = GetForegroundWindow();
            if (window == IntPtr.Zero)
            {
                return null;
            }

            _ = GetWindowThreadProcessId(window, out var processId);
            if (processId == 0)
            {
                return null;
            }

            using var process = Process.GetProcessById((int)processId);
            var processName = process.ProcessName.Trim().ToLowerInvariant();
            var rule = policy.Applications.FirstOrDefault(x =>
                x.IsActive && string.Equals(x.ProcessName, processName, StringComparison.OrdinalIgnoreCase));
            if (rule is null)
            {
                return null;
            }

            string? windowTitle = null;
            if (rule.CaptureWindowTitle)
            {
                windowTitle = ReadWindowTitle(window);
            }

            return new CapturedApplicationActivity(processName, windowTitle);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string? ReadWindowTitle(IntPtr window)
    {
        var length = GetWindowTextLength(window);
        if (length <= 0)
        {
            return null;
        }

        var buffer = new StringBuilder(Math.Min(length, 300) + 1);
        _ = GetWindowText(window, buffer, buffer.Capacity);
        var value = buffer.ToString().Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);
}

public sealed record CapturedApplicationActivity(string ProcessName, string? WindowTitle);
