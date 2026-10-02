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
    // Until the extension is store-installed, a lock's DevTools policy also
    // switches off Developer mode, which disables the unpacked extension for
    // the whole lock (see LockRequest.DevKeepDevTools). Set this to false
    // once the extension is published (Milestone 0 Phase B / 9).
    private const bool KeepDevToolsWhileExtensionIsUnpacked = true;

    private const int MaxLockMinutes = 7 * 24 * 60;

    // The Custom slider's stops, in minutes: every hour up to a day, then
    // every 6 hours up to 7 days.
    private static readonly int[] CustomLengths =
    [
        .. Enumerable.Range(1, 24).Select(hours => hours * 60),
        .. Enumerable.Range(1, 24).Select(step => (24 + step * 6) * 60),
    ];

    private static readonly (DeclutterOption Option, string Label)[] DeclutterLabels =
    [
        (DeclutterOption.HomeFeed, "Home feed"),
        (DeclutterOption.RecommendedVideos, "Recommended videos"),
        (DeclutterOption.EndScreenSuggestions, "End-screen suggestions"),
        (DeclutterOption.Autoplay, "Autoplay"),
        (DeclutterOption.Comments, "Comments"),
        (DeclutterOption.TrendingExplore, "Trending / Explore"),
        (DeclutterOption.LiveChat, "Live chat"),
        (DeclutterOption.SubscriptionsFeed, "Subscriptions feed"),
    ];

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
        foreach (var (option, label) in DeclutterLabels)
        {
            var box = new CheckBox { Content = label, Tag = option, Margin = new Thickness(0, 2, 0, 2) };
            box.Click += Declutter_Click;
            DeclutterGrid.Children.Add(box);
        }
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

        // Open browsers don't notice a policy change by themselves for up to
        // 15 minutes, so every change is pushed to them from here. That covers
        // this window's own changes, a lock starting or ending, and sbctl. The
        // short wait lets the service finish writing a change it made on its
        // own (a lock ending) before the browsers look.
        // The switch counts too: off, the service lifts the blocked sites.
        var policies = JsonSerializer.Serialize(new { locked, lockState.DevKeepDevTools, rules.BlockedDomains, rules.ExtensionEnabled });
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
        AllowChannelButton.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
        LockedNote.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;

        // A lock always has the extension on, and it can't be switched off
        // until the lock ends.
        ExtensionSwitch.IsChecked = rules.ExtensionEnabled;
        ExtensionSwitch.Content = rules.ExtensionEnabled ? "Extension on" : "Extension off";
        ExtensionSwitch.IsEnabled = !locked;
        ExtensionOffNote.Visibility = rules.ExtensionEnabled ? Visibility.Collapsed : Visibility.Visible;

        // Plan §10: breaking early keeps the lock fully on until a countdown
        // ends, with "Cancel lock break" one click away the whole time. The
        // button is only offered while it would end the lock sooner.
        BreakBanner.Visibility = breaking ? Visibility.Visible : Visibility.Collapsed;
        BreakText.Text = breaking ? $"Breaking the lock early: it ends in {Remaining(TimeLeft(lockState))} unless you cancel." : "";
        LockButton.Visibility = breaking ? Visibility.Collapsed : Visibility.Visible;
        if (breaking) LockTimes.Visibility = Visibility.Collapsed;
        BreakButton.Visibility = locked && !breaking && lockState.DurationMs - lockState.ElapsedMs > status.BreakDelayMs
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Once ticked during a lock, a box stays ticked until the lock ends.
        EducationOnlyBox.IsChecked = rules.EducationOnly;
        EducationOnlyBox.IsEnabled = !(locked && rules.EducationOnly);
        foreach (CheckBox box in DeclutterGrid.Children)
        {
            var ticked = rules.Declutter.Contains((DeclutterOption)box.Tag);
            box.IsChecked = ticked;
            box.IsEnabled = !(locked && ticked);
        }

        // The lists are rebuilt only when something in them changed, so the
        // regular refresh never disturbs a button under the mouse.
        var lists = JsonSerializer.Serialize(new { locked, rules.BlockedDomains, channels });
        if (lists == _shownLists) return;
        _shownLists = lists;
        ShowSites(rules.BlockedDomains, locked);
        ShowChannels(channels, locked);
    }

    private void ShowSites(IReadOnlyList<string> sites, bool locked)
    {
        SitesList.Children.Clear();
        if (sites.Count == 0)
        {
            SitesList.Children.Add(EmptyHint("Nothing blocked yet."));
            return;
        }
        foreach (var site in sites)
            SitesList.Children.Add(Row(site, tag: null, locked ? null : RemoveButton(() => RemoveSiteAsync(site))));
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
            : ServiceClient.StartLockAsync(minutes, KeepDevToolsWhileExtensionIsUnpacked));
    }

    private async void BreakLock_Click(object sender, RoutedEventArgs e)
    {
        if (_status is null) return;
        var delay = Describe((int)(_status.BreakDelayMs / 60_000));
        if (!Ask($"Break the lock early?\n\nIt stays fully on for another {delay}, then ends. You can cancel any time before that."))
            return;
        await RunAsync(ServiceClient.BreakLockAsync);
    }

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
        if (on && _status.Lock.IsLocked && !Ask("Turn on education-only mode?\n\nYou can't turn it off again until the lock ends."))
        {
            EducationOnlyBox.IsChecked = false;
            return;
        }
        var rules = _status.Lock.Rules;
        await RunAsync(() => ServiceClient.UpdateRulesAsync(rules with { EducationOnly = on }));
    }

    private async void Declutter_Click(object sender, RoutedEventArgs e)
    {
        if (_status is null) return;
        var box = (CheckBox)sender;
        var option = (DeclutterOption)box.Tag;
        var hide = box.IsChecked == true;
        if (hide && _status.Lock.IsLocked && !Ask($"Hide {box.Content} on YouTube?\n\nYou can't show it again until the lock ends."))
        {
            box.IsChecked = false;
            return;
        }
        var rules = _status.Lock.Rules;
        var declutter = rules.Declutter.Where(o => o != option).ToList();
        if (hide) declutter.Add(option);
        await RunAsync(() => ServiceClient.UpdateRulesAsync(rules with { Declutter = declutter }));
    }

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
