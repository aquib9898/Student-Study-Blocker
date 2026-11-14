namespace StudyBlocker.Service.Policies;

// What "Block adult content" adds to the browsers' URLBlocklist. Chromium's
// blocklist matches hosts, never words in a URL, so this is a list of the
// largest adult sites (each entry covers its subdomains too). The long tail
// is reached mostly through search, which BrowserPolicies closes by forcing
// safe search on Google and Bing.
public static class AdultSites
{
    public static readonly IReadOnlyList<string> Domains =
    [
        "pornhub.com", "xvideos.com", "xvideos2.com", "xnxx.com", "xhamster.com", "xhamster.desi", "xhamster2.com",
        "redtube.com", "youporn.com", "tube8.com", "spankbang.com", "eporner.com", "txxx.com", "hclips.com",
        "porn.com", "porntrex.com", "pornone.com", "tnaflix.com", "empflix.com", "drtuber.com", "sunporno.com",
        "youjizz.com", "beeg.com", "motherless.com", "hqporner.com", "porndig.com", "fuq.com", "4tube.com",
        "porntube.com", "pornerbros.com", "thumbzilla.com", "keezmovies.com", "extremetube.com", "slutload.com",
        "nuvid.com", "gotporn.com", "ixxx.com", "anysex.com", "vjav.com", "javhd.com", "jable.tv", "missav.com",
        "hanime.tv", "nhentai.net", "rule34.xxx", "e-hentai.org", "hentaihaven.xxx", "fapello.com",
        "onlyfans.com", "fansly.com", "manyvids.com", "clips4sale.com", "brazzers.com", "realitykings.com",
        "bangbros.com", "naughtyamerica.com", "mofos.com", "digitalplayground.com", "vixen.com", "blacked.com",
        "tushy.com", "evilangel.com", "kink.com", "chaturbate.com", "stripchat.com", "bongacams.com",
        "cam4.com", "camsoda.com", "livejasmin.com", "myfreecams.com", "flirt4free.com", "imlive.com",
        "xcams.com", "jerkmate.com", "adultfriendfinder.com", "erome.com", "sxyprn.com", "porngo.com",
        "pornhat.com", "xxxbunker.com", "redgifs.com", "scrolller.com", "literotica.com", "f95zone.to",
    ];
}
