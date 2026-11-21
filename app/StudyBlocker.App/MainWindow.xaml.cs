using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using StudyBlocker.Common;

namespace StudyBlocker.App;

// The whole app: one window, kept deliberately simple. Every control only
// requests a change; the service applies it or refuses it (plan §6), and the
// window then redraws from what the service reports. During a lock, controls
// that the service would refuse are hidden or disabled instead of being left
// to fail.
public partial class MainWindow : Window
{
    private const int MaxLockMinutes = 7 * 24 * 60;

    // The Custom slider's stops, in minutes: every hour up to a day, then
    // every 6 hours up to 7 days.
    private static readonly int[] CustomLengths =
    [
        .. Enumerable.Range(1, 24).Select(hours => hours * 60),
        .. Enumerable.Range(1, 24).Select(step => (24 + step * 6) * 60),
    ];

    // One switch of the "Hide on YouTube" list. A sub-option sits indented
    // under its parent. Covered: the parent hides the whole area it's in, so
    // while the parent is on it shows as on (and can't be switched) whatever
    // its own setting, which comes back when the parent goes off.
    private sealed record DeclutterItem(DeclutterOption Option, string Label, DeclutterOption? Parent = null, bool Covered = false);

    // The user's list and wording (2026-11-21), in their order.
    private static readonly DeclutterItem[] DeclutterItems =
    [
        new(DeclutterOption.HomeFeed, "Hide home feed"),
        new(DeclutterOption.RedirectHomeToSubscriptions, "Redirect to Subscriptions", DeclutterOption.HomeFeed),
        new(DeclutterOption.VideoSidebar, "Hide video sidebar"),
        new(DeclutterOption.RecommendedVideos, "Hide recommended", DeclutterOption.VideoSidebar, Covered: true),
        new(DeclutterOption.LiveChat, "Hide live chat", DeclutterOption.VideoSidebar, Covered: true),
        new(DeclutterOption.Playlists, "Hide playlist", DeclutterOption.VideoSidebar, Covered: true),
        new(DeclutterOption.Fundraiser, "Hide fundraiser", DeclutterOption.VideoSidebar, Covered: true),
        new(DeclutterOption.EndScreenSuggestions, "Hide end screen feed"),
        new(DeclutterOption.EndScreenCards, "Hide end screen cards"),
        new(DeclutterOption.Shorts, "Hide Shorts"),
        new(DeclutterOption.Comments, "Hide comments"),
        new(DeclutterOption.Mixes, "Hide mixes"),
        new(DeclutterOption.MerchTicketsOffers, "Hide merch, tickets, offers"),
        new(DeclutterOption.VideoInfo, "Hide video info"),
        new(DeclutterOption.TopHeader, "Hide top header"),
        new(DeclutterOption.Notifications, "Hide notifications", DeclutterOption.TopHeader, Covered: true),
        new(DeclutterOption.InaptSearchResults, "Hide inapt search results"),
        new(DeclutterOption.TrendingExplore, "Hide explore, trending"),
        new(DeclutterOption.MoreFromYouTube, "Hide more from YouTube"),
        new(DeclutterOption.SubscriptionsFeed, "Hide subscriptions"),
        new(DeclutterOption.Autoplay, "Disable autoplay"),
        new(DeclutterOption.Annotations, "Disable annotations"),
    ];

    private readonly Dictionary<DeclutterOption, CheckBox> _declutterBoxes = [];

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private ServiceStatus? _status;
    private string _shownLists = "";
    private string _refreshedPolicies = "";
    private bool _refreshing;
    private bool _offline;

    // False only for the preview tool, which draws the window from made-up
    // state (see ShowState) instead of connecting to the service.
    public bool Live { get; init; } = true;

    public MainWindow()
    {
        InitializeComponent();
        LockInBannerText.Text = LinkProtocol.LockedFromLockIn;
        BuildDeclutterList();
        CustomSlider.Maximum = CustomLengths.Length - 1;
        ShowCustomLength();
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (!Live) return;
        await RefreshAsync();
        _timer.Start();
    }

    private void Window_Closed(object? sender, EventArgs e) => _timer.Stop();

