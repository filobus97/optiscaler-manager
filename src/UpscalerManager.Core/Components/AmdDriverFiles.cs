// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.

namespace UpscalerManager.Core.Components;

/// <summary>Which graphics API an AMD driver file serves.</summary>
public enum AmdDriverApi
{
    /// <summary><c>amdxcffx64.dll</c>: FSR 4 for DirectX 12 games.</summary>
    Dx12,

    /// <summary><c>amdvkffx64.dll</c>: FSR 4 for Vulkan games.</summary>
    Vulkan,
}

/// <summary>
/// The two files AMD's driver carries FSR 4 in. Since driver 26.10.1 (FSR 4.1.2) they hold
/// every model: FP8 for RX 9000, INT8 for RX 7000, an INT8 model for RX 6000, and a small
/// INT8 Lite model for integrated graphics. The driver picks the one for the GPU.
/// </summary>
/// <remarks>
/// AMD distributes them only inside its driver package, so this app never downloads them:
/// they are imported from a driver the player already has. On Windows an installed driver
/// provides them and OptiScaler finds them in the driver store; on Linux there is no AMD
/// driver store, so a copy has to sit next to OptiScaler. OptiScaler loads a copy there in
/// preference to the driver's, on Windows too.
/// </remarks>
public static class AmdDriverFiles
{
    public const string Dx12Name = "amdxcffx64.dll";
    public const string VulkanName = "amdvkffx64.dll";

    /// <summary>The function a driver file hands FSR 4 out through, instead of the FidelityFX API.</summary>
    public const string ProviderExport = "UpdateFfxApiProvider";

    public static readonly IReadOnlyList<string> Names = new[] { Dx12Name, VulkanName };

    /// <summary>The API a file serves, by its name, or null when it is not one of the two.</summary>
    public static AmdDriverApi? ApiOf(string? fileName) =>
        string.Equals(fileName, Dx12Name, StringComparison.OrdinalIgnoreCase) ? AmdDriverApi.Dx12
        : string.Equals(fileName, VulkanName, StringComparison.OrdinalIgnoreCase) ? AmdDriverApi.Vulkan
        : null;

    public static string NameFor(AmdDriverApi api) => api == AmdDriverApi.Dx12 ? Dx12Name : VulkanName;

    public static string Describe(AmdDriverApi api) => api == AmdDriverApi.Dx12 ? "DirectX 12" : "Vulkan";
}
