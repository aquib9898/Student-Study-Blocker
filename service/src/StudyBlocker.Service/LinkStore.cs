using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StudyBlocker.Common;

namespace StudyBlocker.Service;

// The pairing between this service and one LockIn.
//
// 1. While nothing is linked, the service leaves a one-time pairing offer: a
//    random token in `pairing-offer.json`, in a folder every user can read
//    but only administrators and the service can write. Only the token's hash
//    is kept in the protected store.
// 2. LockIn sends the token in a `pair` request. The service checks it,
//    deletes the offer, remembers which Windows account asked, and answers
//    with a long-lived secret, once. Only the secret's hash is kept.
// 3. Every later link request carries that secret and must come from the same
//    Windows account. See LinkService for what each one may do.
//
// The secret lives wherever LockIn keeps it, which is inside the daily
// account's profile, so this is a speed bump against other programs and
// impulses and not a wall against someone who is determined and technical.
// That is the intent: no administrator rights are needed to use the link.
public sealed class LinkStore
{
    private const string LinkFile = "link.json";
    private const string OfferStateFile = "link-offer.json";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public sealed record LinkRecord
    {
        public bool Linked { get; init; }
        public string SecretHash { get; init; } = "";
        public string UserSid { get; init; } = "";
    }

    public sealed record OfferRecord
    {
        public string Hash { get; init; } = "";
    }

    private readonly IStateStore _store;
    private readonly string? _offerPath;
    private readonly object _gate = new();
    private LinkRecord _link;
    private string _offerHash;

    // offerPath: where the offer file goes; null for nowhere. The folder must
    // already exist (the installer makes it), so a copy run from the repo,
    // which isn't installed anywhere, simply has nothing to offer.
    public LinkStore(IStateStore store, string? offerPath)
    {
        _store = store;
        _offerPath = offerPath;
        _link = store.Read<LinkRecord>(LinkFile) ?? new LinkRecord();
        _offerHash = store.Read<OfferRecord>(OfferStateFile)?.Hash ?? "";
    }

    public bool IsLinked
    {
        get { lock (_gate) return _link.Linked; }
    }

    // An offer is waiting for LockIn: unlinked, and the file is really there.
    public bool OfferAvailable
    {
        get
        {
            lock (_gate)
                return !_link.Linked && _offerHash.Length > 0 && _offerPath is not null && File.Exists(_offerPath);
        }
    }

    // Leaves an offer for LockIn if nothing is linked and none is waiting. Cheap
    // enough to run every tick, which also repairs an offer deleted by hand.
    public void EnsureOffer()
    {
        lock (_gate) EnsureOfferCore();
    }

    // Returns the secret, once. Called with the Windows account that asked.
    public string Pair(string? offer, string? userSid)
    {
        lock (_gate)
        {
            if (_link.Linked) throw new InvalidOperationException(LinkProtocol.AlreadyLinked);
            if (string.IsNullOrEmpty(offer) || _offerHash.Length == 0 || !Matches(offer, _offerHash))
                throw new InvalidOperationException(LinkProtocol.BadOffer);
            if (string.IsNullOrEmpty(userSid)) throw new InvalidOperationException(LinkProtocol.WrongUser);

            var secret = NewToken();
            _link = new LinkRecord { Linked = true, SecretHash = Hash(secret), UserSid = userSid };
            _store.Write(LinkFile, _link);

            // The offer is spent whether or not its file can be deleted.
            _offerHash = "";
            _store.Write(OfferStateFile, new OfferRecord());
            TryDeleteOfferFile();
            return secret;
        }
    }

    // Throws unless `secret` is the linked one and `userSid` the account that
    // paired. Both checks always run in the same order, so which one failed
    // tells a caller nothing it didn't already have.
    public void Require(string? secret, string? userSid)
    {
        lock (_gate)
        {
            if (!_link.Linked) throw new InvalidOperationException(LinkProtocol.NotLinked);
            if (string.IsNullOrEmpty(secret) || !Matches(secret, _link.SecretHash))
                throw new InvalidOperationException(LinkProtocol.BadSecret);
            if (!string.Equals(userSid, _link.UserSid, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(LinkProtocol.WrongUser);
        }
    }

    // Forgets the link and leaves a fresh offer. The caller has already
    // checked the secret (or is the installer's reset).
    public void Unpair()
    {
        lock (_gate)
        {
            _link = new LinkRecord();
            _store.Write(LinkFile, _link);
            EnsureOfferCore();
        }
    }

    private void EnsureOfferCore()
    {
        if (_link.Linked || _offerPath is null) return;
        var folder = Path.GetDirectoryName(_offerPath);
        if (folder is null || !Directory.Exists(folder)) return;
        if (_offerHash.Length > 0 && OfferFileHolds(_offerHash)) return;

        var token = NewToken();
        var tmpPath = _offerPath + ".tmp";
        File.WriteAllText(tmpPath, JsonSerializer.Serialize(new { Offer = token }), Utf8NoBom);
        File.Move(tmpPath, _offerPath, overwrite: true);
        _offerHash = Hash(token);
        _store.Write(OfferStateFile, new OfferRecord { Hash = _offerHash });
    }

    // Whether the offer file on disk is the offer whose hash we kept.
    private bool OfferFileHolds(string hash)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(_offerPath!, Utf8NoBom));
            return document.RootElement.TryGetProperty("Offer", out var token)
                && token.GetString() is { Length: > 0 } text
                && Matches(text, hash);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private void TryDeleteOfferFile()
    {
        try
        {
            if (_offerPath is not null) File.Delete(_offerPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The hash is already gone, so the leftover token opens nothing.
            // EnsureOffer replaces the file with a new offer on its next tick.
        }
    }

    // 32 random bytes as URL-safe base64 without padding.
    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Hash(string token) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static bool Matches(string candidate, string storedHash)
    {
        byte[] stored;
        try
        {
            stored = Convert.FromBase64String(storedHash);
        }
        catch (FormatException)
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(candidate)), stored);
    }
}
