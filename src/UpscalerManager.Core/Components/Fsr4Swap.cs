// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.

namespace UpscalerManager.Core.Components;

/// <summary>A FidelityFX DLL on disk, and what it is — by its contents, not only its name.</summary>
/// <param name="FullPath">Where it sits.</param>
/// <param name="Role">What it is in AMD's runtime.</param>
/// <param name="Version">Its file version.</param>
/// <param name="FsrVersion">The FSR version in its provider table, when it was read.</param>
public sealed record FidelityFxFile(string FullPath, FidelityFxRole Role, string? Version, string? FsrVersion)
{
    public string Directory => Path.GetDirectoryName(FullPath) ?? string.Empty;

    public string FileName => Path.GetFileName(FullPath);

    /// <summary>The version as a player should read it, labelled by what the file is.</summary>
    public string VersionText => FidelityFxLayout.Describe(Role, Version, FsrVersion);
}

/// <summary>What one written file is for.</summary>
public enum Fsr4StepKind
{
    /// <summary>A game's SDK 2 upscaler module, replaced by the FSR 4 build.</summary>
    ReplaceUpscaler,

    /// <summary>A game's SDK 1 runtime, replaced by the SDK 2 loader under the same name.</summary>
    ReplaceRuntimeWithLoader,

    /// <summary>A game's SDK 1 runtime, replaced by the FSR 4 upscaler itself (no loader was available).</summary>
    ReplaceRuntimeWithUpscaler,

    /// <summary>The FSR 4 upscaler, added beside a loader that took an SDK 1 runtime's place.</summary>
    AddUpscaler,

    /// <summary>The SDK 2 frame generation module, added beside that loader.</summary>
    AddFrameGeneration,
}

/// <summary>One file the FSR 4 route will write.</summary>
/// <param name="Directory">Where it goes.</param>
/// <param name="FileName">The name it is written under, which is the name the game loads.</param>
/// <param name="SourcePath">The file copied there; empty while the build is not downloaded yet.</param>
/// <param name="SourceText">What it is, for the preview.</param>
/// <param name="ReplacesText">What it replaces, or null for a new file.</param>
public sealed record Fsr4Step(
    Fsr4StepKind Kind, string Directory, string FileName, string SourcePath, string SourceText, string? ReplacesText);

/// <summary>Where the files for the FSR 4 route come from.</summary>
/// <param name="UpscalerPath">The FSR 4 upscaler; empty while it is not downloaded yet.</param>
/// <param name="Label">The build as the user picked it.</param>
/// <param name="UpscalerVersion">Its file version, when it is on disk to read.</param>
/// <param name="Loader">An SDK 2 loader, for a game that ships the SDK 1 runtime; null when none was found.</param>
/// <param name="FrameGeneration">An SDK 2 frame generation module to go beside that loader.</param>
public sealed record Fsr4Sources(
    string UpscalerPath, string Label, string? UpscalerVersion, FidelityFxFile? Loader, FidelityFxFile? FrameGeneration);

/// <summary>What the FSR 4 route would do to one game, file by file.</summary>
/// <param name="Steps">Every file written, modules before the loader that finds them.</param>
/// <param name="Summary">What the route does, one sentence for each kind of file the game has.</param>
/// <param name="Warnings">What the player loses, said before they confirm.</param>
/// <param name="LosesFrameGeneration">True when the game's own frame generation is left with no module to run on.</param>
/// <param name="Refusal">Why nothing can be done, or null.</param>
public sealed record Fsr4Plan(
    IReadOnlyList<Fsr4Step> Steps, IReadOnlyList<string> Summary, IReadOnlyList<string> Warnings,
    bool LosesFrameGeneration, string? Refusal)
{
    public static Fsr4Plan Refused(string reason) =>
        new(Array.Empty<Fsr4Step>(), Array.Empty<string>(), Array.Empty<string>(), false, reason);

    /// <summary>One line of the preview for a step, with the path relative to the game.</summary>
    public static string Describe(Fsr4Step step, string gameRoot)
    {
        string relative;
        try { relative = Path.GetRelativePath(gameRoot, Path.Combine(step.Directory, step.FileName)); }
        catch { relative = step.FileName; }

        return $"{relative.Replace('\\', '/')}  ←  {step.SourceText}  "
               + (step.ReplacesText is { } replaced ? $"(replaces {replaced})" : "(new file)");
    }
}

/// <summary>
/// Works out which files put FSR 4 into a game that uses AMD's FidelityFX runtime, with
/// nothing hooked and no OptiScaler.
///
/// why: the two generations need different things. A game on SDK 2 has its own upscaler
/// module, and a new build goes over it under the game's own name. A game on SDK 1 has
/// one file holding every effect; AMD documents the SDK 2 loader as compatible with that
/// file's name, so the loader goes in under it with the modules beside it. Without a
/// loader the upscaler can stand in for the old file — it exports the same API — but it
/// carries no frame generation. See docs/fidelityfx.md.
/// </summary>
public static class Fsr4SwapPlanner
{
    private const string NothingToReplace =
        "This game ships no FidelityFX upscaler, so there is nothing to replace.";