    // --- Reading the service ---

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var status = await ServiceClient.GetStatusAsync();
            var channels = await ServiceClient.GetChannelsAsync();
            ShowState(status, channels);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            ShowOffline();
        }
        catch (JsonException)
        {
            ShowOutOfDate();
        }
        catch (ServiceRefusedException ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            _refreshing = false;
        }
    }

    // Draws the window from the service's state.
    public void ShowState(ServiceStatus status, IReadOnlyList<ChannelRuleInfo> channels)
    {
        _status = status;
        if (_offline)
        {
            _offline = false;
            ShowError(null);
        }
        Body.IsEnabled = true;

        var lockState = status.Lock;
        var rules = lockState.Rules;
        var locked = lockState.IsLocked;
        var breaking = locked && lockState.Break is not null;

        // A LockIn session holds the rules the way a lock does: the service
        // refuses every loosening meanwhile. So what it would refuse is
        // disabled here the same way, and the banner says why.
        var sessionHeld = status.Link.SessionActive;
        // LockIn's Habit Builder scheduled lock holds the same way, and a
        // session break doesn't lift it.
        var scheduleHeld = status.Link.ScheduleInForce;
        // The Advanced lock is above all of them, breaks included.
        var advancedHeld = status.Link.AdvancedInForce;
        var held = locked || sessionHeld || scheduleHeld || advancedHeld;
        LockInBanner.Visibility = sessionHeld || scheduleHeld || advancedHeld ? Visibility.Visible : Visibility.Collapsed;
        // On LockIn's session-break nothing is enforced (the service's
        // enforced view), though every setting still shows as it is and still
        // can't loosen.
        var sessionBreak = sessionHeld && status.Link.SessionBreak && !scheduleHeld && !advancedHeld;
        LockInBannerText.Text = advancedHeld
            ? LinkProtocol.AdvancedLockFromLockIn
            : sessionBreak
                ? LinkProtocol.SessionBreakFromLockIn
                : sessionHeld ? LinkProtocol.LockedFromLockIn : LinkProtocol.ScheduledLockFromLockIn;

        // Open browsers don't notice a policy change by themselves for up to
        // 15 minutes, so every change is pushed to them from here. That covers
        // this window's own changes, a lock starting or ending, and sbctl. The
        // short wait lets the service finish writing a change it made on its
        // own (a lock ending) before the browsers look.
        // The switch counts too: off, the service lifts the blocked sites. So
        // do a LockIn session (its lockdown) and its breaks (nothing applies).
        var policies = JsonSerializer.Serialize(new { locked, rules.BlockedDomains, rules.ExtensionEnabled, sessionHeld, sessionBreak, scheduleHeld, advancedHeld });
        if (Live && policies != _refreshedPolicies)
        {
            _refreshedPolicies = policies;
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
                BrowserRefresh.RefreshRunning();
            });
        }

        StatusText.Text = locked ? $"Locked · {Remaining(TimeLeft(lockState))} left" : "Not locked";
        LockButton.Content = locked ? "Extend" : "Lock";
        LockLabel.Text = locked ? "Extend the lock by" : "Lock for";
        CustomGoButton.Content = locked ? "Extend" : "Lock";
        ShowCustomLength();
        AllowChannelButton.Visibility = held ? Visibility.Collapsed : Visibility.Visible;
        // A plain lock shows no note (the user's call, 2026-11-21); only LockIn's
        // session states explain themselves, since they change what is blocked.
        LockedNote.Visibility = held && (sessionBreak || sessionHeld) ? Visibility.Visible : Visibility.Collapsed;
        LockedNote.Text = sessionBreak
            ? "LockIn's session is on a break, so nothing is blocked until the break ends. You can still only make things stricter, and what you add applies once it ends."
            : "While a LockIn session runs you can only make things stricter: nothing can be removed, unticked or switched off until it ends. Incognito, Guest windows and DevTools are off and Shorts are blocked meanwhile.";

        // A lock always has the extension on, and it can't be switched off
        // until the lock ends, nor while a LockIn session runs.
        ExtensionSwitch.IsChecked = rules.ExtensionEnabled;
        ExtensionSwitch.Content = rules.ExtensionEnabled ? "Extension on" : "Extension off";
        ExtensionSwitch.IsEnabled = !held;
        ExtensionOffNote.Visibility = rules.ExtensionEnabled ? Visibility.Collapsed : Visibility.Visible;

        // Plan §10: breaking early keeps the lock fully on until a countdown
        // ends, with "Cancel lock break" one click away the whole time. The
        // button is only offered while it would end the lock sooner.
        BreakBanner.Visibility = breaking ? Visibility.Visible : Visibility.Collapsed;
        BreakText.Text = breaking ? $"Breaking the lock early: it ends in {Remaining(TimeLeft(lockState))} unless you cancel." : "";
        LockButton.Visibility = breaking ? Visibility.Collapsed : Visibility.Visible;
        if (breaking) LockTimes.Visibility = Visibility.Collapsed;
        BreakButton.Visibility = locked && !breaking && !sessionHeld && lockState.DurationMs - lockState.ElapsedMs > status.BreakDelayMs
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Once ticked during a lock (or a LockIn session), a box stays ticked
        // until it ends.
        EducationOnlyBox.IsChecked = rules.EducationOnly;
        EducationOnlyBox.IsEnabled = !(held && rules.EducationOnly);
        BlockAdultBox.IsChecked = rules.BlockAdultContent;
        BlockAdultBox.IsEnabled = !(held && rules.BlockAdultContent);
        foreach (var item in DeclutterItems)
        {
            var box = _declutterBoxes[item.Option];
            var ticked = rules.Declutter.Contains(item.Option);
            var covered = item.Covered && item.Parent is { } parent && rules.Declutter.Contains(parent);
            box.IsChecked = ticked || covered;
            box.IsEnabled = !covered && !(held && ticked);
        }

        // The lists are rebuilt only when something in them changed, so the
        // regular refresh never disturbs a button under the mouse.
        var lists = JsonSerializer.Serialize(new { held, rules.BlockedDomains, rules.BlockedApps, channels });
        if (lists == _shownLists) return;
        _shownLists = lists;
        ShowSites(rules.BlockedDomains, held);
        ShowApps(rules.BlockedApps, held);
        ShowChannels(channels, held);
    }

    private void ShowSites(IReadOnlyList<string> sites, bool locked)
    {
        SitesList.Children.Clear();
        SitesExpander.Header = sites.Count == 0 ? "Show blocked sites" : $"Show blocked sites ({sites.Count})";
        if (sites.Count == 0)
        {
            SitesList.Children.Add(EmptyHint("Nothing blocked yet."));
            return;
        }
        foreach (var site in sites)
            SitesList.Children.Add(Row(site, tag: null, locked ? null : RemoveButton(() => RemoveSiteAsync(site))));
    }

    private void ShowApps(IReadOnlyList<BlockedApp> apps, bool locked)
    {
        AppsList.Children.Clear();
        AppsExpander.Header = apps.Count == 0 ? "Show blocked apps" : $"Show blocked apps ({apps.Count})";
        if (apps.Count == 0)
        {
            AppsList.Children.Add(EmptyHint("No apps blocked yet."));
            return;
        }
        foreach (var app in apps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
            AppsList.Children.Add(Row(app.Name, app.Exe, locked ? null : RemoveButton(() => RemoveAppAsync(app.Exe))));
    }

    private void ShowChannels(IReadOnlyList<ChannelRuleInfo> channels, bool locked)
    {
        ChannelsList.Children.Clear();
        if (channels.Count == 0)
        {
            ChannelsList.Children.Add(EmptyHint("No channels yet. Your answers to the popup on YouTube show up here too."));
            return;
        }

        var ordered = channels
            .OrderByDescending(c => c.AllowedDuringCurrentLock)
            .ThenBy(c => c.Decision == ChannelDecision.Blocked)
            .ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase);
        foreach (var channel in ordered)
        {
            if (channel.AllowedDuringCurrentLock)
            {
                // Plan §9: allowed through the popup during this lock, maybe
                // on impulse, so it's at the top with a way to block it again.
                var row = Row(channel.Name, "Allowed during this lock", TextButton("Block", () => SetChannelAsync(channel, "block")));
                row.SetResourceReference(Border.BackgroundProperty, "SystemFillColorCautionBackgroundBrush");
                ChannelsList.Children.Add(row);
                continue;
            }

            // Taking a channel off the allowed list only makes things
            // stricter, so that works during a lock too; unblocking doesn't.
            var allowed = channel.Decision == ChannelDecision.Allowed;
            var remove = !locked || allowed ? RemoveButton(() => SetChannelAsync(channel, "clear")) : null;
            ChannelsList.Children.Add(Row(channel.Name, allowed ? "Always allowed" : "Blocked", remove));
        }
    }

    private void ShowOffline() =>
        ShowUnavailable("Service not running", "Can't reach the background service, which normally starts with Windows. This window reconnects by itself once it's running.");

    // The service answered in a shape this app doesn't know: it's an older
    // or newer build than the app.
    private void ShowOutOfDate() =>
        ShowUnavailable("Service out of date", "The background service is a different version from this app. Run the installer again so they match; this window reconnects by itself.");

    private void ShowUnavailable(string status, string message)
    {
        _offline = true;
        StatusText.Text = status;
        ShowError(message);
        Body.IsEnabled = false;
    }

    private void ShowError(string? message)
    {
        ErrorText.Text = message ?? "";
        ErrorText.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // --- Changing things ---

    // Every change goes through here: the service applies or refuses it, then
    // the window redraws from what the service now says.
    private async Task RunAsync(Func<Task> change)
    {
        _timer.Stop();
        ShowError(null);
        try
        {
            await change();
        }
        catch (ServiceRefusedException ex)
        {
            ShowError(ex.Message);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            ShowOffline();
        }
        catch (JsonException)
        {
            ShowOutOfDate();
        }
        finally
        {
            _shownLists = "";
            if (Live)
            {
                await RefreshAsync();
                _timer.Start();
            }
        }
    }

    // The lengths only show once Lock (or Extend) is clicked; clicking it
    // again hides them.
    private void LockButton_Click(object sender, RoutedEventArgs e) =>
        LockTimes.Visibility = LockTimes.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    // No question either way: outside a lock it's freely switchable, and
    // during one it can't be switched off at all.
    private async void ExtensionSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (_status is null) return;
        var on = ExtensionSwitch.IsChecked == true;
        var rules = _status.Lock.Rules;
        await RunAsync(() => ServiceClient.UpdateRulesAsync(rules with { ExtensionEnabled = on }));
    }

    private async void LockPreset_Click(object sender, RoutedEventArgs e) =>
        await LockOrExtendAsync(int.Parse((string)((Button)sender).Tag, CultureInfo.InvariantCulture));

    private void Custom_Click(object sender, RoutedEventArgs e)
    {
        var show = CustomArea.Visibility != Visibility.Visible;
        CustomArea.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            ShowCustomLength();
            CustomSlider.Focus();
        }
    }

    private void CustomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => ShowCustomLength();

    // Shows the length picked on the slider. During a lock the time left can't
    // go over 7 days, so the slider stops at the longest extension that fits.
    private void ShowCustomLength()
    {
        var leftMinutes = _status is { Lock.IsLocked: true } status
            ? (int)Math.Ceiling((status.Lock.DurationMs - status.Lock.ElapsedMs) / 60_000.0)
            : 0;
        var longest = Math.Max(0, Array.FindLastIndex(CustomLengths, minutes => minutes <= MaxLockMinutes - leftMinutes));
        if (CustomSlider.Value > longest)
        {
            CustomSlider.Value = longest; // raises ValueChanged again
            return;
        }
        CustomLengthText.Text = Describe(CustomLengths[(int)CustomSlider.Value]);
    }

    private async void CustomGo_Click(object sender, RoutedEventArgs e) =>
        await LockOrExtendAsync(CustomLengths[(int)CustomSlider.Value]);

    private async Task LockOrExtendAsync(int minutes)
    {
        if (_status is null) return;
        var locked = _status.Lock.IsLocked;
        var question = locked
            ? $"Add {Describe(minutes)} to the lock?\n\nThis can't be undone."
            : $"Lock for {Describe(minutes)}?\n\nUntil it ends, you can only extend the lock or make it stricter."
              + (_status.Lock.Rules.ExtensionEnabled ? "" : " This also switches the extension back on.");
        if (!Ask(question)) return;

        LockTimes.Visibility = Visibility.Collapsed;
        CustomArea.Visibility = Visibility.Collapsed;
        await RunAsync(() => locked
            ? ServiceClient.ExtendLockAsync(minutes)
            : ServiceClient.StartLockAsync(minutes));
    }

    private async void BreakLock_Click(object sender, RoutedEventArgs e)
    {
        if (_status is null) return;
        var delay = Describe((int)(_status.BreakDelayMs / 60_000));
        if (!Ask($"Break the lock early?\n\nIt stays fully on for another {delay}, then ends. You can cancel any time before that."))
            return;
        await RunAsync(ServiceClient.BreakLockAsync);
    }

    // The reload arrow: LockIn forgets nothing on its side, the service just stops
    // honouring its key and leaves a fresh offer. Refused during a LockIn session.
    private async void ResetLink_Click(object sender, RoutedEventArgs e) => await RunAsync(ServiceClient.ResetLinkAsync);

    // "Cancel lock break" only keeps the lock on, so it doesn't ask first.
    private async void CancelBreak_Click(object sender, RoutedEventArgs e) => await RunAsync(ServiceClient.CancelBreakAsync);

    private async void AddSite_Click(object sender, RoutedEventArgs e) => await AddSiteAsync();

    private async void SiteBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await AddSiteAsync();
    }

    private async Task AddSiteAsync()
    {
        if (_status is null) return;
        if (SiteName.Normalize(SiteBox.Text) is not { } site)
        {
            ShowError("Enter a site like reddit.com.");
            return;
        }
        SiteBox.Clear();
        var rules = _status.Lock.Rules;
        if (rules.BlockedDomains.Contains(site, StringComparer.OrdinalIgnoreCase)) return;
        await RunAsync(() => ServiceClient.UpdateRulesAsync(rules with { BlockedDomains = [.. rules.BlockedDomains, site] }));
    }

    private Task RemoveSiteAsync(string site)
    {
        var rules = _status!.Lock.Rules;
        return ServiceClient.UpdateRulesAsync(rules with { BlockedDomains = rules.BlockedDomains.Where(s => s != site).ToList() });
    }

    // The open apps are listed fresh each time the list is opened.
    private void AppPicker_DropDownOpened(object? sender, EventArgs e)
    {
        var chosen = (AppPicker.SelectedItem as AppChoice)?.Exe;
        var blocked = _status?.Lock.Rules.BlockedApps ?? [];
        var choices = AppChoice.OpenApps().Where(c => !BlockedApp.Contains(blocked, c.Exe)).ToList();
        AppPicker.ItemsSource = choices;
        AppPicker.SelectedItem = choices.FirstOrDefault(c => c.Exe == chosen);
        if (choices.Count == 0) ShowError("No open apps to block. Use Browse… to choose a program file.");
    }

    private void AppPicker_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        AppPickerPlaceholder.Visibility = AppPicker.SelectedItem is null ? Visibility.Visible : Visibility.Collapsed;

    private async void AddApp_Click(object sender, RoutedEventArgs e)
    {
        if (AppPicker.SelectedItem is not AppChoice choice)
        {
            ShowError("Choose an open app first, or use Browse… to find its program file.");
            return;
        }
        await AddAppAsync(choice);
    }

    private async void BrowseApp_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose an app to block",
            Filter = "Programs (*.exe)|*.exe",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };
        if (dialog.ShowDialog(this) != true) return;
        if (AppChoice.FromFile(dialog.FileName) is not { } choice)
        {
            ShowError("That program is part of Windows, this blocker or LockIn, so it can't be blocked.");
            return;
        }
        await AddAppAsync(choice);
    }

    private async Task AddAppAsync(AppChoice choice)
    {
        if (_status is null) return;
        var rules = _status.Lock.Rules;
        if (BlockedApp.Contains(rules.BlockedApps, choice.Exe)) return;
        var question = $"Block {choice.Name}?\n\nIf it's open, it closes now and anything unsaved in it is lost."
            + (Holding(_status) ? " You can't unblock it until the lock or LockIn session ends." : "");
        if (!Ask(question)) return;

        AppPicker.SelectedItem = null;
        var app = new BlockedApp { Exe = choice.Exe, Name = choice.Name };
        await RunAsync(() => ServiceClient.UpdateRulesAsync(rules with { BlockedApps = [.. rules.BlockedApps, app] }));
    }

    private Task RemoveAppAsync(string exe)
    {
        var rules = _status!.Lock.Rules;
        return ServiceClient.UpdateRulesAsync(rules with
        {
            BlockedApps = rules.BlockedApps.Where(a => !string.Equals(a.Exe, exe, StringComparison.OrdinalIgnoreCase)).ToList(),
        });
    }

    private async void AllowChannel_Click(object sender, RoutedEventArgs e) => await AddChannelAsync("allow");

    private async void BlockChannel_Click(object sender, RoutedEventArgs e) => await AddChannelAsync("block");

    // Enter blocks rather than allows: during a lock Allow isn't available,
    // and blocking by mistake is the harmless direction.
    private async void ChannelBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await AddChannelAsync("block");
    }

    private async Task AddChannelAsync(string rule)
    {
        var name = ChannelBox.Text.Trim();
        if (name.Length == 0)
        {
            ShowError("Type a channel's name first, as it appears under its videos.");
            return;
        }
        ChannelBox.Clear();
        await RunAsync(() => ServiceClient.SetChannelAsync(new ChannelRuleRequest { Name = name, Rule = rule }));
    }

    // A channel saved from the popup is changed by its id; one typed in by
    // name, by that name.
    private static Task SetChannelAsync(ChannelRuleInfo channel, string rule) =>
        ServiceClient.SetChannelAsync(channel.ChannelId is { } id
            ? new ChannelRuleRequest { ChannelId = id, Rule = rule }
            : new ChannelRuleRequest { Name = channel.Name, Rule = rule });

    private async void EducationOnly_Click(object sender, RoutedEventArgs e)
    {
        if (_status is null) return;
        var on = EducationOnlyBox.IsChecked == true;
        if (on && Holding(_status) && !Ask("Turn on education-only mode?\n\nYou can't turn it off again until the lock or LockIn session ends."))
        {
            EducationOnlyBox.IsChecked = false;
            return;
        }
        var rules = _status.Lock.Rules;
        await RunAsync(() => ServiceClient.UpdateRulesAsync(rules with { EducationOnly = on }));
    }

    private async void BlockAdult_Click(object sender, RoutedEventArgs e)
    {
        if (_status is null) return;
        var on = BlockAdultBox.IsChecked == true;
        if (on && Holding(_status) && !Ask("Block adult content?\n\nYou can't turn it off again until the lock or LockIn session ends."))
        {
            BlockAdultBox.IsChecked = false;
            return;
        }
        var rules = _status.Lock.Rules;
        await RunAsync(() => ServiceClient.UpdateRulesAsync(rules with { BlockAdultContent = on }));
    }

    // The switches, in DeclutterItems' order. A switch with sub-options gets
    // a "+" at the end of its row; its sub-options, in an indented block with
    // a line down its left, stay folded away until it's clicked ("−" folds
    // them again). Folded every time the window opens, as in the extension
    // (the user's request, 2026-11-21).
    private void BuildDeclutterList()
    {
        foreach (var item in DeclutterItems.Where(i => i.Parent is null))
        {
            var box = DeclutterSwitch(item);
            var children = DeclutterItems.Where(i => i.Parent == item.Option).ToList();
            if (children.Count == 0)
            {
                DeclutterList.Children.Add(box);
                continue;
            }

            var subOptions = new StackPanel();
            foreach (var child in children) subOptions.Children.Add(DeclutterSwitch(child));
            var rail = new Border
            {
                BorderThickness = new Thickness(2, 0, 0, 0),
                Margin = new Thickness(19, 0, 0, 2),
                Padding = new Thickness(16, 0, 0, 0),
                Child = subOptions,
                Visibility = Visibility.Collapsed,
            };
            rail.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");

            var expand = new Button { Style = (Style)FindResource("ExpandButton"), VerticalAlignment = VerticalAlignment.Center };
            void Show(bool open)
            {
                rail.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                expand.Content = open ? "−" : "+";
                var tip = $"{(open ? "Hide" : "Show")} the options under {item.Label.ToLowerInvariant()}";
                expand.ToolTip = tip;
                System.Windows.Automation.AutomationProperties.SetName(expand, tip);
            }
            Show(false);
            expand.Click += (_, _) => Show(rail.Visibility != Visibility.Visible);

            var row = new DockPanel();
            DockPanel.SetDock(expand, Dock.Right);
            row.Children.Add(expand);
            row.Children.Add(box);
            DeclutterList.Children.Add(row);
            DeclutterList.Children.Add(rail);
        }
    }

    private CheckBox DeclutterSwitch(DeclutterItem item)
    {
        var box = new CheckBox
        {
            Content = item.Label,
            Tag = item.Option,
            Style = (Style)FindResource("Switch"),
            Margin = new Thickness(0, 4, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        box.Click += Declutter_Click;
        _declutterBoxes[item.Option] = box;
        return box;
    }

    private async void Declutter_Click(object sender, RoutedEventArgs e)
    {
        if (_status is null) return;
        var box = (CheckBox)sender;
        var option = (DeclutterOption)box.Tag;
        var hide = box.IsChecked == true;
        if (hide && Holding(_status) && !Ask($"{box.Content} on YouTube?\n\nYou can't turn it off again until the lock or LockIn session ends."))
        {
            box.IsChecked = false;
            return;
        }
        var rules = _status.Lock.Rules;
        var declutter = rules.Declutter.Where(o => o != option).ToList();
        if (hide) declutter.Add(option);
        await RunAsync(() => ServiceClient.UpdateRulesAsync(rules with { Declutter = declutter }));
    }

    // Whether the service is holding the rules at their strictest: a lock is
    // on, a LockIn session is running, or LockIn's scheduled lock is in force.
    private static bool Holding(ServiceStatus status) =>
        status.Lock.IsLocked || status.Link.SessionActive || status.Link.ScheduleInForce || status.Link.AdvancedInForce;

    private bool Ask(string question) =>
        MessageBox.Show(this, question, Title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    // --- Building rows ---

    private Border Row(string text, string? tag, Button? action)
    {
        var panel = new DockPanel();
        if (action is not null)
        {
            DockPanel.SetDock(action, Dock.Right);
            panel.Children.Add(action);
        }

        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        label.Inlines.Add(new Run(text));
        if (tag is not null)
        {
            var tagRun = new Run("   " + tag) { FontSize = 12 };
            tagRun.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorSecondaryBrush");
            label.Inlines.Add(tagRun);
        }
        panel.Children.Add(label);

        return new Border
        {
            Child = panel,
            MinHeight = 34,
            Padding = new Thickness(8, 2, 2, 2),
            CornerRadius = new CornerRadius(4),
        };
    }

    private Button RemoveButton(Func<Task> remove)
    {
        var button = new Button
        {
            Content = "", // "Cancel" (an X) in Windows' own icon font
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 11,
            Padding = new Thickness(10, 7, 10, 7),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            ToolTip = "Remove",
        };
        button.Click += async (_, _) => await RunAsync(remove);
        return button;
    }

    private Button TextButton(string text, Func<Task> action)
    {
        var button = new Button { Content = text, Margin = new Thickness(8, 2, 2, 2) };
        button.Click += async (_, _) => await RunAsync(action);
        return button;
    }

    private TextBlock EmptyHint(string text) =>
        new() { Text = text, Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 4, 0, 0) };

    // --- Formatting ---

    // Until the lock ends: its own time, or a break countdown that finishes
    // first.
    private static TimeSpan TimeLeft(LockState lockState)
    {
        var leftMs = lockState.DurationMs - lockState.ElapsedMs;
        if (lockState.Break is { } countdown)
            leftMs = Math.Min(leftMs, countdown.DurationMs - countdown.ElapsedMs);
        return TimeSpan.FromMilliseconds(Math.Max(0, leftMs));
    }

    private static string Remaining(TimeSpan left)
    {
        if (left.TotalMinutes < 1) return "under a minute";
        if (left.TotalHours < 1) return $"{(int)left.TotalMinutes}m";
        if (left.TotalDays < 1) return $"{(int)left.TotalHours}h {left.Minutes}m";
        return $"{(int)left.TotalDays}d {left.Hours}h";
    }

    private static string Describe(int minutes)
    {
        var length = TimeSpan.FromMinutes(minutes);
        var parts = new List<string>();
        if (length.Days > 0) parts.Add(length.Days == 1 ? "1 day" : $"{length.Days} days");
        if (length.Hours > 0) parts.Add(length.Hours == 1 ? "1 hour" : $"{length.Hours} hours");
        if (length.Minutes > 0) parts.Add(length.Minutes == 1 ? "1 minute" : $"{length.Minutes} minutes");
        return string.Join(" ", parts);
    }
}
