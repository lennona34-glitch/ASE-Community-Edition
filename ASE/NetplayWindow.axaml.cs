using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using ASE.Netplay;
using static ASE.Config;

namespace ASE;

/// <summary>
/// Online play (NETPLAY.md), as a wizard that only ever shows what the next decision needs:
///
///   1. Host a game, or join one.
///   2. The host chooses what to play — its machine shared with players at home or far away, or
///      the MIDI or null-modem cable (each player on their own ST); a guest chooses where the
///      game is — on its network, or over the Internet with the host's code.
///   3. Just the data that needs, then the game itself: the host's players, its code and the
///      button that starts playing together; the guest's games found, or the code box, and once
///      in, what is happening.
///
/// With a session running the window opens on its last step. Single instance and non-modal,
/// like the MT-32 toolbox, and like it holds no UI pause: a session is meant to run alongside
/// the machine. Closing the window leaves a hosted or joined game running (the status bar shows
/// it, the Online menu ends it); a guest that is only looking stops looking, because the search
/// belongs to the window (NETPLAY.md §7.2).
/// </summary>
public partial class NetplayWindow : Window
{
    /// <summary>A role preselected by an invitation or a join request.</summary>
    public enum Modes { Host, Join }

    enum Steps
    {
        Role,           // host or join
        HostKind,       // what the host plays
        GuestWay,       // where the guest's game is
        HostSetup,      // name and port, then Start hosting
        HostLobby,      // hosting: players, code, playing together
        GuestSetup,     // name, then the games found or the code
        GuestLobby,     // connecting, waiting to be let in, connected
    }

    // How long a game found on the network stays listed without answering again
    const long LanExpiryMs = 4000;

    sealed class LanGame
    {
        public IPEndPoint Address;
        public HostFound Info;
        public long SeenAt;
    }

    Steps _step = Steps.Role;
    NetSession.HostKinds _hostKind;
    NetSession.GuestWays _guestWay;

    readonly Dictionary<string, LanGame> _games = new();
    string _gamesSignature = "";
    string _playersSignature = "";
    readonly Dictionary<uint, TextBlock> _rttLabels = new();

    // While the option cards are reset from code, their handler must not act
    bool _syncing;

    // An invitation code being made (the STUN servers take up to three seconds)
    bool _inviting;

    // Guest: a join with a link or a code under way (a link is read from the website first)
    bool _joining;

    // Avalonia's runtime XAML loader (and the designer) look for a parameterless constructor.
    public NetplayWindow() : this(null) { }

    public NetplayWindow(Modes? mode)
    {
        InitializeComponent();

        var config = ConfigOptions.RunninConfig;
        TextName.Text = config.NetplayPlayerName ?? "";
        TextPort.Text = config.NetplayPort.ToString(CultureInfo.InvariantCulture);
        TextAddress.Text = config.NetplayLastAddress ?? "";

        _savedSettings = SettingsKey();

        NetSession.EventReceived += OnNetEvent;
        NetSession.StateChanged += Refresh;
        NetSession.Pumped += OnPumped;
        NetSession.WindowOpen = true;

        ShowMode(mode);
    }

