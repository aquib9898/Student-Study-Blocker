namespace StudyBlocker.App;

// Turns whatever was typed or pasted into the form the browser blocklist
// policy expects: "reddit.com" blocks the site and all its subdomains, and
// "youtube.com/shorts" blocks just that part of a site.
public static class SiteName
{
    // Null if it doesn't look like a site. A pasted address ("https://...")
    // keeps only its site, since whoever pastes a page's address almost
    // always means the whole site; a typed path is kept, since typing one
    // is deliberate.
    public static string? Normalize(string text)
    {
        var site = text.Trim().ToLowerInvariant();
        var pasted = false;
        foreach (var scheme in new[] { "https://", "http://" })
        {
            if (site.StartsWith(scheme, StringComparison.Ordinal))
            {
                site = site[scheme.Length..];
                pasted = true;
            }
        }

        site = site.Split('?', '#')[0].TrimEnd('/');
        if (pasted) site = site.Split('/')[0];
        if (site.StartsWith("www.", StringComparison.Ordinal)) site = site[4..];

        var host = site.Split('/')[0];
        return host.Contains('.') && !host.StartsWith('.') && !host.EndsWith('.') && !site.Any(char.IsWhiteSpace)
            ? site
            : null;
    }
}
