// Upscaler Manager - tests
// Licensed under GPL-3.0-or-later (see repository LICENSE).

using System;
using System.IO;
using System.Linq;
using Xunit;

namespace UpscalerManager.Core.Tests
{
    /// <summary>
    /// The app's mark reaches the user through four separate paths: the window icon and
    /// the header image (Avalonia resources), the Windows executable's embedded icon
    /// (the .ico), and the Linux icon theme (the PNGs copied to hicolor). They all come
    /// from assets/icons, but nothing checked they agreed with each other — so a change
    /// that regenerated the PNGs and left the .ico behind would ship an app whose
    /// taskbar icon disagreed with its own window.
    /// </summary>
    public class IconAssetTests
    {
        /// <summary>The sizes shipped, matching SIZES in tools/make-icon.py.</summary>
        private static readonly int[] Sizes = { 16, 24, 32, 48, 64, 128, 256 };

        private static string IconsDir
        {
            get
            {
                var dir = AppContext.BaseDirectory;
                while (dir is not null && !Directory.Exists(Path.Combine(dir, ".git")))
                    dir = Path.GetDirectoryName(dir);
                Assert.NotNull(dir);
                return Path.Combine(dir!, "assets", "icons");
            }
        }

        [Fact]
        public void EverySizeTheAppInstallsExistsAsAPng()
        {
            // DesktopEntryService copies exactly these into the Linux icon theme, and
            // skips any that are missing — silently, so a dropped size would just never
            // appear at that resolution.
            foreach (var size in Sizes)
                Assert.True(File.Exists(Path.Combine(IconsDir, $"icon-{size}.png")),
                    $"icon-{size}.png is missing, so that size would be absent from the icon theme.");
        }

        [Fact]
        public void TheWindowsIconHoldsExactlyTheSamePngsAsTheIconTheme()
        {
            // The .ico is what Explorer and the Windows taskbar show before the window
            // exists; the PNGs are what every other surface shows. If these drift, the
            // app has two different icons depending on where you look at it.
            var ico = File.ReadAllBytes(Path.Combine(IconsDir, "icon.ico"));

            Assert.Equal(0, BitConverter.ToUInt16(ico, 0));        // reserved
            Assert.Equal(1, BitConverter.ToUInt16(ico, 2));        // type: icon
            var count = BitConverter.ToUInt16(ico, 4);
            Assert.Equal(Sizes.Length, count);

            var seen = new System.Collections.Generic.List<int>();
            for (var i = 0; i < count; i++)
            {
                var entry = 6 + 16 * i;
                // A 256px entry is stored as 0, which is the format's way of saying
                // "too big for a byte".
                var width = ico[entry] == 0 ? 256 : ico[entry];
                var length = BitConverter.ToInt32(ico, entry + 8);
                var offset = BitConverter.ToInt32(ico, entry + 12);

                var embedded = ico.AsSpan(offset, length).ToArray();
                var onDisk = File.ReadAllBytes(Path.Combine(IconsDir, $"icon-{width}.png"));

                Assert.True(embedded.SequenceEqual(onDisk),
                    $"The .ico's {width}px image is not the same file as icon-{width}.png. " +
                    "Re-run tools/make-icon.py, which writes both.");
                seen.Add(width);
            }

            Assert.Equal(Sizes.OrderBy(s => s), seen.OrderBy(s => s));
        }

        [Fact]
        public void EveryIconIsARealPngOfTheSizeItsNameClaims()
        {
            // Reads the IHDR rather than trusting the filename: a resized icon saved
            // under the old name looks right in a directory listing and wrong in a
            // taskbar.
            foreach (var size in Sizes)
            {
                var bytes = File.ReadAllBytes(Path.Combine(IconsDir, $"icon-{size}.png"));
                Assert.True(bytes.Length > 8, $"icon-{size}.png is empty.");
                Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, bytes.Take(4));

                // IHDR width and height are big-endian at offsets 16 and 20.
                var width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
                var height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
                Assert.Equal(size, width);
                Assert.Equal(size, height);
            }
        }
    }
}