    // A host with a code out, players and a shared game fills a lot of window: never more than
    // the screen has room for (the middle scrolls)
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen != null)
            MaxHeight = screen.WorkingArea.Height / screen.Scaling * 0.92;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        NetSession.EventReceived -= OnNetEvent;
        NetSession.StateChanged -= Refresh;
        NetSession.Pumped -= OnPumped;
        NetSession.WindowOpen = false;

        SaveSettings();

        if (NetSession.Role == NetSession.Roles.Guest && NetSession.GuestState == NetSession.GuestStates.Searching)
            NetSession.Stop();
    }

    /// <summary>The menu opens at role choice. An invitation or join request can go straight
    /// to its role. A running session always opens at its lobby.</summary>
    public void ShowMode(Modes? mode)
    {
        if (SessionStep() is { } running)
            Go(running);
        else
            Go(mode == Modes.Host ? Steps.HostKind
               : mode == Modes.Join ? Steps.GuestWay
               : Steps.Role);
    }

    /// <summary>An invitation link clicked, or given with --join: the guest's step over the
    /// Internet with the link in the box. Joining is still the user's press of Join.</summary>
    public void ShowJoinLink(string link)
    {
        if (NetSession.Role == NetSession.Roles.Host)
        {
            ShowError("An invitation link arrived, but you are hosting a game: stop hosting to join somebody else's.");
            return;
        }

        if (NetSession.Role == NetSession.Roles.Guest && NetSession.GuestState != NetSession.GuestStates.Searching)
        {
            ShowError("An invitation link arrived, but you are already in a game: leave it to join another.");
            return;
        }

        // A guest looking on the local network goes over to the Internet
        if (NetSession.Role == NetSession.Roles.Guest && NetSession.GuestWay != NetSession.GuestWays.Internet)
            NetSession.Stop();

        _guestWay = NetSession.GuestWays.Internet;
        Go(Steps.GuestSetup);
        TextCode.Text = link;
        ButtonPrimary.Focus();
    }

    // ==================== Steps ====================

    // The step a running session belongs on, or null when none runs (a guest only looking for
    // games has no game yet: it stays where the user is)
    static Steps? SessionStep() => NetSession.Role switch
    {
        NetSession.Roles.Host => Steps.HostLobby,
        NetSession.Roles.Guest when NetSession.GuestState != NetSession.GuestStates.Searching => Steps.GuestLobby,
        _ => null,
    };

    void Go(Steps step)
    {
        // A guest that goes back from choosing its game stops looking for one
        if (_step == Steps.GuestSetup && step is Steps.GuestWay or Steps.Role
            && NetSession.Role == NetSession.Roles.Guest && NetSession.GuestState == NetSession.GuestStates.Searching)
        {
            NetSession.Stop();
            _games.Clear();
        }

        _step = step;
        HideError();

        if (step == Steps.HostLobby)
            _hostKind = NetSession.HostKind;

        if (step is Steps.GuestSetup or Steps.GuestLobby && NetSession.Role == NetSession.Roles.Guest)
            _guestWay = NetSession.GuestWay;

        // Choosing a game on the network is looking for one
        if (step == Steps.GuestSetup && _guestWay == NetSession.GuestWays.LocalNetwork && !NetSession.Active)
        {
            if (!NetSession.StartSearching(NetSession.GuestWays.LocalNetwork, out string error))
                ShowError(error);
        }

        // The cards are buttons here: none stays chosen, so coming back to a step can pick again
        _syncing = true;
        foreach (var card in new[] { CardHost, CardJoin, CardTogetherLan, CardTogetherInternet, CardMidi, CardNullModem, CardFindLan, CardWithCode })
            card.IsChecked = false;
        _syncing = false;

        Refresh();
    }

    void OnCardChosen(object sender, RoutedEventArgs e)
    {
        if (_syncing || sender is not RadioButton { IsChecked: true } card)
            return;

        if (card == CardHost)
            Go(Steps.HostKind);
        else if (card == CardJoin)
            Go(Steps.GuestWay);
        else if (card == CardTogetherLan || card == CardTogetherInternet || card == CardMidi || card == CardNullModem)
        {
            _hostKind = card == CardTogetherLan ? NetSession.HostKinds.TogetherLocal
                      : card == CardTogetherInternet ? NetSession.HostKinds.TogetherInternet
                      : card == CardMidi ? NetSession.HostKinds.MidiCable
                      : NetSession.HostKinds.NullModem;
            Go(Steps.HostSetup);
        }
        else if (card == CardFindLan || card == CardWithCode)
        {
            _guestWay = card == CardFindLan ? NetSession.GuestWays.LocalNetwork : NetSession.GuestWays.Internet;
            Go(Steps.GuestSetup);
        }
    }

    void OnBackClick(object sender, RoutedEventArgs e) => Go(_step switch
    {
        Steps.HostKind or Steps.GuestWay => Steps.Role,
        Steps.HostSetup => Steps.HostKind,
        Steps.GuestSetup => Steps.GuestWay,
        _ => _step,
    });

    void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    // The footer's main button: whatever the step is for
    void OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        switch (_step)
        {
            case Steps.HostSetup:
                StartHosting();
                break;

            case Steps.GuestSetup when _guestWay == NetSession.GuestWays.LocalNetwork:
                JoinSelectedGame();
                break;

            case Steps.GuestSetup:
                JoinWithCode();
                break;
        }
    }

    void OnStopClick(object sender, RoutedEventArgs e)
    {
        HideError();

        if (NetSession.Role == NetSession.Roles.Host)
        {
            NetSession.Stop();
            _playersSignature = "";
            Go(Steps.HostKind);
        }
        else
        {
            // Back to choosing the game (a search of the local network starts again by itself)
            NetSession.Leave();
            Go(Steps.GuestSetup);
        }
    }

    // ==================== Layout ====================

    void Refresh()
    {
        // The session can move on without the window: a guest let in or turned away, a host
        // stopped from the Online menu
        if (SessionStep() is { } running && running != _step)
            _step = running;
        else if (_step == Steps.HostLobby && NetSession.Role != NetSession.Roles.Host)
            _step = Steps.HostKind;
        else if (_step == Steps.GuestLobby && SessionStep() == null)
            _step = Steps.GuestSetup;

        if (_step == Steps.HostLobby)
            _hostKind = NetSession.HostKind;

        var step = _step;
        bool hostSetup = step == Steps.HostSetup, hostLobby = step == Steps.HostLobby;
        bool guestSetup = step == Steps.GuestSetup, guestLobby = step == Steps.GuestLobby;
        bool lan = _guestWay == NetSession.GuestWays.LocalNetwork;
        bool together = !IsCable(_hostKind);

        StepRole.IsVisible = step == Steps.Role;
        StepHostKind.IsVisible = step == Steps.HostKind;
        StepGuestWay.IsVisible = step == Steps.GuestWay;
        RefreshProgress(step);

        CardYou.IsVisible = hostSetup || guestSetup;
        PanelPortLabel.IsVisible = TextPort.IsVisible = hostSetup;
        PanelSetupHint.IsVisible = hostSetup;
        TextSetupHint.Text = SetupHint();

        // Host lobby
        CardInvite.IsVisible = hostLobby && _hostKind != NetSession.HostKinds.TogetherLocal;
        CardPlayers.IsVisible = hostLobby;
        CardTogether.IsVisible = hostLobby && together;
        ExpanderAddresses.IsVisible = hostLobby;
        TextAddresses.Text = hostLobby ? AddressList() : "";

        // Guest
        CardLan.IsVisible = guestSetup && lan;
        CardCode.IsVisible = guestSetup && !lan;
        ExpanderLinks.IsVisible = guestSetup && !lan && LinkScheme.Supported;
        RefreshLinks();
        ExpanderAddress.IsVisible = guestSetup;
        CardGuest.IsVisible = guestLobby;

        TextSubtitle.Text = Subtitle();

        // Footer
        ButtonBack.IsVisible = step is Steps.HostKind or Steps.GuestWay or Steps.HostSetup or Steps.GuestSetup;
        ButtonStop.IsVisible = hostLobby || guestLobby;
        ButtonStop.Content = hostLobby ? "Stop hosting"
                           : NetSession.GuestState == NetSession.GuestStates.Connected ? "Leave the game"
                           : "Cancel";

        ButtonPrimary.IsVisible = hostSetup || guestSetup;
        ButtonPrimary.Content = hostSetup ? "Start hosting" : _joining ? "Joining…" : "Join";
        ButtonPrimary.IsEnabled = hostSetup || (!lan && !_joining) || (lan && ListGames.SelectedItem != null);

        TextNote.Text = Note();
        PanelNote.IsVisible = TextNote.Text.Length > 0;

        RefreshStatus();
        RefreshInvite();
        RefreshPlayers(force: true);
        RefreshShare();
        RefreshGames();
        RefreshGuest();
    }

    void RefreshProgress(Steps step)
    {
        int current = step switch
        {
            Steps.Role => 0,
            Steps.HostKind or Steps.GuestWay => 1,
            _ => 2,
        };

        LabelStepChoice.Text = step is Steps.GuestWay or Steps.GuestSetup or Steps.GuestLobby
            ? "02  LOCATION"
            : "02  THE GAME";

        var labels = new[] { LabelStepRole, LabelStepChoice, LabelStepConnect };
        var bars = new[] { BarStepRole, BarStepChoice, BarStepConnect };
        for (int i = 0; i < labels.Length; i++)
        {
            SetStepClass(labels[i], "current", i == current);
            SetStepClass(labels[i], "complete", i < current);
            SetStepClass(bars[i], "current", i == current);
            SetStepClass(bars[i], "complete", i < current);
        }
    }

    static void SetStepClass(Control control, string name, bool enabled)
    {
        if (enabled && !control.Classes.Contains(name))
            control.Classes.Add(name);
        else if (!enabled)
            control.Classes.Remove(name);
    }

    string Subtitle() => _step switch
    {
        Steps.Role => "Choose how you want to connect with other players.",
        Steps.HostKind => "Choose the game mode and where your players are.",
        Steps.GuestWay => "Choose where to find the host's game.",
        Steps.HostSetup or Steps.HostLobby => "Host · " + KindName(_hostKind),
        _ => _guestWay == NetSession.GuestWays.LocalNetwork ? "Join · A game on your network" : "Join · A game over the Internet",
    };

    static string KindName(NetSession.HostKinds kind) => kind switch
    {
        NetSession.HostKinds.TogetherLocal => "Together, at home",
        NetSession.HostKinds.TogetherInternet => "Together, over the Internet",
        NetSession.HostKinds.NullModem => "Null-modem cable",
        _ => "MIDI cable",
    };

    // The kinds where every player runs their own ST, linked by a cable
    static bool IsCable(NetSession.HostKinds kind) => kind is NetSession.HostKinds.MidiCable or NetSession.HostKinds.NullModem;

    // What happens once the host starts, for the kind it chose
    string SetupHint() => _hostKind switch
    {
        NetSession.HostKinds.TogetherLocal =>
            "Players on your network will find this session automatically. Load the game, then press Play together when everyone has joined.",
        NetSession.HostKinds.TogetherInternet => NetSession.WebAddress is { } web
            ? $"ASE will create a private invitation via {web.Host}."
            : $"ASE will create an invitation code.",
        NetSession.HostKinds.NullModem =>
            "You and one other player each load the game on your own ST, as with a real cable between the serial ports. A player on your network finds this session automatically; one on the Internet joins with a code.",
        _ =>
            "Each player loads the game on their own ST. Local players find this session automatically; Internet players can join with a code.",
    };

    string Note()
    {
        if (_step is Steps.HostSetup)
            return "Whoever joins sees your IP address." + (OperatingSystem.IsWindows()
                ? " The first time you host, Windows asks whether ASE may use the network: allow it on private networks, or nobody will be able to join."
                : "");

        if (_step is Steps.GuestSetup)
            return "The host sees your IP address.";

        return "";
    }

    void RefreshStatus()
    {
        string status = _step switch
        {
            Steps.HostLobby or Steps.GuestLobby => NetSession.StatusText,
            _ => null,
        };

        PillStatus.IsVisible = status != null;
        TextStatus.Text = status ?? "";
    }

    // The addresses a player elsewhere can be given: this computer's on the local networks,
    // and its global IPv6 ones
    static string AddressList()
    {
        int port = NetSession.Port;
        var lines = new List<string>();

        foreach (var a in NetTransport.LocalAddresses())
        {
            if (a.AddressFamily == AddressFamily.InterNetwork)
                lines.Add($"{a}:{port}");
            else if (NetSession.HasIPv6)
                lines.Add($"[{a}]:{port}   (IPv6)");
        }

        return lines.Count == 0 ? $"Port {port}" : string.Join(Environment.NewLine, lines);
    }

    // ==================== Host ====================

    void StartHosting()
    {
        HideError();

        if (!ApplySettings(out string error) || !NetSession.StartHosting(_hostKind, out error))
        {
            ShowError(error);
            return;
        }

        Go(Steps.HostLobby);

        // Over the Internet the code is the point: made straight away
        if (_hostKind == NetSession.HostKinds.TogetherInternet)
            MakeInvitation();
    }

    void RefreshPlayers(bool force = false)
    {
        if (_step != Steps.HostLobby)
        {
            PanelPlayers.Children.Clear();
            _rttLabels.Clear();
            _playersSignature = "";
            return;
        }

        var peers = NetSession.Peers.Where(p => p.State != PeerStates.Joining).ToList();
        string signature = string.Join(";", peers.Select(p => $"{p.Id}:{p.State}:{p.WithCode}"));

        if (force || signature != _playersSignature)
        {
            _playersSignature = signature;
            PanelPlayers.Children.Clear();
            _rttLabels.Clear();

            foreach (var p in peers)
                PanelPlayers.Children.Add(PlayerRow(p));
        }
        else
        {
            foreach (var p in peers)
                if (_rttLabels.TryGetValue(p.Id, out var label))
                    label.Text = NetSession.Rtt(p);
        }

        // Under the title: who may come, while nobody has; the MIDI ring or the null-modem cable,
        // once it links somebody
        string ring = NetSession.MidiLinkText;
        string serial = NetSession.SerialLinkText;
        string linked = ring != null ? $"MIDI ring: {ring}. Everybody loads the game on their own ST."
                      : serial != null ? $"Serial ports linked: {serial}. Each of you loads the game on your own ST and picks its serial link."
                      : null;

        TextNoPlayers.IsVisible = peers.Count == 0 || linked != null;
        TextNoPlayers.Text = peers.Count > 0 && linked != null ? linked : _hostKind switch
        {
            NetSession.HostKinds.TogetherLocal => "Nobody yet. Whoever joins from your network appears here, and you let them in.",
            NetSession.HostKinds.TogetherInternet => NetSession.Link == NetSession.LinkStates.Ready
                ? "Nobody yet. Send the link above; whoever joins with it appears here, and you let them in."
                : "Nobody yet. Send the code above; whoever joins with it appears here, and you let them in.",
            NetSession.HostKinds.NullModem => "Nobody yet. Whoever joins appears here, and you let them in: the cable links your serial port to theirs. It has two ends, so one player.",
            _ => "Nobody yet. Whoever joins appears here, and you let them in: the MIDI ports link in a ring as they do.",
        };
    }

    Control PlayerRow(PeerInfo p)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };

        var who = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        who.Children.Add(new TextBlock { Text = p.Name, FontWeight = FontWeight.SemiBold });
        who.Children.Add(new TextBlock
        {
            Classes = { "description" },
            Text = (p.State == PeerStates.AwaitingDecision ? "Asks to join · " : "")
                   + (p.WithCode ? "with your code · " : "")
                   + $"{p.Address} · ASE {p.AseVersion}",
        });
        row.Children.Add(who);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(actions, 1);

        if (p.State == PeerStates.AwaitingDecision)
        {
            actions.Children.Add(ActionButton("Decline", () => NetSession.Reject(p.Id), accent: false));
            actions.Children.Add(ActionButton("Let in", () => NetSession.Accept(p.Id), accent: true));
        }
        else
        {
            var rtt = new TextBlock { Classes = { "valueBadge" }, Text = NetSession.Rtt(p) };
            _rttLabels[p.Id] = rtt;
            actions.Children.Add(rtt);
            actions.Children.Add(ActionButton("Remove", () => NetSession.Kick(p.Id), accent: false));
        }

        row.Children.Add(actions);

        return new Border
        {
            Classes = { "keycap" },
            Padding = new Avalonia.Thickness(10, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = row,
        };
    }

    static Button ActionButton(string text, Action action, bool accent)
    {
        var b = new Button { Content = text, Classes = { "compact" } };
        if (accent)
            b.Classes.Add("accent");
        b.Click += (_, _) => action();
        return b;
    }

    // The code card: over the Internet it is the way in and is made on starting; with the cables
    // it is there for whoever is not on the network, made on demand
    void RefreshInvite()
    {
        if (_step != Steps.HostLobby)
            return;

        string code = NetSession.InvitationText;
        bool cable = IsCable(_hostKind);
        var link = NetSession.Link;
        bool linked = link is NetSession.LinkStates.Ready or NetSession.LinkStates.Used or NetSession.LinkStates.Expired;
        bool web = NetSession.WebAddress != null;
        string site = NetSession.WebAddress?.Host;

        // With a link, the link is what goes out and the code waits in the expander; without
        // one (no website configured, or it failed), the code as before
        string shown = link == NetSession.LinkStates.Ready ? NetSession.LinkText : linked ? null : code;
        PanelInvite.IsVisible = shown != null;
        if (TextInvite.Text != (shown ?? ""))
            TextInvite.Text = shown ?? "";

        TextLinkCode.IsVisible = link == NetSession.LinkStates.Ready;
        TextLinkCode.Text = $"Or the code {NetSession.LinkCode}, to type into their ASE.";

        ExpanderManualCode.IsVisible = linked && code != null;
        if (TextManualCode.Text != code)
            TextManualCode.Text = code ?? "";
        PanelAnswer.IsVisible = code != null && (!linked || ExpanderManualCode.IsExpanded);

        ButtonInvite.IsVisible = code == null || cable || link is NetSession.LinkStates.Used or NetSession.LinkStates.Expired;
        ButtonInvite.Content = _inviting ? "Asking…" : code == null ? (web ? "Create link" : "Create code") : web ? "New link" : "New code";
        ButtonInvite.IsEnabled = !_inviting;

        TextInviteTitle.Text = link switch
        {
            NetSession.LinkStates.Ready => "Send this link",
            NetSession.LinkStates.Used => "The link was used",
            NetSession.LinkStates.Expired => "The link expired",
            _ => code != null ? "Send this code" : cable ? "Players on the Internet?" : "Your invitation",
        };

        string making = $"Asking for your public address" + (web ? $", your router to open the game's port and {site} to keep the link…" : ", and your router to open the game's port…");

        TextInviteHint.Text = _inviting ? making : link switch
        {
            NetSession.LinkStates.Ready =>
                $"By chat or by mail, to one player: it works once, for ten minutes, and their ASE does the rest. {LinkRouterNote()}Whoever uses it sees your IP address.",
            NetSession.LinkStates.Used =>
                "A player answered: they should appear in the list now, asking to join. For somebody else, make a new link.",
            NetSession.LinkStates.Expired =>
                "Nobody used it in ten minutes. Make a new one, or send the code, which does not expire.",
            _ when code == null =>
                cable ? $"Make {(web ? "a link" : "a code")} to send them. It asks for your public address, and your router to open the game's port." : "The invitation could not be made.",
            NetSession.LinkStates.Failed =>
                $"{NetSession.LinkProblem} The code works without it: by chat or by mail, to whoever you want to play with. {RouterNote()} Whoever uses it sees your IP address.",
            _ =>
                $"By chat or by mail, to whoever you want to play with. {RouterNote()} Whoever uses the code sees your IP address.",
        };
    }

    // With a link the answer comes by itself: the router matters only when it says something
    static string LinkRouterNote() => NetSession.Router switch
    {
        NetSession.RouterStates.Opened => $"Your router opened port {NetSession.RouterMapping.ExternalPort} for the game. ",
        NetSession.RouterStates.BehindAnotherNat => "Your router cannot open a port for you (there is another NAT beyond it). ",
        _ => "",
    };

    void OnManualCodeToggled(object sender, RoutedEventArgs e) => RefreshInvite();

    void OnCopyManualCodeClick(object sender, RoutedEventArgs e) => CopyToClipboard(NetSession.InvitationText);

    // Whether the code is all a player needs, and what to do when it is not
    static string RouterNote()
    {
        int port = ConfigOptions.RunninConfig.NetplayPort;

        return NetSession.Router switch
        {
            NetSession.RouterStates.Opened =>
                $"Your router opened port {NetSession.RouterMapping.ExternalPort} for the game ({NetSession.RouterMapping.ProtocolName}): the code is all they need.",
            NetSession.RouterStates.BehindAnotherNat =>
                "Your router cannot open a port for you: there is another NAT beyond it (your operator's, or a second router). If they cannot get in, they send you an answer code.",
            _ when NetSession.Nat == NatKind.Open =>
                "This computer is on the Internet directly: the code is all they need.",
            _ =>
                $"Your router did not open a port for the game. If they cannot get in, they send you an answer code; " +
                $"a player on a phone's connection may also need UDP port {port} forwarded to this computer in your router.",
        };
    }

    void OnInviteClick(object sender, RoutedEventArgs e) => MakeInvitation();

    async void MakeInvitation()
    {
        HideError();
        _inviting = true;
        RefreshInvite();

        string error = await NetSession.CreateInvitation();

        _inviting = false;
        if (error != null)
            ShowError(error);
        Refresh();
    }

    void OnCopyInviteClick(object sender, RoutedEventArgs e) =>
        CopyToClipboard(NetSession.Link == NetSession.LinkStates.Ready ? NetSession.LinkText : NetSession.InvitationText);

    void OnAnswerKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnUseAnswerClick(sender, e);
            e.Handled = true;
        }
    }

    void OnUseAnswerClick(object sender, RoutedEventArgs e)
    {
        HideError();

        string error = NetSession.UseAnswer(TextAnswer.Text);
        if (error != null)
        {
            ShowError(error);
            return;
        }

        TextAnswer.Text = "";
        TextInviteHint.Text = "Opening the way to that player for 30 seconds: they should appear in the list now, asking to join.";
    }

    // The host's last step: start playing together, or stop
    void RefreshShare()
    {
        if (_step != Steps.HostLobby || IsCable(_hostKind))
            return;

        bool sharing = Lockstep.IsHost;
        int connected = NetSession.Peers.Count(p => p.State == PeerStates.Connected);

        TextTogetherTitle.Text = sharing ? "Playing together" : connected == 0 ? "Ready when they are" : "Ready?";
        ButtonShare.Content = sharing ? "Stop playing together" : "Play together";
        ButtonShare.Classes.Set("accent", !sharing);
        ButtonShare.IsEnabled = sharing || connected > 0;

        TextShareHint.Text = sharing
            ? $"Everybody is playing your machine, {Lockstep.Delay} frames behind the controls. Players who join now get it too."
            : connected == 0
                ? "Once somebody is in, load the game and press Play together: they get your machine as it is, and everybody plays it."
                : "Load the game, then press Play together: everybody gets your machine as it is. Each player plugs their joystick or keyboard into a port in their own Configuration ▸ Input — agree who takes which.";
    }

    void OnShareClick(object sender, RoutedEventArgs e)
    {
        HideError();

        if (Lockstep.IsHost)
        {
            NetSession.StopSharing();
        }
        else
        {
            string error = NetSession.StartSharing();
            if (error != null)
                ShowError(error);
        }

        Refresh();
    }

    // ==================== Guest ====================

    void OnGameSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_step == Steps.GuestSetup)
            ButtonPrimary.IsEnabled = _guestWay != NetSession.GuestWays.LocalNetwork || ListGames.SelectedItem != null;
    }

    void OnGameDoubleTapped(object sender, TappedEventArgs e)
    {
        if (ListGames.SelectedItem is ListBoxItem)
            JoinSelectedGame();
    }

    void JoinSelectedGame()
    {
        HideError();

        if (ListGames.SelectedItem is not ListBoxItem { Tag: string key } || !_games.TryGetValue(key, out var game))
        {
            ShowError("Pick a game from the list first.");
            return;
        }

        if (!ApplySettings(out string error))
        {
            ShowError(error);
            return;
        }

        error = NetSession.Join(game.Address, game.Info.Name);
        if (error != null)
            ShowError(error);

        Refresh();
    }

    void OnCodeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            JoinWithCode();
            e.Handled = true;
        }
    }

    async void JoinWithCode()
    {
        if (_joining)
            return;

        HideError();

        if (!ApplySettings(out string error))
        {
            ShowError(error);
            return;
        }

        // A link is read from the website first, which takes a moment
        _joining = true;
        Refresh();

        try
        {
            error = await NetSession.JoinWithCode(TextCode.Text);
        }
        finally
        {
            _joining = false;
        }

        if (error != null)
            ShowError(error);

        Refresh();
    }

    void OnAddressKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnJoinAddressClick(sender, e);
            e.Handled = true;
        }
    }

    async void OnJoinAddressClick(object sender, RoutedEventArgs e)
    {
        HideError();

        string text = (TextAddress.Text ?? "").Trim();
        if (text.Length == 0)
        {
            ShowError("Type the host's address first.");
            return;
        }

        if (!ApplySettings(out string error))
        {
            ShowError(error);
            return;
        }

        ConfigOptions.RunninConfig.NetplayLastAddress = text;
        SaveSettings();

        // Joining by address needs the network open; over the Internet it may not be yet
        if (!NetSession.Active && !NetSession.StartSearching(_guestWay, out error))
        {
            ShowError(error);
            return;
        }

        (_, error) = await NetSession.Join(text);
        if (error != null)
            ShowError(error);

        Refresh();
    }

    // The guest's last step: what is happening, in words
    void RefreshGuest()
    {
        if (_step != Steps.GuestLobby)
        {
            CardAnswerCode.IsVisible = false;
            return;
        }

        string host = NetSession.Remote;
        var state = NetSession.GuestState;

        (TextGuestTitle.Text, TextGuestDetail.Text) = state switch
        {
            NetSession.GuestStates.Connecting => ($"Connecting to {host}…",
                NetSession.JoinedWithCode ? "Trying every address in the code. This can take a few seconds." : "This takes a second or two."),
            NetSession.GuestStates.WaitingForAccept => ($"Waiting for {host} to let you in…", "They see your name in their window."),
            _ when Lockstep.IsGuest => ($"Playing on {host}'s machine",
                "Both joystick ports and the keyboard are everybody's: plug yours into a port in Configuration ▸ Input, and agree with the others who takes which. " +
                "Anybody can change disks; resetting and loading games that need a reset are the host's. When the game ends you keep the machine where it was."),
            _ when NetSession.HostGame == NetSession.GameKinds.MidiCable => ($"In {host}'s MIDI ring",
                $"Your MIDI ports are linked {NetSession.MidiLinkText}. Load MIDI Maze (or the game you agreed on) on your own ST and set the game up from its screens, as with real cables."),
            _ when NetSession.HostGame == NetSession.GameKinds.NullModem => ($"Linked to {host} by a null-modem cable",
                $"Your serial port is linked to {host}'s. Load the game you agreed on on your own ST and choose its serial link (null modem) on both machines, as with a real cable."),
            _ when NetSession.HostGame == NetSession.GameKinds.Shared => ($"In {host}'s game",
                $"Waiting for {host} to start playing together: their machine will appear here on its own."),
            _ => ($"In {host}'s game", ""),
        };

        // While joining with a code: the answer for a host whose router has to be opened
        bool answer = NetSession.JoinedWithCode && state is NetSession.GuestStates.Connecting;
        CardAnswerCode.IsVisible = answer;

        if (answer)
        {
            string code = NetSession.AnswerText;
            string text = code ?? $"Asking {NetSession.StunServers} for your public address…";
            if (TextAnswerCode.Text != text)
                TextAnswerCode.Text = text;

            ButtonCopyAnswer.IsEnabled = code != null;
            TextAnswerHint.Text = NetSession.AnswerThroughWeb
                ? $"Your answer went to the host through {NetSession.WebAddress?.Host}, and their ASE opens the way to you by itself. " +
                  "If nothing happens within a minute, send them this answer code. This side keeps trying for five minutes."
                : "If it does not connect within a few seconds, send this answer code to the host: pasting it in their window opens their router to you. " +
                  "This side keeps trying for five minutes. Whoever gets it sees your IP address.";
        }
    }

    void OnCopyAnswerClick(object sender, RoutedEventArgs e) => CopyToClipboard(NetSession.AnswerText);

    // ==================== Invitation links that open ASE ====================

    ConfigOptions.LinkChoices? _linksShownFor;
    bool _linksBusy;

    // The question while it has not been answered (open, so it is seen), what was answered
    // afterwards (folded), and the way to change it either way
    void RefreshLinks()
    {
        if (!ExpanderLinks.IsVisible)
            return;

        var choice = ConfigOptions.RunninConfig.NetplayOpenLinks;
        string what = OperatingSystem.IsMacOS()
            ? "added to this computer as the program for \"ase:\" links (through a small helper, ASE Links, in your Library folder)"
            : "added to this computer as the program for \"ase:\" links";

        ExpanderLinks.Header = choice switch
        {
            ConfigOptions.LinkChoices.Yes => "Invitation links open in ASE",
            ConfigOptions.LinkChoices.No => "Invitation links do not open ASE",
            _ => "Open invitation links in ASE?",
        };

        TextLinksHint.Text = _linksBusy ? "One moment…" : choice == ConfigOptions.LinkChoices.Yes
            ? "On an invitation page, Open in ASE brings ASE up with the link, ready to join. Taking it out removes ASE as the program for \"ase:\" links."
            : $"An invitation link clicked in a browser or a chat could bring ASE up with it, ready to join, instead of being copied here. For that, ASE has to be {what}. Only if you want it; you can take it out here at any time.";

        ButtonLinksYes.IsVisible = choice != ConfigOptions.LinkChoices.Yes;
        ButtonLinksYes.Content = choice == ConfigOptions.LinkChoices.Ask ? "Yes, add it" : "Add it";
        ButtonLinksNo.IsVisible = choice != ConfigOptions.LinkChoices.No;
        ButtonLinksNo.Content = choice == ConfigOptions.LinkChoices.Yes ? "Take it out" : "No, thanks";
        ButtonLinksYes.IsEnabled = ButtonLinksNo.IsEnabled = !_linksBusy;

        if (_linksShownFor != choice)
        {
            _linksShownFor = choice;
            ExpanderLinks.IsExpanded = choice == ConfigOptions.LinkChoices.Ask;
        }
    }

    async void OnLinksYesClick(object sender, RoutedEventArgs e)
    {
        HideError();
        _linksBusy = true;
        RefreshLinks();

        string error = null;
        bool ok = await Task.Run(() => LinkScheme.Register(out error));

        _linksBusy = false;
        if (ok)
        {
            var config = ConfigOptions.RunninConfig;
            config.NetplayOpenLinks = ConfigOptions.LinkChoices.Yes;
            config.NetplayLinksLauncher = LinkScheme.LauncherSignature;
            Program.Config.DumpJsonConfig();
            ASEMain.MainWindow?.ListenForInvitationLinks();
            ColoredConsole.WriteLine("Online play: invitation links now open ASE (the \"ase:\" link type was added, as asked)", ConfigOptions.DebugModes.Quiet);
        }
        else
        {
            ShowError($"ASE could not be added for invitation links: {error}");
        }

        RefreshLinks();
    }

    async void OnLinksNoClick(object sender, RoutedEventArgs e)
    {
        HideError();
        var config = ConfigOptions.RunninConfig;
        string error = null;

        if (config.NetplayOpenLinks == ConfigOptions.LinkChoices.Yes || LinkScheme.IsRegistered)
        {
            _linksBusy = true;
            RefreshLinks();
            bool ok = await Task.Run(() => LinkScheme.Unregister(out error));
            _linksBusy = false;

            if (!ok)
            {
                ShowError($"ASE could not be taken out for invitation links: {error}");
                RefreshLinks();
                return;
            }

            ColoredConsole.WriteLine("Online play: invitation links no longer open ASE (the \"ase:\" link type was removed)", ConfigOptions.DebugModes.Quiet);
        }

        config.NetplayOpenLinks = ConfigOptions.LinkChoices.No;
        config.NetplayLinksLauncher = "";
        Program.Config.DumpJsonConfig();
        RefreshLinks();
    }

    void RefreshGames()
    {
        bool searching = NetSession.Role == NetSession.Roles.Guest && NetSession.GuestState == NetSession.GuestStates.Searching;

        // A host on this same computer answers both on loopback and on its network address:
        // one entry is enough, the network one
        var shown = _games.Values
            .Where(g => !(IPAddress.IsLoopback(g.Address.Address)
                          && _games.Values.Any(o => o != g && o.Info.Name == g.Info.Name && o.Address.Port == g.Address.Port)))
            .OrderBy(g => g.Info.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        TextLanHint.Text = !searching ? ""
            : shown.Count == 0 ? $"Looking for games on your network… A host using another port than {ConfigOptions.RunninConfig.NetplayPort} does not show up here: join it by address."
            : "Double-click a game, or pick it and press Join.";

        string signature = string.Join(";", shown.Select(g =>
            $"{g.Address}|{g.Info.Name}|{g.Info.Game}|{g.Info.Players}|{g.Info.Accepting}|{g.Info.Compatible}"));

        if (signature == _gamesSignature)
            return;

        _gamesSignature = signature;

        string selected = (ListGames.SelectedItem as ListBoxItem)?.Tag as string;
        ListGames.Items.Clear();

        foreach (var g in shown)
        {
            string key = g.Address.ToString();
            var info = g.Info;

            var content = new StackPanel { Spacing = 1 };
            content.Children.Add(new TextBlock { Text = $"{info.Name} — {info.Game}", FontWeight = FontWeight.SemiBold });

            string details = $"{key} · ASE {info.AseVersion} · {info.Players}/{info.MaxPlayers} players";
            if (!info.Compatible) details += " · another version of ASE";
            else if (!info.Accepting) details += " · not letting anyone in";
            content.Children.Add(new TextBlock { Classes = { "description" }, Text = details });

            var item = new ListBoxItem { Content = content, Tag = key, IsEnabled = info.Compatible };
            ListGames.Items.Add(item);

            if (key == selected)
                ListGames.SelectedItem = item;
        }

        // One game and nothing chosen: that one, so Join is all it takes
        if (ListGames.SelectedItem == null && ListGames.Items.Count == 1 && ListGames.Items[0] is ListBoxItem { IsEnabled: true } only)
            ListGames.SelectedItem = only;
    }

    // ==================== Events ====================

    void OnNetEvent(NetEvent e)
    {
        switch (e)
        {
            case HostFound found:
                string key = found.Address.ToString();
                _games[key] = new LanGame { Address = found.Address, Info = found, SeenAt = Environment.TickCount64 };
                RefreshGames();
                break;

            case ConnectFailed failed:
                ShowError(failed.Reason);
                break;

            case PeerLeft left when NetSession.Role == NetSession.Roles.Guest:
                ShowError($"{Capitalised(left.Reason)}.");
                break;

            case JoinRequest:
                Activate();
                break;
        }

        Refresh();
    }

    void OnPumped()
    {
        long now = Environment.TickCount64;
        int before = _games.Count;
        foreach (var key in _games.Where(kv => now - kv.Value.SeenAt > LanExpiryMs).Select(kv => kv.Key).ToList())
            _games.Remove(key);

        if (_games.Count != before)
            RefreshGames();

        RefreshPlayers();
        RefreshStatus();
        RefreshShare();
        RefreshGuest();
    }

    // ==================== Settings ====================

    bool TryReadPort(out int port) =>
        int.TryParse((TextPort.Text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out port)
        && port is > 0 and < 65536;

    /// <summary>Takes the name and, for a host, the port from the boxes into the running
    /// configuration.</summary>
    bool ApplySettings(out string error)
    {
        error = null;
        var config = ConfigOptions.RunninConfig;

        string name = (TextName.Text ?? "").Trim();
        if (name.Length == 0)
        {
            error = "Type your name first: it is what the other players see.";
            return false;
        }

        config.NetplayPlayerName = name;

        if (_step == Steps.HostSetup)
        {
            if (!TryReadPort(out int port))
            {
                error = "The port must be a number between 1 and 65535.";
                return false;
            }

            config.NetplayPort = port;
        }

        SaveSettings();
        return true;
    }

    // Online play keeps its own few settings; config.json is written only when one changed
    string _savedSettings;

    static string SettingsKey()
    {
        var c = ConfigOptions.RunninConfig;
        return $"{c.NetplayPlayerName}\n{c.NetplayPort}\n{c.NetplayLastAddress}";
    }

    void SaveSettings()
    {
        string now = SettingsKey();
        if (now == _savedSettings)
            return;

        _savedSettings = now;
        Program.Config.DumpJsonConfig();
    }

    // ==================== Messages ====================

    async void CopyToClipboard(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null || string.IsNullOrEmpty(text))
            return;

        try { await clipboard.SetTextAsync(text); }
        catch (Exception ex) { ShowError($"Could not copy: {ex.Message}"); }
    }

    void ShowError(string text)
    {
        TextError.Text = text ?? "";
        TextError.IsVisible = !string.IsNullOrEmpty(text);
    }

    void HideError() => ShowError(null);

    static string Capitalised(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