    /// <summary>The files in a game that this route could replace: its upscaler modules and its SDK 1 runtimes.</summary>
    public static IReadOnlyList<FidelityFxFile> Targets(IEnumerable<FidelityFxFile> gameFiles) =>
        gameFiles.Where(f => f.Role is FidelityFxRole.Upscaler or FidelityFxRole.Monolith).ToList();

    /// <summary>The steps for a game, given what it ships and where the FSR 4 files come from.</summary>
    public static Fsr4Plan Plan(string gameRoot, IEnumerable<FidelityFxFile> gameFiles, Fsr4Sources sources)
    {
        var files = gameFiles.ToList();
        if (Targets(files).Count == 0) return Fsr4Plan.Refused(NothingToReplace);

        var steps = new List<Fsr4Step>();
        var summary = new List<string>();
        var losesFrameGeneration = false;
        var upscalerText = Text("FSR 4 upscaler", sources.UpscalerVersion);

        foreach (var directory in files
                     .GroupBy(f => f.Directory, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var upscalers = directory.Where(f => f.Role == FidelityFxRole.Upscaler).ToList();
            var runtimes = directory.Where(f => f.Role == FidelityFxRole.Monolith).ToList();

            if (upscalers.Count > 0)
            {
                foreach (var file in upscalers)
                    steps.Add(new Fsr4Step(Fsr4StepKind.ReplaceUpscaler, file.Directory, file.FileName,
                        sources.UpscalerPath, upscalerText, file.VersionText));
                Once(summary, "Replaces the game's FSR upscaler with the FSR 4 build, under the same name.");
                continue;
            }

            if (runtimes.Count == 0) continue;
            var home = runtimes[0].Directory;

            if (sources.Loader is { } loader)
            {
                // Modules first: a game interrupted halfway then still has a runtime it can load.
                steps.Add(new Fsr4Step(Fsr4StepKind.AddUpscaler, home,
                    FidelityFxLayout.CanonicalName(FidelityFxRole.Upscaler)!,
                    sources.UpscalerPath, upscalerText, null));

                if (sources.FrameGeneration is { } frames)
                {
                    var existing = directory.FirstOrDefault(f => f.Role == FidelityFxRole.FrameGeneration);
                    steps.Add(new Fsr4Step(Fsr4StepKind.AddFrameGeneration, home,
                        existing?.FileName ?? FidelityFxLayout.CanonicalName(FidelityFxRole.FrameGeneration)!,
                        frames.FullPath, Text("SDK 2 frame generation", frames.Version), existing?.VersionText));
                }
                else
                {
                    losesFrameGeneration = true;
                }

                foreach (var file in runtimes)
                    steps.Add(new Fsr4Step(Fsr4StepKind.ReplaceRuntimeWithLoader, file.Directory, file.FileName,
                        loader.FullPath, Text("SDK 2 loader", loader.Version), file.VersionText));

                Once(summary, "This game ships AMD's older all-in-one FidelityFX file. The SDK 2 loader "
                              + "takes that file's name, with the FSR 4 upscaler beside it.");
            }
            else
            {
                foreach (var file in runtimes)
                    steps.Add(new Fsr4Step(Fsr4StepKind.ReplaceRuntimeWithUpscaler, file.Directory, file.FileName,
                        sources.UpscalerPath, upscalerText, file.VersionText));
                losesFrameGeneration = true;

                Once(summary, "This game ships AMD's older all-in-one FidelityFX file. The FSR 4 upscaler "
                              + "takes its place under the same name.");
            }
        }

        if (steps.Count == 0) return Fsr4Plan.Refused(NothingToReplace);

        var warnings = new List<string>();
        if (losesFrameGeneration)
            warnings.Add(sources.Loader is null
                ? "No SDK 2 loader was found, so the upscaler stands in for the old file on its own. FSR 4 "
                  + "works, but the game's own frame generation will not: the upscaler carries none. Undo puts "
                  + "the original file back."
                : "No SDK 2 frame generation module was found to go with the loader, so the game's own frame "
                  + "generation will not work. Undo puts the original file back.");

        return new Fsr4Plan(steps, summary, warnings, losesFrameGeneration, null);
    }

    private static void Once(List<string> lines, string line)
    {
        if (!lines.Contains(line)) lines.Add(line);
    }

    private static string Text(string what, string? version) =>
        version is { Length: > 0 } ? $"{what} {Short(version)}" : what;

    /// <summary>Drops the SDK build number every module of one release shares.</summary>
    private static string Short(string version)
    {
        var parts = version.Split('.');
        return parts.Length == 4 ? string.Join('.', parts[0], parts[1], parts[2]) : version;
    }
}
