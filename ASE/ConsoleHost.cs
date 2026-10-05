/*
 * 
 * Wires the emulator's log to the console it was launched from.
 * 
 * ASE is a GUI (WinExe) executable, so Windows never creates a console window for it: running
 * it from Explorer, a shortcut or the Start menu shows the emulator window and nothing else.
 * Launched from cmd, PowerShell or a terminal it still logs to that console, which is what this
 * file arranges. When there is no console to attach to and the log is wanted anyway, --console
 * opens one.
 * 
 * Official repository 👉 https://github.com/thebitculture/ase
 * 
 */

using System.Runtime.InteropServices;
using System.Text;

namespace ASE
{
    /// <summary>
    /// Console plumbing for a GUI-subsystem executable on Windows.
    /// </summary>
    /// <remarks>Everything here is a no-op outside Windows: only the Windows PE header tells a
    /// console program from a windowed one, and on Linux/macOS the process always keeps the
    /// terminal that started it (or none, with the standard streams already pointing nowhere).
    /// </remarks>
    internal static class ConsoleHost
    {
        // The parent process, for AttachConsole: whoever launched us (cmd, PowerShell, a terminal).
        const uint AttachParentProcess = 0xFFFFFFFF;

        const int StdOutputHandle = -11;
        const int StdErrorHandle = -12;

        const uint GenericRead = 0x80000000;
        const uint GenericWrite = 0x40000000;
        const uint FileShareRead = 0x00000001;
        const uint FileShareWrite = 0x00000002;
        const uint OpenExisting = 3;

        // System menu entry of the console window, removed on a console of our own (see
        // PrepareOwnConsole).
        const uint ScClose = 0xF060;
        const uint MfByCommand = 0x00000000;

        static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AttachConsole(uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AllocConsole();

        [DllImport("kernel32.dll")]
        static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);

