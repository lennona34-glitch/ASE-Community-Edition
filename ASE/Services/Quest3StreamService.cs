using System;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;

namespace ASE.Services;

public class Quest3StreamService
{
    private static Quest3StreamService _instance;
    public static Quest3StreamService Instance => _instance ??= new Quest3StreamService();

    private TcpListener _tcpListener;
    private CancellationTokenSource _cts;
    private Task _listenerTask;
    private int _clientCount = 0;

    public bool IsRunning => _tcpListener != null;
    public int Port { get; set; } = 8088;
    public int Quality { get; set; } = 80;
    public int TargetFps { get; set; } = 50;
    public int ConnectedClients => _clientCount;

    public event Action<bool> StatusChanged;
    public event Action<int> ClientCountChanged;

    public string GetLocalIpAddress()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                var ipProps = ni.GetIPProperties();
                foreach (var addr in ipProps.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(addr.Address))
                    {
                        return addr.Address.ToString();
                    }
                }
            }
        }
        catch { }

        return "127.0.0.1";
    }

    public string GetStreamUrl() => $"http://{GetLocalIpAddress()}:{Port}";

    public bool Start(out string error)
    {
        error = null;
        if (IsRunning) return true;

        try
        {
            _cts = new CancellationTokenSource();
            _tcpListener = new TcpListener(IPAddress.Any, Port);
            _tcpListener.Start();

            _listenerTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
            StatusChanged?.Invoke(true);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Stop();
            return false;
        }
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
            _tcpListener?.Stop();
        }
        catch { }
        finally
        {
            _tcpListener = null;
            _cts = null;
            _clientCount = 0;
            StatusChanged?.Invoke(false);
            ClientCountChanged?.Invoke(0);
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _tcpListener != null)
        {
            try
            {
                var client = await _tcpListener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleClientAsync(client, ct), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        using (var stream = client.GetStream())
        using (var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true))
        {
            try
            {
                string requestLine = await reader.ReadLineAsync(ct);
                if (string.IsNullOrWhiteSpace(requestLine))
                    return;

                string[] parts = requestLine.Split(' ');
                if (parts.Length < 2)
                    return;

                string method = parts[0].ToUpperInvariant();
                string rawUrl = parts[1];

                // Drain remaining headers until empty line
                while (true)
                {
                    string headerLine = await reader.ReadLineAsync(ct);
                    if (string.IsNullOrEmpty(headerLine))
                        break;
                }

                int queryIdx = rawUrl.IndexOf('?');
                string path = queryIdx >= 0 ? rawUrl.Substring(0, queryIdx).ToLowerInvariant() : rawUrl.ToLowerInvariant();
                string queryString = queryIdx >= 0 ? rawUrl.Substring(queryIdx + 1) : "";

                if (method == "OPTIONS")
                {
                    byte[] cors = Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nAccess-Control-Allow-Origin: *\r\nAccess-Control-Allow-Methods: GET, POST, OPTIONS\r\nAccess-Control-Allow-Headers: Content-Type\r\nContent-Length: 0\r\n\r\n");
                    await stream.WriteAsync(cors, 0, cors.Length, ct);
                    return;
                }

                if (path == "/" || path == "/index.html")
                {
                    byte[] html = Encoding.UTF8.GetBytes(GetWebPlayerHtml());
                    string head = $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {html.Length}\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n";
                    byte[] headBytes = Encoding.ASCII.GetBytes(head);
                    await stream.WriteAsync(headBytes, 0, headBytes.Length, ct);
                    await stream.WriteAsync(html, 0, html.Length, ct);
                    await stream.FlushAsync(ct);
                    return;
                }

                if (path == "/stream.mjpg")
                {
                    await StreamMjpegToTcpAsync(stream, ct);
                    return;
                }

                if (path == "/frame.jpg")
                {
                    byte[] jpeg = ASEMain.CaptureFrameJpeg(Quality);
                    if (jpeg != null)
                    {
                        string head = $"HTTP/1.1 200 OK\r\nContent-Type: image/jpeg\r\nContent-Length: {jpeg.Length}\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n";
                        byte[] headBytes = Encoding.ASCII.GetBytes(head);
                        await stream.WriteAsync(headBytes, 0, headBytes.Length, ct);
                        await stream.WriteAsync(jpeg, 0, jpeg.Length, ct);
                        await stream.FlushAsync(ct);
                    }
                    else
                    {
                        byte[] notFound = Encoding.ASCII.GetBytes("HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\n\r\n");
                        await stream.WriteAsync(notFound, 0, notFound.Length, ct);
                    }
                    return;
                }

                if (path == "/api/status")
                {
                    string statusJson = $"{{\"running\":{IsRunning.ToString().ToLower()},\"clients\":{_clientCount},\"fps\":{TargetFps}}}";
                    byte[] jsonBytes = Encoding.UTF8.GetBytes(statusJson);
                    string head = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {jsonBytes.Length}\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n";
                    byte[] headBytes = Encoding.ASCII.GetBytes(head);
                    await stream.WriteAsync(headBytes, 0, headBytes.Length, ct);
                    await stream.WriteAsync(jsonBytes, 0, jsonBytes.Length, ct);
                    await stream.FlushAsync(ct);
                    return;
                }

                if (path == "/api/input")
                {
                    HandleInputQuery(queryString);
                    byte[] okBytes = Encoding.UTF8.GetBytes("{\"ok\":true}");
                    string head = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {okBytes.Length}\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n";
                    byte[] headBytes = Encoding.ASCII.GetBytes(head);
                    await stream.WriteAsync(headBytes, 0, headBytes.Length, ct);
                    await stream.WriteAsync(okBytes, 0, okBytes.Length, ct);
                    await stream.FlushAsync(ct);
                    return;
                }

                byte[] resp404 = Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n");
                await stream.WriteAsync(resp404, 0, resp404.Length, ct);
            }
            catch { }
        }
    }

    private void HandleInputQuery(string query)
    {
        try
        {
            if (string.IsNullOrEmpty(query)) return;
            var parts = query.Split('&');
            int source = HostInput.GamepadButtons;

            foreach (var part in parts)
            {
                var kv = part.Split('=');
                if (kv.Length == 2)
                {
                    string key = kv[0].ToLowerInvariant();
                    bool val = kv[1].Equals("true", StringComparison.OrdinalIgnoreCase);

                    if (key == "fire") HostInput.Joystick(source, ACIA.JOY_FIRE, val);
                    else if (key == "up") HostInput.Joystick(source, ACIA.JOY_UP, val);
                    else if (key == "down") HostInput.Joystick(source, ACIA.JOY_DOWN, val);
                    else if (key == "left") HostInput.Joystick(source, ACIA.JOY_LEFT, val);
                    else if (key == "right") HostInput.Joystick(source, ACIA.JOY_RIGHT, val);
                }
            }
        }
        catch { }
    }

    private async Task StreamMjpegToTcpAsync(NetworkStream stream, CancellationToken ct)
    {
        Interlocked.Increment(ref _clientCount);
        ClientCountChanged?.Invoke(_clientCount);

        try
        {
            string initHead = "HTTP/1.1 200 OK\r\n" +
                              "Content-Type: multipart/x-mixed-replace; boundary=--frame\r\n" +
                              "Cache-Control: no-cache, no-store, must-revalidate\r\n" +
                              "Access-Control-Allow-Origin: *\r\n" +
                              "Connection: close\r\n\r\n";
            byte[] initBytes = Encoding.ASCII.GetBytes(initHead);
            await stream.WriteAsync(initBytes, 0, initBytes.Length, ct);
            await stream.FlushAsync(ct);

            int delayMs = Math.Max(10, 1000 / Math.Max(10, TargetFps));

            while (!ct.IsCancellationRequested && stream.CanWrite)
            {
                byte[] jpeg = ASEMain.CaptureFrameJpeg(Quality);
                if (jpeg != null)
                {
                    string frameHead = $"--frame\r\nContent-Type: image/jpeg\r\nContent-Length: {jpeg.Length}\r\n\r\n";
                    byte[] frameHeadBytes = Encoding.ASCII.GetBytes(frameHead);

                    await stream.WriteAsync(frameHeadBytes, 0, frameHeadBytes.Length, ct);
                    await stream.WriteAsync(jpeg, 0, jpeg.Length, ct);
                    byte[] crlf = Encoding.ASCII.GetBytes("\r\n");
                    await stream.WriteAsync(crlf, 0, crlf.Length, ct);
                    await stream.FlushAsync(ct);
                }

                await Task.Delay(delayMs, ct);
            }
        }
        catch { }
        finally
        {
            Interlocked.Decrement(ref _clientCount);
            ClientCountChanged?.Invoke(_clientCount);
        }
    }

    private string GetWebPlayerHtml()
    {
        return @"<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0, user-scalable=no"">
    <title>Atari ST - Meta Quest 3 VR &amp; Passthrough Cinema</title>
    <style>
        :root {
            --neon-blue: #00d2ff;
            --neon-glow: rgba(0, 210, 255, 0.4);
            --bg-dark: #0a0c10;
            --card-bg: rgba(18, 22, 32, 0.85);
        }
        * { box-sizing: border-box; margin: 0; padding: 0; user-select: none; }
        body {
            background-color: var(--bg-dark);
            color: #fff;
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
            display: flex;
            flex-direction: column;
            align-items: center;
            justify-content: center;
            min-height: 100vh;
            overflow: hidden;
            transition: background 0.3s ease;
        }
        body.passthrough {
            background: transparent !important;
            background-color: rgba(0,0,0,0) !important;
        }
        #theater-container {
            position: relative;
            display: flex;
            flex-direction: column;
            align-items: center;
            justify-content: center;
            width: 100vw;
            height: 100vh;
        }
        .screen-wrapper {
            position: relative;
            max-width: 90vw;
            max-height: 80vh;
            aspect-ratio: 4 / 3;
            border-radius: 14px;
            box-shadow: 0 0 50px var(--neon-glow), 0 20px 60px rgba(0,0,0,0.8);
            border: 2px solid rgba(0, 210, 255, 0.6);
            overflow: hidden;
            background: #000;
            transition: all 0.3s cubic-bezier(0.16, 1, 0.3, 1);
        }
        .screen-wrapper.imax {
            max-width: 98vw;
            max-height: 94vh;
            border-radius: 6px;
        }
        .screen-wrapper.curved {
            transform: perspective(1000px) rotateX(1deg);
            border-radius: 24px;
        }
        #stream-img {
            width: 100%;
            height: 100%;
            object-fit: contain;
            display: block;
            image-rendering: pixelated;
        }
        /* Scanlines overlay */
        .scanlines {
            position: absolute;
            inset: 0;
            pointer-events: none;
            background: linear-gradient(rgba(18, 16, 16, 0) 50%, rgba(0, 0, 0, 0.35) 50%);
            background-size: 100% 4px;
            opacity: 0.6;
            display: none;
        }
        body.scanlines-on .scanlines { display: block; }

        /* Floating HUD Controls */
        #toolbar {
            position: absolute;
            bottom: 24px;
            display: flex;
            gap: 12px;
            background: var(--card-bg);
            padding: 8px 16px;
            border-radius: 30px;
            backdrop-filter: blur(16px);
            border: 1px solid rgba(0, 210, 255, 0.3);
            box-shadow: 0 8px 32px rgba(0,0,0,0.6);
            z-index: 100;
            opacity: 0.85;
            transition: opacity 0.2s, transform 0.2s;
        }
        #toolbar:hover { opacity: 1; transform: translateY(-2px); }
        .hud-btn {
            background: transparent;
            border: 1px solid rgba(255,255,255,0.15);
            color: #fff;
            padding: 8px 14px;
            border-radius: 20px;
            font-size: 13px;
            font-weight: 500;
            cursor: pointer;
            display: flex;
            align-items: center;
            gap: 6px;
            transition: all 0.2s;
        }
        .hud-btn:hover, .hud-btn.active {
            background: var(--neon-blue);
            color: #000;
            border-color: var(--neon-blue);
            box-shadow: 0 0 16px var(--neon-glow);
        }
        .badge {
            background: rgba(0, 210, 255, 0.15);
            color: var(--neon-blue);
            border: 1px solid var(--neon-blue);
            padding: 3px 8px;
            border-radius: 12px;
            font-size: 11px;
            font-weight: 600;
        }
        #gamepad-status {
            font-size: 12px;
            color: #888;
            display: flex;
            align-items: center;
            gap: 6px;
        }
        #gamepad-status.active { color: #00ffaa; }
    </style>
