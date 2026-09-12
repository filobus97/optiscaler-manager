// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace UpscalerManager.Core.Services;

/// <summary>
/// Streams one large file to disk, for the download routes that fetch whole DLLs.
///
/// Shared by the vendor route and the DLSS Swapper archive route because the two
/// mistakes worth avoiding are the same in both:
///
/// <list type="bullet">
/// <item><see cref="System.Net.Http.HttpClient.Timeout"/> is a deadline on the entire
/// operation, so the app's usual 30-second client cannot fetch a 60 MB file at all.
/// The large-file client has no such deadline, which means something else has to end
/// a dead transfer — hence the idle timeout below, restarted every time bytes
/// arrive.</item>
/// <item>A transfer cut off part way leaves a file that still begins with a valid PE
/// header, so every check downstream passes and a truncated DLL gets installed into a
/// game. Comparing the bytes written against <c>Content-Length</c> is what catches
/// it.</item>
/// </list>
/// </summary>
internal static class LargeFileDownload
{
    /// <summary>
    /// How long the transfer may go without a single byte arriving before it is given
    /// up on. An idle timeout, not a total one: these files are tens of megabytes, and
    /// a deadline long enough for a slow connection would leave a genuinely dead one
    /// hanging just as long.
    /// </summary>
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Fetches <paramref name="url"/> to <paramref name="destination"/>, reporting
    /// progress as a fraction when the server declares a length.
    /// </summary>
    public static async Task ToFileAsync(
        string url, string destination, IProgress<double>? progress, CancellationToken cancel)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        idle.CancelAfter(StallTimeout);

        using var response = await NetworkService.GetLargeFileClient()
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, idle.Token);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(idle.Token);
        await using var file = File.Create(destination);

        var buffer = new byte[81920];
        long written = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, idle.Token)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), idle.Token);
            written += read;
            // Progress means the connection is alive, so the clock starts again.
            idle.CancelAfter(StallTimeout);
            if (total is > 0) progress?.Report((double)written / total.Value);
        }

        if (total is > 0 && written != total.Value)
            throw new IOException(
                $"The download stopped after {written:N0} of {total.Value:N0} bytes. Nothing was added.");
    }
}
