// OptiScaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.Diagnostics;
using OptiscalerManager.Core.Logging;

namespace OptiscalerManager.App.Infrastructure;

/// <summary>
/// Minimal <see cref="ILog"/> sink for the app: forwards the ported service
/// layer's diagnostics to the debugger/console. The source project routed these
/// into a DebugWindow; the Manager keeps it lean and just traces them.
/// </summary>
public sealed class UiLog : ILog
{
    public void Write(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        Debug.WriteLine(line);
        Console.WriteLine(line);
    }
}
