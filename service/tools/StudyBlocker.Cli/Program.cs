using System.Text.Encodings.Web;
using System.Text.Json;
using StudyBlocker.Common;

// A developer's command line for the service, next to the desktop app
// (app/): sends one request and prints the reply, for testing without the
// app or a browser.

const string Usage = """
    Usage:
      sbctl status
      sbctl rules [--block-site <domain>]... [--education-only] [--extension-off]
                  [--declutter-home-feed] [--declutter-recommended] [--declutter-end-screen]
                  [--declutter-autoplay] [--declutter-comments] [--declutter-trending]
                  [--declutter-live-chat] [--declutter-subscriptions]
                  (sets the whole rule set; no options clears it. During a
                  lock, sites and Declutter options can only be added, and
                  --education-only can't be dropped nor --extension-off
                  added)
      sbctl lock <minutes> [--dev-keep-devtools]
                  (every lock also switches off Incognito, Guest, DevTools
                  and secure DNS and blocks Shorts, until it ends.
                  --dev-keep-devtools: local testing only, before the
                  extension is published — skips the DevTools lockdown,
                  which otherwise force-disables Developer mode and takes
                  an unpacked extension down with it. Never use this once
                  the extension is actually deployed.)
      sbctl extend <minutes>
      sbctl break
                  ("Break lock early": the lock stays fully on for the
                  break delay (BreakDelayMs in the reply), then ends)
      sbctl cancel-break
                  (the app's "Cancel lock break": the lock carries on; a later break
                  starts the whole countdown over)
      sbctl channel <channel-id> allow|block|clear
      sbctl channel --name <channel name> allow|block|clear
                  (by name: matches videos by the channel name shown on
                  YouTube, ignoring case. During a lock, blocking always
                  works; allowing a new channel or unblocking one doesn't)
      sbctl channels
                  (lists channel rules; AllowedDuringCurrentLock flags the
                  plan's "Allowed during this lock" list while a lock is
                  active)
      sbctl classify --id <id> --title <title> [--channel-id <id>] [--channel <name>]
                     [--category <category>] [--description <text>] [--keyword <word>]...
                     [--length <seconds>] [--live] [--short]
                     (stands in for the extension, for testing the classifier directly)
    """;

IpcRequest request;
try
{
    request = args switch
    {
        ["status"] => new IpcRequest { Type = "get-status" },
        ["rules", .. var options] => new IpcRequest { Type = "update-rules", Payload = JsonSerializer.SerializeToElement(ParseRules(options)) },
        ["lock", var minutes, "--dev-keep-devtools"] => new IpcRequest { Type = "start-lock", Payload = LockPayload(minutes, devKeepDevTools: true) },
        ["lock", var minutes] => new IpcRequest { Type = "start-lock", Payload = LockPayload(minutes, devKeepDevTools: false) },
        ["extend", var minutes] => new IpcRequest { Type = "extend-lock", Payload = Minutes(minutes) },
        ["break"] => new IpcRequest { Type = "break-lock" },
        ["cancel-break"] => new IpcRequest { Type = "cancel-break" },
        ["channel", "--name", var name, var rule] => new IpcRequest { Type = "set-channel-rule", Payload = JsonSerializer.SerializeToElement(new ChannelRuleRequest { Name = name, Rule = rule }) },
        ["channel", var channelId, var rule] => new IpcRequest { Type = "set-channel-rule", Payload = JsonSerializer.SerializeToElement(new ChannelRuleRequest { ChannelId = channelId, Rule = rule }) },
        ["channels"] => new IpcRequest { Type = "list-channel-rules" },
        ["classify", .. var options] => new IpcRequest { Type = "classify-video", Payload = JsonSerializer.SerializeToElement(ParseMetadata(options)) },
        _ => throw new ArgumentException("Unrecognized command."),
    };
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine(Usage);
    return 2;
}

