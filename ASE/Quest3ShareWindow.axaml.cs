using System;
using System.Diagnostics;
using System.Threading.Tasks;
using ASE.Services;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace ASE;

public partial class Quest3ShareWindow : Window
{
    private readonly Quest3StreamService _streamService = Quest3StreamService.Instance;

    public Quest3ShareWindow()
    {
        InitializeComponent();

        _streamService.StatusChanged += OnServiceStatusChanged;
        _streamService.ClientCountChanged += OnServiceClientCountChanged;

        Closed += (s, e) =>
        {
            _streamService.StatusChanged -= OnServiceStatusChanged;
            _streamService.ClientCountChanged -= OnServiceClientCountChanged;
        };

        UpdateUiState();

        // If not running, start stream server automatically when dialog opens
        if (!_streamService.IsRunning)
        {
            if (_streamService.Start(out string error))
            {
                UpdateUiState();
            }
            else if (!string.IsNullOrEmpty(error))
            {
                TextStatus.Text = $"Error starting server: {error}";
                StatusLed.Fill = new SolidColorBrush(Color.Parse("#FF4444"));
            }
        }
    }

    private void UpdateUiState()
    {
        bool running = _streamService.IsRunning;
        string url = _streamService.GetStreamUrl();

        TextStreamUrl.Text = url;
        TextPort.Text = _streamService.Port.ToString();

        if (running)
        {
            StatusLed.Fill = new SolidColorBrush(Color.Parse("#00FF66"));
            TextStatus.Text = "Server Running (Broadcasting)";
            BtnToggleServer.Content = "Stop Server";
            BtnToggleServer.Classes.Set("accent", false);
        }
        else
        {
            StatusLed.Fill = new SolidColorBrush(Color.Parse("#888888"));
            TextStatus.Text = "Server Stopped";
            BtnToggleServer.Content = "Start Server";
            BtnToggleServer.Classes.Set("accent", true);
        }

        UpdateClientsCount(_streamService.ConnectedClients);

        // Sync dropdown selections
        int fpsIndex = _streamService.TargetFps switch
        {
            25 => 0,
            30 => 1,
            50 => 2,
            60 => 3,
            _ => 2
        };
        ComboFps.SelectedIndex = fpsIndex;

        int qualityIndex = _streamService.Quality switch
        {
            <= 65 => 0,
            <= 80 => 1,
            <= 90 => 2,
            _ => 3
        };
        ComboQuality.SelectedIndex = qualityIndex;
    }

    private void OnServiceStatusChanged(bool running)
    {
        Dispatcher.UIThread.Post(UpdateUiState);
    }

    private void OnServiceClientCountChanged(int count)
    {
        Dispatcher.UIThread.Post(() => UpdateClientsCount(count));
    }

    private void UpdateClientsCount(int count)
    {
        TextClients.Text = count == 1 ? "1 connected client" : $"{count} connected clients";
    }

    private async void OnCopyUrlClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.Clipboard != null)
            {
                string url = _streamService.GetStreamUrl();
                try
                {
                    var clipboard = topLevel.Clipboard;
                    foreach (var m in clipboard.GetType().GetMethods())
                    {
                        if (m.Name.Contains("Set", StringComparison.OrdinalIgnoreCase))
                        {
                            var parms = m.GetParameters();
                            if (parms.Length == 1 && parms[0].ParameterType == typeof(string))
                            {
                                var res = m.Invoke(clipboard, new object[] { url });
                                if (res is Task t) await t;
                                break;
                            }
                        }
                    }
                }
                catch { }
                TextCopyLabel.Text = "Copied!";
                await Task.Delay(1800);
                TextCopyLabel.Text = "Copy URL";
            }
        }
        catch { }
    }

    private async void OnOpenInBrowserClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel != null)
            {
                await topLevel.Launcher.LaunchUriAsync(new Uri(_streamService.GetStreamUrl()));
            }
            else
            {
                Process.Start(new ProcessStartInfo(_streamService.GetStreamUrl()) { UseShellExecute = true });
            }
        }
        catch { }
    }

    private void OnToggleServerClick(object sender, RoutedEventArgs e)
    {
        if (_streamService.IsRunning)
        {
            _streamService.Stop();
        }
        else
        {
            if (int.TryParse(TextPort.Text?.Trim(), out int port) && port > 0 && port < 65536)
            {
                _streamService.Port = port;
            }
            _streamService.Start(out _);
        }
        UpdateUiState();
    }

    private void OnFpsChanged(object sender, SelectionChangedEventArgs e)
    {
        _streamService.TargetFps = ComboFps.SelectedIndex switch
        {
            0 => 25,
            1 => 30,
            2 => 50,
            3 => 60,
            _ => 50
        };
    }

    private void OnQualityChanged(object sender, SelectionChangedEventArgs e)
    {
        _streamService.Quality = ComboQuality.SelectedIndex switch
        {
            0 => 65,
            1 => 80,
            2 => 90,
            3 => 95,
            _ => 80
        };
    }

    private void OnPortChanged(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(TextPort.Text?.Trim(), out int port) && port > 0 && port < 65536)
        {
            if (_streamService.Port != port)
            {
                _streamService.Port = port;
                if (_streamService.IsRunning)
                {
                    _streamService.Stop();
                    _streamService.Start(out _);
                }
                UpdateUiState();
            }
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
