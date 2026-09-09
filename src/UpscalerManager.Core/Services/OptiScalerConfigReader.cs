// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.Collections.Generic;
using System.IO;
using UpscalerManager.Core.Logging;

namespace UpscalerManager.Core.Services;

/// <summary>One setting, said in words rather than ini syntax.</summary>
/// <param name="Label">What the setting controls, e.g. "Upscaler in use".</param>
/// <param name="Value">What it is currently set to, in plain language.</param>
/// <param name="Source">The ini key behind it, for anyone who wants to check.</param>
public sealed record ConfigFact(string Label, string Value, string Source);

/// <summary>
/// Reads a game's OptiScaler.ini and says what it will actually do.
///
/// The point is not to mirror the ini — OptiScaler's own overlay does that better —
/// but to answer the questions a player has after installing: is FSR 4 really going
/// to be used, is frame generation on, is the Nvidia override active. Anything left
/// at "auto" is reported as OptiScaler's choice rather than hidden, because "auto"
/// is a real answer: it is why the wrong upscaler can end up running.
/// </summary>
public static class OptiScalerConfigReader
{
    /// <summary>True when the game has an OptiScaler.ini to read.</summary>
    public static bool Exists(string gameDir) => File.Exists(Path.Combine(gameDir, "OptiScaler.ini"));

    /// <summary>Reads the settings that decide behaviour. Empty when there is no ini.</summary>
    public static IReadOnlyList<ConfigFact> Read(string gameDir)
    {
        var ini = Parse(Path.Combine(gameDir, "OptiScaler.ini"));
        if (ini.Count == 0) return Array.Empty<ConfigFact>();

        string Get(string section, string key) =>
            ini.TryGetValue(section, out var s) && s.TryGetValue(key, out var v) ? v : "auto";

        var facts = new List<ConfigFact>
        {
            new("Upscaler in use", DescribeUpscaler(Get("Upscalers", "Dx12Upscaler")),
                "[Upscalers] Dx12Upscaler"),
            new("Upgrade FSR 3 to FSR 4", OnOffAuto(Get("FSR", "Fsr4Update"),
                    on: "Yes", off: "No", auto: "Only on GPUs OptiScaler considers capable"),
                "[FSR] Fsr4Update"),
            new("Force FSR 4 on unsupported GPUs", OnOffAuto(Get("FSR", "Fsr4ForceEnableInt8"),
                    on: "Yes (INT8 mode)", off: "No", auto: "No"),
                "[FSR] Fsr4ForceEnableInt8"),
            new("Frame generation", DescribeFrameGen(Get("FrameGen", "FGInput")),
                "[FrameGen] FGInput"),
            new("Nvidia override", DescribeSpoof(Get("Spoofing", "Dxgi"), Get("Plugins", "LoadAsiPlugins")),
                "[Spoofing] Dxgi / [Plugins] LoadAsiPlugins"),
            new("Overlay key", DescribeKey(Get("Menu", "ShortcutKey")),
                "[Menu] ShortcutKey"),
        };
        return facts;
    }

    private static string DescribeUpscaler(string value) => value.ToLowerInvariant() switch
    {
        "auto" => "Chosen by OptiScaler — on DX12 that means XeSS unless the release picks otherwise",
        // Both spellings mean the FidelityFX path; the name changed between releases.
        "ffx" or "fsr31" => "FSR (this is the one that can be FSR 4)",
        "ffx_12" or "fsr31_12" => "FSR, through a DX12 compatibility layer",
        "xess" or "xess_12" => "XeSS",
        "fsr21" or "fsr21_12" => "FSR 2.1",
        "fsr22" or "fsr22_12" => "FSR 2.2",
        "dlss" => "DLSS",
        _ => value,
    };

    private static string DescribeFrameGen(string value) => value.ToLowerInvariant() switch
    {
        "auto" or "nofg" => "Off",
        "nukems" => "Nukem's DLSSG-to-FSR3 mod",
        "fsrfg" or "fsrfg30" => "AMD FSR frame generation",
        "dlssg" => "Nvidia DLSS frame generation",
        "upscaler" => "Driven by the upscaler",
        _ => value,
    };

    private static string DescribeSpoof(string dxgi, string plugins)
    {
        var onDxgi = IsTrue(dxgi);
        var onPlugins = IsTrue(plugins);
        if (onDxgi && onPlugins) return "On (DXGI spoofing and OptiPatcher)";
        if (onDxgi) return "On (reports the GPU as an RTX 4090)";
        if (onPlugins) return "On (OptiPatcher plugin)";
        return dxgi.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? "OptiScaler's default (on for AMD and Intel)"
            : "Off";
    }

    private static string DescribeKey(string value)
    {
        if (value.Equals("auto", StringComparison.OrdinalIgnoreCase)) return "Insert (OptiScaler's default)";
        return value.ToLowerInvariant() switch
        {
            "0x2d" => "Insert", "0x24" => "Home", "0x23" => "End", "0x2e" => "Delete",
            "0x21" => "Page Up", "0x22" => "Page Down",
            "0x70" => "F1", "0x71" => "F2", "0x72" => "F3", "0x73" => "F4",
            "0x74" => "F5", "0x75" => "F6", "0x76" => "F7", "0x77" => "F8",
            "0x78" => "F9", "0x79" => "F10", "0x7a" => "F11", "0x7b" => "F12",
            _ => value,
        };
    }

    private static string OnOffAuto(string value, string on, string off, string auto)
    {
        if (value.Equals("auto", StringComparison.OrdinalIgnoreCase)) return auto;
        return IsTrue(value) ? on : off;
    }

    private static bool IsTrue(string value) => value.Equals("true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Minimal ini parse: section -> key -> value, comments and blanks skipped.</summary>
    private static Dictionary<string, Dictionary<string, string>> Parse(string path)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return result;

        try
        {
            var section = "";
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue;

                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    section = line[1..^1].Trim();
                    continue;
                }

                var eq = line.IndexOf('=');
                if (eq <= 0) continue;

                if (!result.TryGetValue(section, out var keys))
                    result[section] = keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                keys[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
        }
        catch (Exception ex)
        {
            Log.Write($"[Config] Could not read {path}: {ex.Message}");
        }

        return result;
    }
}
