// Upscaler Manager - UI harness
// Licensed under GPL-3.0-or-later (see repository LICENSE).

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UpscalerManager.App;
using UpscalerManager.App.Infrastructure;
using UpscalerManager.App.Services;
using UpscalerManager.App.Views;
using UpscalerManager.Core.Services;
using UpscalerManager.UiHarness;

// Refuses to run anywhere but the throwaway HOME run.sh makes: it writes a sample Steam
// library and app settings, and must never touch a real one.
var home = Environment.GetEnvironmentVariable("HOME") ?? "";
if (Environment.GetEnvironmentVariable("UM_HARNESS") != "1" || !home.Contains("um-harness"))
{
    Console.Error.WriteLine("Run this through run.sh: it needs a throwaway HOME.");
    return 2;
}

var shots = args.Length > 0 ? args[0] : Path.Combine(home, "shots");
Directory.CreateDirectory(shots);
SampleLibrary.Build(home);

AppBuilder.Configure<App>()
    .UseSkia()
    .WithInterFont()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .SetupWithoutStarting();

var harness = new Harness(shots);
var done = new CancellationTokenSource();
Dispatcher.UIThread.Post(async () =>
{
    try { await harness.RunAsync(); }
    catch (Exception ex) { harness.Fail("the run itself", ex.ToString()); }
    finally { done.Cancel(); }
});
Dispatcher.UIThread.MainLoop(done.Token);

Console.WriteLine(harness.Failures == 0 ? $"ALL PASS ({harness.Passes})" : $"{harness.Failures} FAILED, {harness.Passes} passed");
return harness.Failures == 0 ? 0 : 1;

namespace UpscalerManager.UiHarness
{
    /// <summary>The walk through the screens, with a check at every step that matters.</summary>
    internal sealed partial class Harness
    {
        private readonly string _shots;
        private MainWindow _w = null!;

        public int Passes { get; private set; }
        public int Failures { get; private set; }

        public Harness(string shots) => _shots = shots;

        public async Task RunAsync()
        {
            var manager = new ManagerService(new AvaloniaManualComponentProvider(() => _w), new FakeGpu());
            _w = new MainWindow(manager) { Width = 880, Height = 640 };
            _w.Show();

            await Until(() => Cards().Count > 0, "the library scan");
            // Covers are decoration and load after the list; give them a moment so the
            // screenshots look like the app, and carry on without them offline.
            await Until(() => Cards().Count(c => c.GetVisualDescendants().OfType<Avalonia.Controls.Image>()
                .Any(i => i.IsEffectivelyVisible)) >= 4, "cover art", timeoutMs: 20000, optional: true);

            // A second pass on an RX 9000 checks only what differs there.
            if (Environment.GetEnvironmentVariable("UM_HARNESS_GPU") == "rdna4")
            {
                await Rdna4Async();
                return;
            }

            await LibraryAsync();
            await InstallPageAsync();
            await SettingsAsync();
            await FasterFsr4Async();
        }

        // ── Checks ───────────────────────────────────────────────────────────────

        public void Check(string what, string actual, string expected)
        {
            if (actual == expected) { Passes++; Console.WriteLine($"  PASS  {what,-58} -> {actual}"); }
            else Fail(what, $"expected '{expected}', got '{actual}'");
        }

        public void Check(string what, bool ok, string detail = "")
        {
            if (ok) { Passes++; Console.WriteLine($"  PASS  {what}"); }
            else Fail(what, detail);
        }

        public void Fail(string what, string detail)
        {
            Failures++;
            Console.WriteLine($"  FAIL  {what}: {detail}");
        }

        // ── Driving the window ───────────────────────────────────────────────────

        private IReadOnlyList<Button> Cards() =>
            _w.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("GameCard")).ToList();

        private Button Card(string gameName) =>
            Cards().First(c => c.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == gameName));

        private T Named<T>(string name) where T : Control =>
            _w.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

        private T? MaybeNamed<T>(string name) where T : Control =>
            _w.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);

        private object? Page => Named<ContentControl>("PageContent").Content;

        private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        private async Task Settle(int ms = 300)
        {
            _w.UpdateLayout();
            await Task.Delay(ms);
            _w.UpdateLayout();
        }

        private async Task Until(Func<bool> condition, string what, int timeoutMs = 30000, bool optional = false)
        {
            var waited = 0;
            while (!condition())
            {
                if (waited >= timeoutMs)
                {
                    if (optional) Console.WriteLine($"  note  {what} did not arrive in time; carrying on");
                    else Fail($"waiting for {what}", "timed out");
                    return;
                }
                await Task.Delay(100);
                waited += 100;
                _w.UpdateLayout();
            }
        }

        private async Task BackAsync()
        {
            Click(Named<Button>("BackButton"));
            await Settle();
        }

        private void Shoot(string file)
        {
            _w.UpdateLayout();
            var frame = _w.CaptureRenderedFrame();
            if (frame is null) { Fail($"screenshot {file}", "nothing rendered"); return; }
            frame.Save(Path.Combine(_shots, file), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            Console.WriteLine($"  shot  {file}");
        }

        private static bool Shown(Control c) => c.IsEffectivelyVisible;
    }

    /// <summary>The GPU the harness says the machine has: an RX 7800 XT unless UM_HARNESS_GPU says otherwise.</summary>
    internal sealed class FakeGpu : IGpuDetectionService
    {
        private readonly GpuInfo _gpu = (Environment.GetEnvironmentVariable("UM_HARNESS_GPU") ?? "rdna3") switch
        {
            "rdna4" => new GpuInfo { Vendor = GpuVendor.AMD, Name = "AMD Radeon RX 9070 XT", DeviceId = 0x7550, VideoMemoryBytes = 16L << 30 },
            "rdna2" => new GpuInfo { Vendor = GpuVendor.AMD, Name = "AMD Radeon RX 6800 XT", DeviceId = 0x73BF, VideoMemoryBytes = 16L << 30 },
            "deck" => new GpuInfo { Vendor = GpuVendor.AMD, Name = "AMD Custom GPU 0405", DeviceId = 0x163F, VideoMemoryBytes = 1L << 30 },
            "nvidia" => new GpuInfo { Vendor = GpuVendor.NVIDIA, Name = "NVIDIA GeForce RTX 4070", VideoMemoryBytes = 12L << 30 },
            _ => new GpuInfo { Vendor = GpuVendor.AMD, Name = "AMD Radeon RX 7800 XT", DeviceId = 0x747E, VideoMemoryBytes = 16L << 30 },
        };

        public GpuInfo[] DetectGPUs() => new[] { _gpu };
        public GpuInfo? GetPrimaryGPU() => _gpu;
        public GpuInfo? GetDiscreteGPU() => _gpu;
        public bool HasGPU(GpuVendor vendor) => _gpu.Vendor == vendor;
        public string GetGPUDescription() => _gpu.Name;
    }
}