IpcResponse response;
try
{
    response = await IpcClient.SendAsync(request);
}
catch (Exception ex) when (ex is TimeoutException or IOException)
{
    Console.Error.WriteLine($"Couldn't reach the service ({ex.Message}). Is it running?");
    return 3;
}

Console.WriteLine(JsonSerializer.Serialize(response, new JsonSerializerOptions
{
    WriteIndented = true,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
}));
return response.Ok ? 0 : 1;

static RuleSet ParseRules(string[] options)
{
    var rules = new RuleSet();
    var sites = new List<string>();
    var declutter = new List<DeclutterOption>();
    for (var i = 0; i < options.Length; i++)
    {
        switch (options[i])
        {
            case "--block-site" when i + 1 < options.Length: sites.Add(options[++i]); break;
            case "--education-only": rules = rules with { EducationOnly = true }; break;
            case "--extension-off": rules = rules with { ExtensionEnabled = false }; break;
            case "--declutter-home-feed": declutter.Add(DeclutterOption.HomeFeed); break;
            case "--declutter-recommended": declutter.Add(DeclutterOption.RecommendedVideos); break;
            case "--declutter-end-screen": declutter.Add(DeclutterOption.EndScreenSuggestions); break;
            case "--declutter-autoplay": declutter.Add(DeclutterOption.Autoplay); break;
            case "--declutter-comments": declutter.Add(DeclutterOption.Comments); break;
            case "--declutter-trending": declutter.Add(DeclutterOption.TrendingExplore); break;
            case "--declutter-live-chat": declutter.Add(DeclutterOption.LiveChat); break;
            case "--declutter-subscriptions": declutter.Add(DeclutterOption.SubscriptionsFeed); break;
            default: throw new ArgumentException($"Unknown or incomplete option '{options[i]}'.");
        }
    }
    return rules with { BlockedDomains = sites, Declutter = declutter };
}

static JsonElement Minutes(string value) =>
    int.TryParse(value, out var minutes)
        ? JsonSerializer.SerializeToElement(new LockRequest { Minutes = minutes })
        : throw new ArgumentException($"'{value}' isn't a whole number of minutes.");

static JsonElement LockPayload(string value, bool devKeepDevTools) =>
    int.TryParse(value, out var minutes)
        ? JsonSerializer.SerializeToElement(new LockRequest { Minutes = minutes, DevKeepDevTools = devKeepDevTools })
        : throw new ArgumentException($"'{value}' isn't a whole number of minutes.");

static VideoMetadata ParseMetadata(string[] options)
{
    string? id = null, title = null, channel = null, channelId = null, category = null, description = null;
    long? length = null;
    var live = false;
    var isShort = false;
    var keywords = new List<string>();
    for (var i = 0; i < options.Length; i++)
    {
        switch (options[i])
        {
            case "--id" when i + 1 < options.Length: id = options[++i]; break;
            case "--title" when i + 1 < options.Length: title = options[++i]; break;
            case "--channel" when i + 1 < options.Length: channel = options[++i]; break;
            case "--channel-id" when i + 1 < options.Length: channelId = options[++i]; break;
            case "--category" when i + 1 < options.Length: category = options[++i]; break;
            case "--description" when i + 1 < options.Length: description = options[++i]; break;
            case "--keyword" when i + 1 < options.Length: keywords.Add(options[++i]); break;
            case "--length" when i + 1 < options.Length:
                length = long.TryParse(options[++i], out var secs) ? secs : throw new ArgumentException($"'{options[i]}' isn't a whole number of seconds.");
                break;
            case "--live": live = true; break;
            case "--short": isShort = true; break;
            default: throw new ArgumentException($"Unknown or incomplete option '{options[i]}'.");
        }
    }
    if (id is null || title is null) throw new ArgumentException("classify needs at least --id and --title.");
    return new VideoMetadata
    {
        Id = id,
        Title = title,
        Channel = channel,
        ChannelId = channelId,
        Category = category,
        Description = description ?? "",
        Keywords = keywords,
        LengthSeconds = length,
        IsLive = live,
        IsShort = isShort,
    };
}
