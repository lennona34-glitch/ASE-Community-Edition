using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using TinyDialogsNet;

namespace ASE;

/// <summary>
/// Creates an ACSI hard disk image: a raw file of whole 512-byte sectors, laid out ready to
/// use — one partition, a FAT16 file system, and the ASEHD boot driver (see AtariHdDriver)
/// installed in the sectors reserved for it. The disk boots on its own, with nothing to run
/// on the ST first.
///
/// Third-party drivers (ICD Pro, PPutnik, HDDriver…) re-partition and re-format to their own
/// taste, so an image can still be handed to one of those; it simply overwrites this.
///
/// Shown as a modal dialog from the Configuration window's Hard disk tab; closes returning the
/// full path of the created image, or <c>null</c> when nothing was created.
/// </summary>
public partial class CreateHardDiskWindow : Window
{
    // A class-0 ACSI read/write carries a 21-bit LBA (see Acsi.GetLba), which tops out at
    // 2^21 sectors = 1 GB. The ASEHD driver switches to class-1 commands above that, but the
    // cap stays: a bigger disk needs a cluster size that wastes more than it gains.
    const long MaxSizeMB = 1024;
    const long MinSizeMB = 1;

    const long DefaultSizeMB = 32;

    const string DefaultFileName = "harddisk.img";

    static readonly FileFilter ImageFilter =
        new("Hard disk image", ["*.img", "*.hd", "*.acsi", "*.raw", "*.ahd"]);

    // Avalonia's runtime XAML loader (and the designer) look for a parameterless constructor.
    public CreateHardDiskWindow() : this("") { }

    /// <param name="suggestedPath">
    /// Image path the Hard disk tab currently holds, used only to open the file dialog on a
    /// sensible folder. Empty falls back to the configured disk-images directory.
    /// </param>
    public CreateHardDiskWindow(string suggestedPath)
    {
        InitializeComponent();

        string folder = "";

        if (!string.IsNullOrWhiteSpace(suggestedPath))
        {
            try
            {
                folder = Path.GetDirectoryName(suggestedPath) ?? "";
            }
            catch
            {
                // A malformed path in the text box is not worth reporting here
            }
        }

        if (string.IsNullOrWhiteSpace(folder))
            folder = Config.ConfigOptions.RunninConfig.DiskImagesPath ?? "";

        TextImagePath.Text = Directory.Exists(folder)
            ? Path.Combine(folder, DefaultFileName)
            : DefaultFileName;

        TextSize.Text = DefaultSizeMB.ToString(CultureInfo.InvariantCulture);
        TextSize.TextChanged += (_, _) => RefreshGeometry();

        RefreshGeometry();
    }

    // The emulator parks while the window is open. The Configuration window that opens this one
    // already holds a pause; the count makes the nesting harmless.
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        ASEMain.EnterUiPause();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        ASEMain.ExitUiPause();
    }

    /// <summary>Echoes the size the way the emulated controller sees it, and flags a bad value.</summary>
    void RefreshGeometry()
    {
        if (!TryParseSize(out long mb))
        {
            TextGeometry.Text = $"Enter a whole number of megabytes between {MinSizeMB} and {MaxSizeMB}.";
            return;
        }

        long sectors = mb * 1024 * 1024 / Acsi.SectorSize;
        TextGeometry.Text = $"{mb} MB - {sectors:N0} sectors of {Acsi.SectorSize} bytes.";
    }

    bool TryParseSize(out long mb)
    {
        bool ok = long.TryParse((TextSize.Text ?? "").Trim(), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out mb);

        return ok && mb >= MinSizeMB && mb <= MaxSizeMB;
    }

    void OnPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string tag)
            TextSize.Text = tag;
    }

    async void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var (canceled, path) = await Dialogs.SaveFile("New hard disk image",
            TextImagePath.Text ?? DefaultFileName, ImageFilter);

        if (canceled || string.IsNullOrWhiteSpace(path))
            return;

        TextImagePath.Text = path;
    }

    void OnCancelClick(object sender, RoutedEventArgs e) => Close(null);

    async void OnCreateClick(object sender, RoutedEventArgs e)
    {
        if (!TryParseSize(out long mb))
        {
            await Error($"The size must be a whole number of megabytes between {MinSizeMB} and {MaxSizeMB}.");
            return;
        }

        string path = (TextImagePath.Text ?? "").Trim();

        if (string.IsNullOrEmpty(path))
        {
            await Error("Choose where the image file should be created.");
            return;
        }

        try
        {
            path = Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            await Error($"'{path}' is not a valid file name: {ex.Message}");
            return;
        }

        // Typing a bare name is natural; give it the extension the file dialogs filter on.
        if (string.IsNullOrEmpty(Path.GetExtension(path)))
            path += ".img";

        string dir = Path.GetDirectoryName(path) ?? "";

        if (!Directory.Exists(dir))
        {
            await Error($"The folder '{dir}' does not exist.");
            return;
        }

        // The running machine holds the attached image open, and truncating a disk under a
        // mounted file system corrupts it anyway. The OS error for this is cryptic, so it is
        // caught here instead.
        if (Acsi.EmulationOn && SamePath(path, Acsi.AttachedImagePath))
        {
            await Error("That image is attached to the running machine. Reset with the ACSI disk switched off, or pick another file.");
            return;
        }

        if (File.Exists(path))
        {
            var answer = await Dialogs.MessageBox("Create hard disk image",
                $"'{Path.GetFileName(path)}' already exists.\n\nReplace it with a new empty image? Everything stored in it will be lost.",
                MessageBoxDialogType.YesNo, MessageBoxIconType.Warning, MessageBoxButton.No);

            if (answer != MessageBoxButton.Yes)
                return;
        }

        long bytes = mb * 1024 * 1024;

        try
        {
            // SetLength on a fresh file is instant and reads back as zeros on every platform —
            // no point writing a gigabyte of them, and the blocks are claimed as the ST uses them.
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            fs.SetLength(bytes);
            WriteAtariLayout(fs, bytes / Acsi.SectorSize);
        }
        catch (Exception ex)
        {
            await Error($"Could not create '{path}': {ex.Message}");
            return;
        }

        ColoredConsole.WriteLine($"ACSI: created empty hard disk image [[green]]{path}[[/green]] " +
            $"({mb} MB, {bytes / Acsi.SectorSize:N0} sectors).",
            Config.ConfigOptions.DebugModes.Quiet);

        Close(path);
    }

    // The layout itself (partition table, FAT, root directory) is AtariHdImage's, shared with
    // the library's hard-disk games; this only adds the driver, last, so the disk boots on its
    // own with nothing to run on the ST. A build carrying no driver still produces a perfectly
    // good — just unbootable — disk.
    static void WriteAtariLayout(FileStream fs, long totalSectors)
    {
        var layout = AtariHdImage.Format(fs, totalSectors);
        if (layout != null)
            AtariHdDriver.Install(fs, layout.RootSector);
    }

    static bool SamePath(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return false;

        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    static Task<MessageBoxButton> Error(string message)
        => Dialogs.MessageBox("Create hard disk image", message,
            MessageBoxDialogType.Ok, MessageBoxIconType.Error, MessageBoxButton.Ok);
}
