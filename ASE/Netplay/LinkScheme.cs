/*
 *
 * Invitation links that open ASE (NETPLAY.md §8.4): the "ase:" link type, added to the system
 * only when the user says so in the Online play window and removed there the same way, and the
 * hand-over of a link to an ASE that is already running.
 *
 * Every system keeps its link types its own way; all three are per user and need no
 * administrator rights:
 *   Windows  HKCU\Software\Classes\ase, whose command starts this ASE with the link.
 *   macOS    A link type can only be declared by an application's Info.plist, and declaring it
 *            in ASE.app would register it on installation, without asking. So ASE makes a small
 *            AppleScript applet for it on request, "ASE Links.app" in ~/Library/Application
 *            Support/ASE, which starts ASE with the link; removing it removes the type. It must
 *            not live in a temporary folder: LaunchServices registers an applet there but never
 *            hands it a link (tried on macOS 26).
 *   Linux    A hidden .desktop file for x-scheme-handler/ase, made the default with xdg-mime.
 *
 * A link clicked while ASE is running starts a second ASE, which passes the link to the first
 * through a named pipe (current user only) and ends before opening anything (Program.Main).
 *
 * Official repository 👉 https://github.com/thebitculture/ase
 *
 */

using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace ASE.Netplay
{
    public static class LinkScheme
    {
        public const string Scheme = "ase";
        const string PipeName = "ase-invitation-links";
        const int MaxLink = 512;

        public static bool Supported => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

        /// <summary>The invitation link among ASE's arguments: a bare ase: link, which is what the
        /// system passes, or --join=&lt;link&gt; (an https link too).</summary>
        public static string LinkIn(IEnumerable<string> args)
        {
            foreach (string arg in args)
            {
                if (arg.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase))
                    return arg;
                if (arg.StartsWith("--join=", StringComparison.OrdinalIgnoreCase))
                    return arg["--join=".Length..];
            }
            return null;
        }

        // ==================== Adding and removing the link type ====================

        /// <summary>Whether ase: links open this ASE (its type is there; the command in it may
        /// point at another copy of ASE, which <see cref="Register"/> puts right).</summary>
        public static bool IsRegistered
        {
            get
            {
                try
                {
                    if (OperatingSystem.IsWindows())
                        return Registry.CurrentUser.OpenSubKey(WindowsKey + @"\shell\open\command")?.GetValue("") is string;
                    if (OperatingSystem.IsMacOS())
                        return Directory.Exists(MacApplet);
                    if (OperatingSystem.IsLinux())
                        return File.Exists(LinuxDesktopFile);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                }
                return false;
            }
        }

        /// <summary>The command a link starts, as text: when it changes (ASE moved, or another copy
        /// runs) the type is registered again so it points here.</summary>
        public static string LauncherSignature
        {
            get
            {
                var (file, prefix) = Launcher();
                return string.Join(" ", [file, .. prefix]);
            }
        }

        /// <summary>Adds the ase: link type for this ASE. False with the reason.</summary>
        public static bool Register(out string error)
        {
            error = null;
            try
            {
                if (OperatingSystem.IsWindows())
                    RegisterWindows();
                else if (OperatingSystem.IsMacOS())
                    RegisterMac();
                else if (OperatingSystem.IsLinux())
                    RegisterLinux();
                else
                    error = "This system has no way for ASE to open links.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                error = ex.Message;
                Unregister(out _);      // nothing half made stays behind
            }

            return error == null;
        }

        /// <summary>Removes the ase: link type. False with the reason.</summary>
        public static bool Unregister(out string error)
        {
            error = null;
            try
            {
                if (OperatingSystem.IsWindows())
                    Registry.CurrentUser.DeleteSubKeyTree(WindowsKey, throwOnMissingSubKey: false);
                else if (OperatingSystem.IsMacOS())
                    UnregisterMac();
                else if (OperatingSystem.IsLinux())
                    UnregisterLinux();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                error = ex.Message;
            }

            return error == null;
        }

        // How this ASE is started: its executable, and its assembly first when the executable
        // is dotnet itself (a development run)
        static (string File, string[] Prefix) Launcher()
        {
            string exe = Environment.ProcessPath ?? "";
            string entry = Assembly.GetEntryAssembly()?.Location;

            if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(entry))
                return (exe, [entry]);

            return (exe, []);
        }

        // ---- Windows ----

        const string WindowsKey = @"Software\Classes\" + Scheme;

        [SupportedOSPlatform("windows")]
        static void RegisterWindows()
        {
            var (file, prefix) = Launcher();
            string command = string.Join(" ", [Quote(file), .. prefix.Select(Quote), "\"%1\""]);

            using var key = Registry.CurrentUser.CreateSubKey(WindowsKey);
            key.SetValue("", "URL:ASE invitation link");
            key.SetValue("URL Protocol", "");

            using (var icon = key.CreateSubKey("DefaultIcon"))
                icon.SetValue("", $"{Quote(file)},0");

            using (var open = key.CreateSubKey(@"shell\open\command"))
                open.SetValue("", command);

            static string Quote(string s) => $"\"{s}\"";
        }

        // ---- macOS ----

        static string MacApplet => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Application Support", "ASE", "ASE Links.app");

        const string LsRegister = "/System/Library/Frameworks/CoreServices.framework/Versions/A/Frameworks/LaunchServices.framework/Versions/A/Support/lsregister";

        static void RegisterMac()
        {
            UnregisterMac();
            Directory.CreateDirectory(Path.GetDirectoryName(MacApplet));

            // Inside an app bundle: open starts a new ASE, which passes the link on to one that
            // is running. Outside one (a development run): the executable itself, in the background.
            var (file, prefix) = Launcher();
            int bundle = file.IndexOf(".app/Contents/MacOS/", StringComparison.Ordinal);
            string start = bundle > 0
                ? $"\"open -n -a \" & quoted form of \"{Escape(file[..(bundle + 4)])}\" & \" --args \" & quoted form of theLink"
                : string.Join(" & \" \" & ", [.. new[] { file }.Concat(prefix).Select(p => $"quoted form of \"{Escape(p)}\""), "quoted form of theLink"]) + " & \" > /dev/null 2>&1 &\"";

            string script = Path.Combine(Path.GetTempPath(), $"ase-links-{Environment.ProcessId}.applescript");
            File.WriteAllText(script, "on open location theLink\n\tdo shell script " + start + "\nend open location\n");

            try
            {
                Run("/usr/bin/osacompile", "-o", MacApplet, script);
            }
            finally
            {
                File.Delete(script);
            }

            string plist = Path.Combine(MacApplet, "Contents", "Info.plist");
            Run("/usr/libexec/PlistBuddy",
                "-c", "Add :CFBundleIdentifier string com.thebitculture.ase.links",
                "-c", "Add :LSUIElement bool true",
                "-c", "Add :CFBundleURLTypes array",
                "-c", "Add :CFBundleURLTypes:0 dict",
                "-c", "Add :CFBundleURLTypes:0:CFBundleURLName string com.thebitculture.ase.invitation",
                "-c", "Add :CFBundleURLTypes:0:CFBundleURLSchemes array",
                "-c", $"Add :CFBundleURLTypes:0:CFBundleURLSchemes:0 string {Scheme}",
                plist);

            // Editing Info.plist breaks the applet's signature: sign it again, ad hoc
            Run("/usr/bin/codesign", "--force", "--deep", "-s", "-", MacApplet);
            Run(LsRegister, "-f", MacApplet);

            // An AppleScript string literal
            static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        static void UnregisterMac()
        {
            if (!Directory.Exists(MacApplet))
                return;

            try { Run(LsRegister, "-u", MacApplet); } catch (InvalidOperationException) { }
            Directory.Delete(MacApplet, recursive: true);
        }

        // ---- Linux ----

        static string LinuxApplications => Path.Combine(
            Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } data ? data : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"),
            "applications");

        const string LinuxDesktopName = "ase-invitation-links.desktop";
        static string LinuxDesktopFile => Path.Combine(LinuxApplications, LinuxDesktopName);

        static void RegisterLinux()
        {
            var (file, prefix) = Launcher();
            string exec = string.Join(" ", [.. new[] { file }.Concat(prefix).Select(DesktopQuote), "%u"]);

            Directory.CreateDirectory(LinuxApplications);
            File.WriteAllText(LinuxDesktopFile,
                "[Desktop Entry]\n" +
                "Type=Application\n" +
                "Name=ASE invitation links\n" +
                "Comment=Opens Atari System Emulator's online play invitations\n" +
                $"Exec={exec}\n" +
                "NoDisplay=true\n" +
                "Terminal=false\n" +
                $"MimeType=x-scheme-handler/{Scheme};\n");

            Run("xdg-mime", "default", LinuxDesktopName, $"x-scheme-handler/{Scheme}");
            TryRun("update-desktop-database", LinuxApplications);

            // A desktop entry's Exec quoting: double quotes, with ", `, $ and \ escaped
            static string DesktopQuote(string s) => "\"" + s.Replace("\\", "\\\\\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$") + "\"";
        }

        static void UnregisterLinux()
        {
            if (File.Exists(LinuxDesktopFile))
                File.Delete(LinuxDesktopFile);

            // xdg-mime cannot unset a default: take the line out of mimeapps.list by hand
            string config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } c ? c : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            string list = Path.Combine(config, "mimeapps.list");
            if (File.Exists(list))
            {
                var lines = File.ReadAllLines(list);
                var kept = lines.Where(l => !l.StartsWith($"x-scheme-handler/{Scheme}=", StringComparison.Ordinal)).ToArray();
                if (kept.Length != lines.Length)
                    File.WriteAllLines(list, kept);
            }

            TryRun("update-desktop-database", LinuxApplications);
        }

        // ---- Running the system's tools ----

        static void Run(string file, params string[] args)
        {
            var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (string a in args)
                start.ArgumentList.Add(a);

            using var process = Process.Start(start) ?? throw new InvalidOperationException($"{Path.GetFileName(file)} could not be started.");
            var error = process.StandardError.ReadToEndAsync();
            process.StandardOutput.ReadToEnd();

            if (!process.WaitForExit(30_000))
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                throw new InvalidOperationException($"{Path.GetFileName(file)} did not finish.");
            }

            if (process.ExitCode != 0)
                throw new InvalidOperationException($"{Path.GetFileName(file)} failed: {error.Result.Trim()}");
        }

        static void TryRun(string file, params string[] args)
        {
            try { Run(file, args); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }

        // ==================== One ASE takes the links ====================

        /// <summary>Hands <paramref name="link"/> to an ASE already running: true when one took
        /// it, and this one can end.</summary>
        public static bool TryForward(string link)
        {
            if (string.IsNullOrEmpty(link) || link.Length > MaxLink)
                return false;

            try
            {
                using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
                pipe.Connect(400);
                byte[] bytes = Encoding.UTF8.GetBytes(link + "\n");
                pipe.Write(bytes);
                pipe.Flush();
                return true;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        static bool _listening;

        // Whether an ASE answers on the pipe (a connection with nothing in it, which it ignores)
        static bool SomebodyListens()
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
                pipe.Connect(200);
                return true;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>Takes the links other ASE processes hand over, for as long as this one runs.
        /// <paramref name="onLink"/> is called on a pool thread.</summary>
        public static void Listen(Action<string> onLink)
        {
            if (_listening)
                return;
            _listening = true;

            _ = Task.Run(async () =>
            {
                bool cleared = false;
                while (true)
                {
                    NamedPipeServerStream server;
                    try
                    {
                        server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    }
                    catch (IOException) when (!cleared && !OperatingSystem.IsWindows() && !SomebodyListens())
                    {
                        // A socket file left by an ASE that died, which nobody answers on
                        cleared = true;
                        try { File.Delete(Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + PipeName)); } catch (IOException) { }
                        continue;
                    }
                    catch (IOException)
                    {
                        return;     // another ASE takes the links
                    }

                    using (server)
                    {
                        try
                        {
                            await server.WaitForConnectionAsync().ConfigureAwait(false);
                            var buffer = new byte[MaxLink + 2];
                            int total = 0, n;
                            while (total < buffer.Length && (n = await server.ReadAsync(buffer.AsMemory(total)).ConfigureAwait(false)) > 0)
                                total += n;

                            string link = Encoding.UTF8.GetString(buffer, 0, total).Trim();
                            if (link.Length is > 0 and <= MaxLink)
                                onLink(link);
                        }
                        catch (IOException)
                        {
                            // A client that went away half-way: wait for the next one
                        }
                    }
                }
            });
        }
    }
}
