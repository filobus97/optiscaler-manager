// OptiScaler Manager - a simple, AMD-focused frontend for the OptiScaler mod.
// Copyright (C) 2026 filobus97
//
// Based on OptiScaler Client (Copyright (C) 2026 Agustín Montaña / Agustinm28).
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System;

namespace OptiscalerManager.Core.Logging;

/// <summary>
/// Ambient logging facade used throughout the ported service layer in place of
/// the source project's <c>DebugWindow.Log</c>. The host injects an
/// <see cref="ILog"/> sink once at startup via <see cref="SetSink"/>; until then
/// (and in tests) writes are silently discarded. Keeping the call sites as a
/// static facade avoids threading an <see cref="ILog"/> through every one of the
/// dozens of service constructors while still removing the UI dependency.
/// </summary>
public static class Log
{
    private static ILog? _sink;

    /// <summary>True when a sink is installed and logging is being captured.</summary>
    public static bool IsEnabled => _sink != null;

    /// <summary>Installs (or clears, with <c>null</c>) the active logging sink.</summary>
    public static void SetSink(ILog? sink) => _sink = sink;

    /// <summary>Writes a diagnostic line to the active sink, if any.</summary>
    public static void Write(string message)
    {
        try { _sink?.Write(message); }
        catch { /* logging must never throw into the caller */ }
    }

    /// <summary>
    /// Writes a lazily-built diagnostic line. The factory is only invoked when a
    /// sink is installed, mirroring the source project's deferred overload so hot
    /// paths pay nothing when logging is off.
    /// </summary>
    public static void Write(Func<string> messageFactory)
    {
        var sink = _sink;
        if (sink == null) return;
        try { sink.Write(messageFactory()); }
        catch { /* logging must never throw into the caller */ }
    }
}
