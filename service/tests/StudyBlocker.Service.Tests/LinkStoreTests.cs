using System.Text.Json;
using StudyBlocker.Common;

namespace StudyBlocker.Service.Tests;

// The pairing: a one-time offer left on disk, a secret handed out once, and
// the checks every later link request has to pass.
public sealed class LinkStoreTests : IDisposable
{
    private const string Me = "S-1-5-21-1-2-3-1001";
    private const string SomeoneElse = "S-1-5-21-1-2-3-1002";

    private readonly InMemoryStateStore _disk = new();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ssb-link-tests-" + Guid.NewGuid().ToString("N"));

    public LinkStoreTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string OfferPath => Path.Combine(_folder, "pairing-offer.json");

    // The service starting: a new store over the same disk.
    private LinkStore NewStore()
    {
        var store = new LinkStore(_disk, OfferPath);
        store.EnsureOffer();
        return store;
    }

    private string ReadOffer()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(OfferPath));
        return document.RootElement.GetProperty("Offer").GetString()!;
    }

    private static void AssertRefused(string code, Action action) =>
        Assert.Equal(code, Assert.Throws<InvalidOperationException>(action).Message);

    [Fact]
    public void An_unlinked_service_leaves_an_offer_for_LockIn()
    {
        var store = NewStore();

        Assert.True(store.OfferAvailable);
        Assert.False(store.IsLinked);
        Assert.Equal(43, ReadOffer().Length);
    }

    [Fact]
    public void Only_a_hash_of_the_offer_is_kept_by_the_service()
    {
        NewStore();
        var token = ReadOffer();

        var kept = _disk.Read<LinkStore.OfferRecord>("link-offer.json")!;

        Assert.NotEmpty(kept.Hash);
        Assert.DoesNotContain(token, kept.Hash);
    }

    [Fact]
    public void The_offer_stays_the_same_until_it_is_used()
    {
        var store = NewStore();
        var first = ReadOffer();

        store.EnsureOffer();
        var restarted = NewStore();

        Assert.Equal(first, ReadOffer());
        Assert.True(restarted.OfferAvailable);
    }

    [Fact]
    public void A_deleted_or_garbled_offer_file_is_replaced()
    {
        var store = NewStore();
        var first = ReadOffer();

        File.Delete(OfferPath);
        Assert.False(store.OfferAvailable);
        store.EnsureOffer();
        var second = ReadOffer();
        Assert.NotEqual(first, second);
        Assert.True(store.OfferAvailable);

        File.WriteAllText(OfferPath, "{ not json");
        store.EnsureOffer();
        Assert.NotEqual(second, ReadOffer());
    }

    [Fact]
    public void With_nowhere_to_put_an_offer_there_is_none_and_nothing_breaks()
    {
        var missingFolder = new LinkStore(_disk, Path.Combine(_folder, "not-installed", "pairing-offer.json"));
        missingFolder.EnsureOffer();
        Assert.False(missingFolder.OfferAvailable);
        Assert.False(Directory.Exists(Path.Combine(_folder, "not-installed")));

        var noPath = new LinkStore(new InMemoryStateStore(), null);
        noPath.EnsureOffer();
        Assert.False(noPath.OfferAvailable);
    }

    [Fact]
    public void Pairing_hands_out_the_secret_once_and_uses_up_the_offer()
    {
        var store = NewStore();
        var offer = ReadOffer();

        var secret = store.Pair(offer, Me);

        Assert.Equal(43, secret.Length);
        Assert.NotEqual(offer, secret);
        Assert.True(store.IsLinked);
        Assert.False(store.OfferAvailable);
        Assert.False(File.Exists(OfferPath));
        store.Require(secret, Me);
    }

    [Fact]
    public void The_secret_is_never_stored_as_it_is()
    {
        var store = NewStore();
        var secret = store.Pair(ReadOffer(), Me);

        var kept = _disk.Read<LinkStore.LinkRecord>("link.json")!;

        Assert.True(kept.Linked);
        Assert.NotEmpty(kept.SecretHash);
        Assert.DoesNotContain(secret, kept.SecretHash);
        Assert.Equal(Me, kept.UserSid);
    }

    [Fact]
    public void A_wrong_or_missing_offer_pairs_nothing()
    {
        var store = NewStore();

        AssertRefused(LinkProtocol.BadOffer, () => store.Pair("not-the-offer", Me));
        AssertRefused(LinkProtocol.BadOffer, () => store.Pair("", Me));
        AssertRefused(LinkProtocol.BadOffer, () => store.Pair(null, Me));
        AssertRefused(LinkProtocol.WrongUser, () => store.Pair(ReadOffer(), null));

        Assert.False(store.IsLinked);
        Assert.True(store.OfferAvailable);
    }

    [Fact]
    public void Once_linked_nobody_else_can_pair_even_with_a_copy_of_the_old_offer()
    {
        var store = NewStore();
        var offer = ReadOffer();
        store.Pair(offer, Me);

        AssertRefused(LinkProtocol.AlreadyLinked, () => store.Pair(offer, SomeoneElse));
    }

    [Fact]
    public void Every_later_request_needs_the_secret_from_the_account_that_paired()
    {
        var store = NewStore();
        AssertRefused(LinkProtocol.NotLinked, () => store.Require("anything", Me));

        var secret = store.Pair(ReadOffer(), Me);

        AssertRefused(LinkProtocol.BadSecret, () => store.Require("wrong", Me));
        AssertRefused(LinkProtocol.BadSecret, () => store.Require("", Me));
        AssertRefused(LinkProtocol.BadSecret, () => store.Require(null, Me));
        AssertRefused(LinkProtocol.WrongUser, () => store.Require(secret, SomeoneElse));
        AssertRefused(LinkProtocol.WrongUser, () => store.Require(secret, null));
        store.Require(secret, Me);
    }

    [Fact]
    public void The_link_survives_a_service_restart()
    {
        var secret = NewStore().Pair(ReadOffer(), Me);

        var afterRestart = NewStore();

        Assert.True(afterRestart.IsLinked);
        Assert.False(afterRestart.OfferAvailable);
        afterRestart.Require(secret, Me);
    }

    [Fact]
    public void Unlinking_forgets_the_secret_and_leaves_a_fresh_offer()
    {
        var store = NewStore();
        var firstOffer = ReadOffer();
        var oldSecret = store.Pair(firstOffer, Me);

        store.Unpair();

        Assert.False(store.IsLinked);
        AssertRefused(LinkProtocol.NotLinked, () => store.Require(oldSecret, Me));
        Assert.True(store.OfferAvailable);
        var secondOffer = ReadOffer();
        Assert.NotEqual(firstOffer, secondOffer);

        // The old offer opens nothing, the new one links again, with a new secret.
        AssertRefused(LinkProtocol.BadOffer, () => store.Pair(firstOffer, Me));
        var newSecret = store.Pair(secondOffer, Me);
        Assert.NotEqual(oldSecret, newSecret);
        AssertRefused(LinkProtocol.BadSecret, () => store.Require(oldSecret, Me));
        store.Require(newSecret, Me);
    }
}