        [DllImport("user32.dll")]
        static extern bool DeleteMenu(IntPtr hMenu, uint uPosition, uint uFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetStdHandle(int nStdHandle, IntPtr hHandle);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
        static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
                                        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
                                        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        /// <summary>
        /// True when the process has somewhere to write its log — a console, or the file/pipe the
        /// standard output was redirected to. False only on Windows, when the emulator was started
        /// from Explorer or a shortcut and <c>--console</c> was not given: <see cref="ColoredConsole"/>
        /// then skips the formatting work rather than composing lines nobody will ever read.
        /// </summary>
        public static bool HasConsole { get; private set; } = true;

        /// <summary>
        /// Attaches to the launching console and points the standard streams at it. Must be the
        /// very first thing <c>Main</c> does: it rebinds <see cref="Console"/>, so anything that
        /// wrote (or merely read <c>Console.IsOutputRedirected</c>) before it would have cached the
        /// streams the process started with.
        /// </summary>
        /// <param name="args">The command line, read here for <c>--console</c> alone. The real
        /// parser (<see cref="Config.LoadConfig"/>) runs later and already logs while it works, so
        /// that one option cannot wait for it.</param>
        public static void Initialize(string[] args)
        {
            if (!OperatingSystem.IsWindows())
                return;

            try
            {
                // A non-zero console window means the process already owns one and there is
                // nothing to attach to: `dotnet ASE.dll` (the dotnet host is a console program),
                // or a debugger that made one for us.
                bool attached = Console.IsOutputRedirected || GetConsoleWindow() != IntPtr.Zero || AttachConsole(AttachParentProcess);
                bool ownConsole = false;

                // Started from Explorer, a shortcut or the Start menu: there is no console and no
                // parent that has one, so --console asks for one of our own. A process can only
                // have a single console, which is why this is not tried when one was found above.
                if (!attached && WantsOwnConsole(args) && AllocConsole())
                    attached = ownConsole = true;

                HasConsole = attached;

                // No console and none asked for. .NET already hands Console.Out a writer over
                // Stream.Null, so writing stays harmless — but the output encoding below must not
                // be touched, since setting the console code page needs a console.
                if (!attached)
                    return;

                BindStandardStreams();

                if (ownConsole)
                    PrepareOwnConsole();
            }
            catch
            {
                // The log is never worth failing over: an emulator that starts silently beats one
                // that does not start.
                HasConsole = false;
            }
        }

        /// <summary>
        /// Reads <c>--console</c> out of the raw command line, bare or with a boolean value.
        /// </summary>
        static bool WantsOwnConsole(string[] args)
        {
            if (args == null)
                return false;

            foreach (string arg in args)
            {
                string[] parts = arg.Split('=');

                if (!parts[0].Equals("--console", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Bare --console turns it on; an explicit value wins, and anything unparseable is
                // taken as "on" — the user did type the option.
                return parts.Length < 2 || !bool.TryParse(parts[1], out bool on) || on;
            }

            return false;
        }

        /// <summary>
        /// Dresses the console window ASE made for itself.
        /// </summary>
        static void PrepareOwnConsole()
        {
            try
            {
                Console.Title = "Atari System Emulator — log";
            }
            catch
            {
                // Cosmetic.
            }

            // Closing a console window terminates every process attached to it, and this one is
            // ASE's: its X button would take the emulator down where it stands, possibly in the
            // middle of a write to a hard disk image. Dropping the entry from the system menu
            // disables the button and Alt+F4 with it — the window still goes away with the
            // emulator, which is the only way it should.
            IntPtr window = GetConsoleWindow();

            if (window == IntPtr.Zero)
                return;

            IntPtr menu = GetSystemMenu(window, false);

            if (menu != IntPtr.Zero)
                DeleteMenu(menu, ScClose, MfByCommand);
        }

        /// <summary>
        /// Makes <see cref="Console"/> write to the console we just attached to.
        /// </summary>
        /// <remarks>A GUI process inherits the parent's standard handles when the parent has any
        /// (cmd and PowerShell pass theirs down, and so does a redirection to a file or a pipe) and
        /// gets none at all otherwise — AttachConsole hands the process a console but leaves those
        /// handles as it found them. So a handle that is already valid is left alone, or
        /// <c>ASE.exe --help &gt; log.txt</c> would lose its redirection, and only a missing one is
        /// opened against the console device.</remarks>
        static void BindStandardStreams()
        {
            // CONOUT$ is opened for reading as well: Console.ForegroundColor reads the screen
            // buffer info back through the same handle, and a write-only one makes every colour
            // change fail silently.
            ReopenIfMissing(StdOutputHandle, GenericRead | GenericWrite);
            ReopenIfMissing(StdErrorHandle, GenericRead | GenericWrite);

            // The log carries emoji and box characters, which need the console code page at UTF-8.
            // Guarded because the call fails when the standard output is a pipe with no console
            // behind it at all.
            try
            {
                Console.OutputEncoding = Encoding.UTF8;
            }
            catch
            {
                // Not fatal: the text still comes out, some characters as '?'.
            }

            // Console caches its writer over the handles it saw at first use; these are built over
            // the ones set above. No BOM (UTF8Encoding(false)): a StreamWriter would emit it on the
            // first write, which is noise on a console and dirt at the head of a redirected log.
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
        }

        /// <summary>
        /// Points one standard handle at the console device, but only if the process was given no
        /// handle for it.
        /// </summary>
        static void ReopenIfMissing(int stdHandle, uint access)
        {
            IntPtr current = GetStdHandle(stdHandle);

            // Zero means "the process was started without one"; INVALID_HANDLE_VALUE is the error
            // return. Anything else is the parent's own handle and has to be kept.
            if (current != IntPtr.Zero && current != InvalidHandleValue)
                return;

            IntPtr handle = CreateFile("CONOUT$", access, FileShareRead | FileShareWrite,
                                       IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);

            if (handle != InvalidHandleValue)
                SetStdHandle(stdHandle, handle);
        }
    }
}
