// OptiScaler Manager - tests
// Licensed under GPL-3.0-or-later (see repository LICENSE).

using Xunit;

namespace OptiscalerManager.Core.Tests
{
    /// <summary>
    /// Tests that redirect the app-data directory by setting XDG_CONFIG_HOME.
    ///
    /// That variable is process-global, and xUnit runs test classes in parallel by
    /// default, so two such classes will overwrite each other's value and fail
    /// intermittently — which is exactly what happened when a second one was added.
    /// Sharing a collection makes them run one after another instead.
    /// </summary>
    [CollectionDefinition(Name)]
    public class AppDataCollection
    {
        public const string Name = "app-data environment";
    }
}
