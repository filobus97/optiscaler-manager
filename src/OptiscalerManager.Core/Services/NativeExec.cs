// OptiScaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.Runtime.InteropServices;

namespace OptiscalerManager.Core.Services
{
    /// <summary>
    /// Thin wrapper over libc <c>execv(2)</c>. Replacing the process image in place —
    /// keeping the same PID — is what makes the in-app update survive Steam Gaming
    /// Mode / gamescope: Steam never sees the "game" exit, so nothing tears down the
    /// session and no unmanaged window needs to be resurfaced. Unix only (Windows
    /// locks the running .exe and isn't a Gaming-Mode target).
    /// </summary>
    internal static partial class NativeExec
    {
        [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int execv(string path, string?[] argv);

        /// <summary>
        /// Replaces the current process with <paramref name="path"/> and the given
        /// <paramref name="args"/>. Returns ONLY on failure (with errno in
        /// <see cref="Marshal.GetLastPInvokeError"/>); on success control never comes back.
        /// </summary>
        public static int Exec(string path, string[] args)
        {
            // argv is [program, args…, NULL]; argv[0] is conventionally the program path.
            var argv = new string?[args.Length + 2];
            argv[0] = path;
            Array.Copy(args, 0, argv, 1, args.Length);
            argv[^1] = null; // NULL terminator
            execv(path, argv);
            return Marshal.GetLastPInvokeError();
        }
    }
}
