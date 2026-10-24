// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.

using UpscalerManager.Core.Services;

namespace UpscalerManager.Core.Components;

/// <summary>
/// The FSR 4 models, as OptiScaler 10 names them. FSR 4.1.2 (AMD driver 26.10.1) carries
/// all four in one file and picks the one for the GPU; OptiScaler 10 can pick another.
/// </summary>
public enum Fsr4ModelChoice
{
    /// <summary>The one the GPU is meant to run, chosen by the driver and OptiScaler.</summary>
    Auto,
    /// <summary>The full model, for RX 9000.</summary>
    Fp8,
    /// <summary>The INT8 model made for RX 7000.</summary>
    Int8,
    /// <summary>The INT8 model AMD optimised for RX 6000.</summary>
    Int8Rdna2,
    /// <summary>The small INT8 model for integrated graphics; it uses only standard shader features, so any GPU can run it.</summary>
    Int8Lite,
}

/// <summary>Which key a release takes to run FSR 4 on a GPU it does not list.</summary>
public enum Fsr4ForceKey
{
    /// <summary><c>[FSR] Fsr4ForceEnableInt8=true</c>: OptiScaler 0.9.4 and older.</summary>
    LegacyEnableInt8,
    /// <summary><c>[FSR] Fsr4ForceModel</c>, 0 to 2: OptiScaler 0.9.5.</summary>
    ForceModel,
    /// <summary><c>[FSR] Fsr4GpuSupport</c>, 0 to 4: OptiScaler 10, which knows the RX 6000 and Lite models.</summary>
    GpuSupport,
}

/// <summary>What a release's OptiScaler.ini says it understands about FSR 4.</summary>
/// <param name="HasModelKey">Whether it takes <c>[FSR] Fsr4Model</c>, the model picker of OptiScaler 10.</param>
/// <param name="ForceKey">The key it takes to run FSR 4 on a GPU it does not list.</param>
public sealed record Fsr4IniSchema(bool HasModelKey, Fsr4ForceKey ForceKey)
{
    /// <summary>
    /// Read from the release's own ini rather than its version number: the force key was
    /// renamed twice, and a key a release does not know is ignored without a word.
    /// </summary>
    public static Fsr4IniSchema Of(string releaseDir) => new(
        GameInstallationService.IniDocumentsKey(releaseDir, "FSR", "Fsr4Model"),
        GameInstallationService.IniDocumentsKey(releaseDir, "FSR", "Fsr4GpuSupport") ? Fsr4ForceKey.GpuSupport
        : GameInstallationService.IniDocumentsKey(releaseDir, "FSR", "Fsr4ForceModel") ? Fsr4ForceKey.ForceModel
        : Fsr4ForceKey.LegacyEnableInt8);
}

public static class Fsr4Models
{
    public static readonly IReadOnlyList<Fsr4ModelChoice> All = Enum.GetValues<Fsr4ModelChoice>();

    public static string Label(Fsr4ModelChoice model) => model switch
    {
        Fsr4ModelChoice.Fp8 => "FP8 — RX 9000",
        Fsr4ModelChoice.Int8 => "INT8 — RX 7000",
        Fsr4ModelChoice.Int8Rdna2 => "INT8 RDNA2 — RX 6000",
        Fsr4ModelChoice.Int8Lite => "INT8 Lite — integrated graphics, runs on any GPU",
        _ => "The one for this GPU (recommended)",
    };

    /// <summary>
    /// The model a GPU generation is meant to run, as OptiScaler 10 reads AMD's tables, or
    /// null when FSR 4 does not cover it.
    /// </summary>
    public static Fsr4ModelChoice? OfficialFor(RadeonGeneration generation) => generation switch
    {
        RadeonGeneration.Rdna4 => Fsr4ModelChoice.Fp8,
        RadeonGeneration.Rdna3 => Fsr4ModelChoice.Int8,
        RadeonGeneration.Rdna2 => Fsr4ModelChoice.Int8Rdna2,
        // Strix Halo runs INT8; every other RDNA 3 and 3.5 chip, and the Steam Deck, Lite.
        RadeonGeneration.Rdna3Integrated or RadeonGeneration.SteamDeck => Fsr4ModelChoice.Int8Lite,
        _ => null,
    };

