using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ASE;

/// <summary>
/// The time warp (<see cref="TimeWarp"/>): the moments of the last minute as a strip of
/// pictures, the chosen one large, and Continue from here to put the machine back there. Picked
/// with the mouse, the slider, the arrow keys or the gamepad, which ASEMain.HandleEvents hands
/// over while the window is open (<see cref="OnPadButton"/>, <see cref="Move"/>). Holds a UI
/// pause like the emulator's dialogs, so the moments do not move while the window is open;
/// closing it without continuing leaves the game where it was.
/// </summary>
public partial class TimeWarpWindow : Window
{
    /// <summary>The window, while it is open: one at a time, and where the gamepad goes.</summary>
    public static TimeWarpWindow Current { get; private set; }

    // SDL drops gamepad events while none of its windows has the focus, and this one is not
    // SDL's: it is told otherwise while the window is open, and put back as it was after
    string _backgroundEvents;

    IReadOnlyList<TimeWarp.Moment> _moments;
    readonly List<Border> _items = new();
    readonly List<WriteableBitmap> _bitmaps = new();
    readonly long _now;
    int _chosen = -1;

    // While the slider or the switch are set from code, their handlers must not act
    bool _syncing;

    public TimeWarpWindow()
    {
        InitializeComponent();
        Current = this;

        _moments = TimeWarp.Moments;
        _now = TimeWarp.Frame;

        _syncing = true;
        SwitchTimeWarp.IsChecked = TimeWarp.Enabled;
        _syncing = false;

        foreach (var moment in _moments)
            PanelStrip.Children.Add(Item(moment));

        SliderMoment.Maximum = Math.Max(0, _moments.Count - 1);

        // The moment one press of Ctrl+Shift+← would go back to: a sensible first choice
        if (_moments.Count > 0)
        {
            long target = _now - (long)Math.Round(TimeWarp.StepSeconds * TimeWarp.FramesPerSecond);
            int index = _moments.Count - 1;
            while (index > 0 && _moments[index].Frame > target)
                index--;
            Choose(index);
        }

        RefreshState();
        KeyDown += OnKeyDown;
    }

    // What the window can do right now, in its subtitle, and whether there is anything to pick.
    // Switched off is the user's own choice and goes in the subtitle; anything else that keeps
    // it from going back (a hard disk, an online game…) is a warning, on its own, in red.
    void RefreshState()
    {
        string why = TimeWarp.Unavailable;
        bool warning = TimeWarp.Enabled && why != null;
        SliderMoment.IsEnabled = ButtonContinue.IsEnabled = why == null && _moments.Count > 0;

        PanelWarning.IsVisible = warning;
        TextWarning.Text = warning ? why : "";

        TextSubtitle.Text = !TimeWarp.Enabled
            ? "The time warp is off. Switch it on below, and from then on a moment of the game is kept every second." +
              (Config.HostIsRaspberryPi && Config.ConfigOptions.RunninConfig.TimeWarpEnabled == null
                  ? " It starts off on a Raspberry Pi, where keeping them takes a real share of the processor and memory."
                  : "")
            : warning ? "Back to any second of the last minute of play, and the game carries on from there."
            : _moments.Count == 0 ? "Nothing is kept yet: a moment of the game is kept every second from now on. Close the window and play."
            : $"The last {AgeSeconds(_moments[0])} seconds of play, one moment a second. Pick one, and the game carries on from there.";
    }

