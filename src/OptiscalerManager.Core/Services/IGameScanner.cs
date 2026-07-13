using OptiscalerManager.Core.Models;

namespace OptiscalerManager.Core.Services;

/// <summary>
/// Common interface for all platform game scanners.
/// Implement this to add support for new platforms (e.g. Linux launchers).
/// </summary>
public interface IGameScanner
{
    List<Game> Scan();
}