</head>
<body>
    <div id=""theater-container"">
        <div class=""screen-wrapper"" id=""screen-frame"">
            <img id=""stream-img"" src=""/stream.mjpg"" alt=""Atari ST Display"">
            <div class=""scanlines""></div>
        </div>

        <div id=""toolbar"">
            <div class=""badge"">Quest 3 VR</div>
            <button class=""hud-btn"" id=""btn-fs"" onclick=""toggleFullScreen()"">⛶ Fullscreen VR</button>
            <button class=""hud-btn"" id=""btn-passthrough"" onclick=""togglePassthrough()"">👓 Passthrough</button>
            <button class=""hud-btn"" id=""btn-imax"" onclick=""toggleImax()"">📽 Cinema Scale</button>
            <button class=""hud-btn"" id=""btn-crt"" onclick=""toggleScanlines()"">📺 CRT Scanlines</button>
            <div id=""gamepad-status"">🎮 No Controller</div>
        </div>
    </div>

    <script>
        function toggleFullScreen() {
            if (!document.fullscreenElement) {
                document.documentElement.requestFullscreen().catch(() => {});
            } else {
                document.exitFullscreen();
            }
        }
        function togglePassthrough() {
            document.body.classList.toggle('passthrough');
            document.getElementById('btn-passthrough').classList.toggle('active');
        }
        function toggleImax() {
            document.getElementById('screen-frame').classList.toggle('imax');
            document.getElementById('btn-imax').classList.toggle('active');
        }
        function toggleScanlines() {
            document.body.classList.toggle('scanlines-on');
            document.getElementById('btn-crt').classList.toggle('active');
        }

        // Gamepad polling for Quest 3 Touch Controllers and Bluetooth Gamepads
        let lastInput = { up: false, down: false, left: false, right: false, fire: false };
        let inputActive = false;

        window.addEventListener('gamepadconnected', (e) => {
            document.getElementById('gamepad-status').textContent = '🎮 ' + e.gamepad.id.substring(0, 15) + '...';
            document.getElementById('gamepad-status').classList.add('active');
        });

        window.addEventListener('gamepaddisconnected', () => {
            document.getElementById('gamepad-status').textContent = '🎮 No Controller';
            document.getElementById('gamepad-status').classList.remove('active');
        });

        function pollGamepads() {
            const gps = navigator.getGamepads ? navigator.getGamepads() : [];
            let gp = null;
            for (let i = 0; i < gps.length; i++) {
                if (gps[i]) { gp = gps[i]; break; }
            }

            if (gp) {
                const threshold = 0.45;
                const axisX = gp.axes[0] || 0;
                const axisY = gp.axes[1] || 0;

                const left = axisX < -threshold || (gp.buttons[14] && gp.buttons[14].pressed);
                const right = axisX > threshold || (gp.buttons[15] && gp.buttons[15].pressed);
                const up = axisY < -threshold || (gp.buttons[12] && gp.buttons[12].pressed);
                const down = axisY > threshold || (gp.buttons[13] && gp.buttons[13].pressed);
                // A button (0), X button (2), or Right Trigger (7)
                const fire = (gp.buttons[0] && gp.buttons[0].pressed) ||
                             (gp.buttons[2] && gp.buttons[2].pressed) ||
                             (gp.buttons[7] && gp.buttons[7].pressed);

                if (left !== lastInput.left || right !== lastInput.right ||
                    up !== lastInput.up || down !== lastInput.down || fire !== lastInput.fire) {
                    lastInput = { up, down, left, right, fire };
                    fetch(`/api/input?up=${up}&down=${down}&left=${left}&right=${right}&fire=${fire}`, { method: 'POST' }).catch(() => {});
                }
            }
            requestAnimationFrame(pollGamepads);
        }
        requestAnimationFrame(pollGamepads);
    </script>
</body>
</html>";
    }
}