    // The switch: kept in the configuration; off, every moment goes, and the strip with them
    void OnSwitchChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing)
            return;

        bool on = SwitchTimeWarp.IsChecked == true;
        TimeWarp.SetEnabled(on);

        if (!on)
        {
            ImagePreview.Source = null;
            TextChosen.Text = "";
            PanelStrip.Children.Clear();
            _items.Clear();
            foreach (var bitmap in _bitmaps)
                bitmap.Dispose();
            _bitmaps.Clear();
            _moments = [];
            _chosen = -1;
        }

        RefreshState();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        ASEMain.EnterUiPause();

        _backgroundEvents = SDL2.SDL.SDL_GetHint(SDL2.SDL.SDL_HINT_JOYSTICK_ALLOW_BACKGROUND_EVENTS);
        SDL2.SDL.SDL_SetHint(SDL2.SDL.SDL_HINT_JOYSTICK_ALLOW_BACKGROUND_EVENTS, "1");

        if (_chosen >= 0)
            _items[_chosen].BringIntoView();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        ASEMain.ExitUiPause();

        SDL2.SDL.SDL_SetHint(SDL2.SDL.SDL_HINT_JOYSTICK_ALLOW_BACKGROUND_EVENTS, _backgroundEvents ?? "0");
        Current = null;

        ImagePreview.Source = null;
        foreach (var bitmap in _bitmaps)
            bitmap.Dispose();
    }

    // ==================== The strip ====================

    Border Item(TimeWarp.Moment moment)
    {
        var bitmap = ToBitmap(moment);
        _bitmaps.Add(bitmap);

        int index = _items.Count;
        var content = new StackPanel { Spacing = 3 };
        content.Children.Add(new Image
        {
            Source = bitmap,
            Height = 72,
            Width = 72.0 * moment.ThumbWidth / moment.ThumbHeight,
            Stretch = Avalonia.Media.Stretch.Fill,
        });
        content.Children.Add(new TextBlock { Text = AgeText(moment) });

        var item = new Border { Classes = { "moment" }, Child = content };
        item.PointerPressed += (_, e) =>
        {
            Choose(index);
            if (e.ClickCount == 2)
                Continue();
        };

        _items.Add(item);
        return item;
    }

    void Choose(int index)
    {
        if (index < 0 || index >= _moments.Count)
            return;

        if (_chosen >= 0)
            _items[_chosen].Classes.Remove("chosen");

        _chosen = index;
        _items[index].Classes.Add("chosen");
        _items[index].BringIntoView();

        ImagePreview.Source = _bitmaps[index];
        TextChosen.Text = AgeText(_moments[index]);

        _syncing = true;
        SliderMoment.Value = index;
        _syncing = false;

        HideError();
    }

    void OnSliderChanged(object sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_syncing)
            Choose((int)Math.Round(e.NewValue));
    }

    /// <summary>A step through the strip: earlier (negative) or later, kept within it.</summary>
    public void Move(int step)
    {
        if (_moments.Count > 0)
            Choose(Math.Clamp(_chosen + step, 0, _moments.Count - 1));
    }

    /// <summary>
    /// A gamepad button pressed while the window is open: the D-pad steps through the moments,
    /// LB and RB five at a time, A continues from the chosen one and B goes back to the game —
    /// and so does the button that opened the window, pressed again. The left stick steps too
    /// (<see cref="Move"/>).
    /// </summary>
    public void OnPadButton(SDL2.SDL.SDL_GameControllerButton button)
    {
        switch (button)
        {
            case SDL2.SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_DPAD_LEFT:
                Move(-1);
                break;
            case SDL2.SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_DPAD_RIGHT:
                Move(1);
                break;
            case SDL2.SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_LEFTSHOULDER:
                Move(-5);
                break;
            case SDL2.SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_RIGHTSHOULDER:
                Move(5);
                break;
            case SDL2.SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_A:
                Continue();
                break;
            case SDL2.SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_B:
                Close();
                break;
            default:
                if (ASEMain.GamepadMapping(button) == Config.ConfigOptions.GamepadButtonsMapping.OpenTimeWarp)
                    Close();
                break;
        }
    }

    void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left:
                Choose(_chosen - 1);
                break;
            case Key.Right:
                Choose(_chosen + 1);
                break;
            case Key.Home:
                Choose(0);
                break;
            case Key.End:
                Choose(_moments.Count - 1);
                break;
            case Key.Enter:
                Continue();
                break;
            case Key.Escape:
                Close();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    // ==================== Going back ====================

    void OnContinueClick(object sender, RoutedEventArgs e) => Continue();

    void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    void Continue()
    {
        if (_chosen < 0)
            return;

        var moment = _moments[_chosen];
        if (!TimeWarp.GoTo(moment, out string error))
        {
            ShowError(error);
            return;
        }

        ASEMain.MainWindow?.SetStatusBarText($"Time warp: back {AgeSeconds(moment)} s");
        Close();
    }

    // ==================== Helpers ====================

    int AgeSeconds(TimeWarp.Moment moment) => (int)Math.Round((_now - moment.Frame) / TimeWarp.FramesPerSecond);

    string AgeText(TimeWarp.Moment moment)
    {
        int seconds = AgeSeconds(moment);
        return seconds == 0 ? "Just now" : $"{seconds} s ago";
    }

    static unsafe WriteableBitmap ToBitmap(TimeWarp.Moment moment)
    {
        int width = moment.ThumbWidth, height = moment.ThumbHeight;
        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormats.Rgba8888, AlphaFormat.Opaque);

        // The thumbnail is laid out like the framebuffer: RGBA bytes, row after row
        var source = MemoryMarshal.AsBytes(moment.Thumbnail.AsSpan());
        using (var fb = bitmap.Lock())
        {
            for (int y = 0; y < height; y++)
                source.Slice(y * width * 4, width * 4).CopyTo(new Span<byte>((byte*)fb.Address + y * fb.RowBytes, width * 4));
        }

        return bitmap;
    }

    void ShowError(string text)
    {
        TextError.Text = text ?? "";
        TextError.IsVisible = !string.IsNullOrEmpty(text);
    }

    void HideError() => ShowError(null);
}
