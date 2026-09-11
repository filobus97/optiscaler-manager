// Upscaler Manager - tests
// Licensed under GPL-3.0-or-later (see repository LICENSE).

using UpscalerManager.Core.Models;
using System;
using System.IO;
using UpscalerManager.Core.Services;
using Xunit;

namespace UpscalerManager.Core.Tests
{
    /// <summary>
    /// The update repository is stored in the user's config on first run and never
    /// refreshed from the template. If a rename is not corrected here, existing installs
    /// keep polling a name we no longer publish to — and a failed check reports "no
    /// update", so nobody finds out.
    /// </summary>
    public class AppRepositoryTests
    {
        [Fact]
        public void AConfigAlreadyPointingAtTheCurrentRepoIsLeftAlone()
            => Assert.False(AppRepository.NeedsRetargeting(new RepositoryConfig
            {
                RepoOwner = AppRepository.Current.RepoOwner,
                RepoName = AppRepository.Current.RepoName,
            }));

        [Fact]
        public void ARepoTheProjectHasMovedAwayFromIsRetargeted()
            => Assert.True(AppRepository.NeedsRetargeting(
                new RepositoryConfig { RepoOwner = "filobus97", RepoName = "Optiscaler-Client" }));

        [Theory]
        [InlineData("", "")]
        [InlineData("filobus97", "")]
        [InlineData("", "optiscaler-manager")]
        [InlineData("   ", "   ")]
        public void AnIncompleteConfigIsRetargeted(string owner, string name)
            => Assert.True(AppRepository.NeedsRetargeting(
                new RepositoryConfig { RepoOwner = owner, RepoName = name }));

        [Fact]
        public void AMissingConfigIsRetargeted()
            => Assert.True(AppRepository.NeedsRetargeting(null));

        [Fact]
        public void ARepositoryTheUserChoseIsNotOverridden()
        {
            // Only names this project has actually published under are corrected;
            // anything else is treated as a deliberate choice, e.g. a private fork.
            Assert.False(AppRepository.NeedsRetargeting(
                new RepositoryConfig { RepoOwner = "someone", RepoName = "their-own-fork" }));
        }

        [Fact]
        public void TheSupportPageIsBuiltFromTheRepositoryRatherThanHardcoded()
        {
            // The whole point of the indirection: a repository rename moves the link
            // with it, and the donation channels behind the page change without a
            // release. If this ever becomes a literal platform URL, that is lost.
            Assert.Equal(
                $"https://github.com/{AppRepository.Current.RepoOwner}/{AppRepository.Current.RepoName}"
                + $"/blob/HEAD/{AppRepository.SupportPageFile}",
                AppRepository.SupportPageUrl);
        }

        [Fact]
        public void TheSupportPageUsesHeadSoADefaultBranchRenameDoesNotBreakIt()
        {
            Assert.Contains("/blob/HEAD/", AppRepository.SupportPageUrl);
            Assert.DoesNotContain("/blob/main/", AppRepository.SupportPageUrl);
        }

        [Fact]
        public void TheSupportPageFileActuallyExistsInTheRepository()
        {
            // A dead link in the only place the app asks for support would be worse
            // than no button at all, so the file the URL names is checked to exist.
            var repoRoot = FindRepositoryRoot();
            Assert.True(
                File.Exists(Path.Combine(repoRoot, AppRepository.SupportPageFile)),
                $"{AppRepository.SupportPageFile} is missing from the repository root, "
                + "so the app's Support button would open a 404.");
        }

        private static string FindRepositoryRoot()
        {
            var dir = AppContext.BaseDirectory;
            while (dir is not null && !Directory.Exists(Path.Combine(dir, ".git")))
                dir = Path.GetDirectoryName(dir);
            Assert.NotNull(dir);
            return dir!;
        }
    }
}