    /// <summary>What "the one for this GPU" comes out as, for the note under the picker.</summary>
    public static string DescribeFor(RadeonGeneration generation) => OfficialFor(generation) switch
    {
        Fsr4ModelChoice.Fp8 => $"On {RadeonGenerations.Describe(generation)} that is FP8, the full model.",
        Fsr4ModelChoice.Int8 => $"On {RadeonGenerations.Describe(generation)} that is the INT8 model.",
        Fsr4ModelChoice.Int8Rdna2 =>
            $"On {RadeonGenerations.Describe(generation)} that is AMD's RX 6000 model, in AMD driver 26.10.1 and newer.",
        Fsr4ModelChoice.Int8Lite =>
            $"On {RadeonGenerations.Describe(generation)} that is INT8 Lite (INT8 on Strix Halo), in AMD driver 26.10.1 and newer.",
        _ => $"FSR 4 does not cover {RadeonGenerations.Describe(generation)}; INT8 Lite can still be forced, since it runs on any GPU.",
    };

    /// <summary>The <c>[FSR] Fsr4Model</c> value: 0 FP8, 1 INT8, 2 INT8 RDNA2, 3 INT8 Lite.</summary>
    public static string ModelValue(Fsr4ModelChoice model) => model switch
    {
        Fsr4ModelChoice.Fp8 => "0",
        Fsr4ModelChoice.Int8 => "1",
        Fsr4ModelChoice.Int8Rdna2 => "2",
        Fsr4ModelChoice.Int8Lite => "3",
        _ => "auto",
    };

    /// <summary>
    /// The FSR 4 keys an install writes, for the preview and the install alike.
    /// </summary>
    /// <remarks>
    /// Written on every install, "auto" included, where the release takes the key: the ini
    /// is kept across a reinstall, so leaving a key alone would keep a pick the player has
    /// since undone. The legacy force key is the exception, as before: it is only written to
    /// turn it on.
    /// </remarks>
    public static IReadOnlyList<IniKeyChange> IniKeys(
        Fsr4IniSchema schema, Fsr4ModelChoice model, bool force, RadeonGeneration generation)
    {
        var keys = new List<IniKeyChange>();
        if (schema.HasModelKey)
            keys.Add(new IniKeyChange("FSR", "Fsr4Model", ModelValue(model)));

        switch (schema.ForceKey)
        {
            case Fsr4ForceKey.GpuSupport:
                keys.Add(new IniKeyChange("FSR", "Fsr4GpuSupport", force ? GpuSupportValue(model, generation) : "auto"));
                break;
            case Fsr4ForceKey.ForceModel:
                // This release knows FP8 and INT8 only.
                keys.Add(new IniKeyChange("FSR", "Fsr4ForceModel",
                    !force ? "auto" : model == Fsr4ModelChoice.Fp8 ? "1" : "2"));
                break;
            case Fsr4ForceKey.LegacyEnableInt8 when force:
                keys.Add(new IniKeyChange("FSR", "Fsr4ForceEnableInt8", "true"));
                break;
        }
        return keys;
    }

    /// <summary>
    /// <c>[FSR] Fsr4GpuSupport</c>: 1 FP8, 2 INT8, 3 INT8 RDNA2, 4 INT8 Lite. With no model
    /// picked it is the GPU's own model, or Lite where FSR 4 has none, as Lite runs anywhere.
    /// </summary>
    private static string GpuSupportValue(Fsr4ModelChoice model, RadeonGeneration generation) =>
        (model == Fsr4ModelChoice.Auto ? OfficialFor(generation) ?? Fsr4ModelChoice.Int8Lite : model) switch
        {
            Fsr4ModelChoice.Fp8 => "1",
            Fsr4ModelChoice.Int8 => "2",
            Fsr4ModelChoice.Int8Rdna2 => "3",
            _ => "4",
        };
}

/// <summary>The FSR 4 choices of one install.</summary>
/// <param name="Model">The model OptiScaler 10 is asked to run.</param>
/// <param name="Force">Run FSR 4 even on a GPU OptiScaler does not list.</param>
/// <param name="DriverFiles">AMD driver files to place next to OptiScaler.</param>
public sealed record Fsr4Options(Fsr4ModelChoice Model, bool Force, IReadOnlyList<AmdDriverFile> DriverFiles)
{
    public static Fsr4Options None { get; } = new(Fsr4ModelChoice.Auto, false, Array.Empty<AmdDriverFile>());
}
