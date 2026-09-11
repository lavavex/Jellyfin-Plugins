using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.MangaBaka;

/// <summary>Title/text helpers shared by the API client and the local database.</summary>
public static class MangaBakaText
{
    /// <summary>
    /// Folder-name decoration that is not part of the series title: a trailing
    /// "(2019)" or "[Complete]", and " - Vol. 3" style volume suffixes.
    /// </summary>
    private static readonly Regex Noise = new(
        @"\s*[\(\[][^\)\]]*[\)\]]|\s*[-–]\s*(Complete|Vol(ume)?\.?\s*\d+).*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);


    /// <summary>Strips folder-name decoration without otherwise altering the text.</summary>
    /// <param name="value">Raw title.</param>
    /// <returns>The title with decoration removed.</returns>
    public static string StripNoise(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : Noise.Replace(value, " ").Trim();

    /// <summary>
    /// Normalises a title for comparison and for the database index: decoration
    /// removed, lowercased, and reduced to space-separated alphanumeric words.
    /// </summary>
    /// <param name="value">Raw title.</param>
    /// <returns>Lowercase alphanumeric words, or empty.</returns>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        // Letters and digits of any script, not just ASCII: stripping to [a-z0-9]
        // collapses a Japanese or Cyrillic title to nothing, which silently makes it
        // unmatchable and unindexable.
        var source = Noise.Replace(value, " ");
        var builder = new StringBuilder(source.Length);
        var pendingSpace = false;
        foreach (var c in source)
        {
            if (char.IsLetterOrDigit(c))
            {
                if (pendingSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                pendingSpace = false;
                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                pendingSpace = true;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Similarity of two normalised titles as a 0-100 score, using token overlap
    /// weighted toward the query. Cheap, and good enough to separate a real match
    /// from MangaBaka's looser search results.
    /// </summary>
    /// <param name="a">First normalised title.</param>
    /// <param name="b">Second normalised title.</param>
    /// <returns>A score from 0 to 100.</returns>
    public static int Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0)
        {
            return 0;
        }

        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return 100;
        }

        var at = a.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var bt = new HashSet<string>(b.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
        if (at.Length == 0 || bt.Count == 0)
        {
            return 0;
        }

        var hit = at.Count(bt.Contains);
        if (hit == 0)
        {
            return 0;
        }

        var recall = (double)hit / at.Length;          // how much of the query was found
        var precision = (double)hit / bt.Count;        // how much of the candidate was used

        // Favour recall: a candidate carrying extra subtitle words is still fine.
        return (int)Math.Round(((recall * 0.75) + (precision * 0.25)) * 100);
    }

    /// <summary>
    /// FNV-1a over a normalised title. The database index stores these rather than
    /// the strings themselves, which keeps a two-million-alias index at 20 bytes an
    /// entry; a collision only costs a wasted record read, because the caller
    /// re-checks the titles it gets back.
    /// </summary>
    /// <param name="normalized">A title already passed through <see cref="Normalize"/>.</param>
    /// <returns>The 64-bit hash.</returns>
    public static ulong Hash(string normalized)
    {
        var hash = 14695981039346656037UL;
        foreach (var c in normalized)
        {
            hash = (hash ^ c) * 1099511628211UL;
        }

        return hash;
    }

    /// <summary>Turns a slug such as "slice_of_life" into "Slice Of Life".</summary>
    /// <param name="slug">The slug.</param>
    /// <returns>The prettified name.</returns>
    public static string Prettify(string slug)
    {
        var parts = slug.Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }
}

/// <summary>Which of a series' titles to write into the library.</summary>
public static class TitleStyles
{
    /// <summary>The English/licensed title, e.g. "Mushoku Tensei: Jobless Reincarnation".</summary>
    public const string English = "english";

    /// <summary>The romanised native title, e.g. "Mushoku Tensei: Isekai Ittara Honki Dasu".</summary>
    public const string Romanized = "romanized";

    /// <summary>The native-script title, e.g. "無職転生　～異世界行ったら本気だす～".</summary>
    public const string Native = "native";
}

/// <summary>A MangaBaka series.</summary>
public sealed class MangaBakaSeries
{
    /// <summary>Gets or sets the MangaBaka id.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the record state: active, merged or deleted.</summary>
    public string? State { get; set; }

    /// <summary>Gets or sets the id this record was merged into, when state is merged.</summary>
    public int? MergedWith { get; set; }

    /// <summary>Gets or sets the English/licensed title.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets the native-script title.</summary>
    public string? NativeTitle { get; set; }

    /// <summary>Gets or sets the romanised native title.</summary>
    public string? RomanizedTitle { get; set; }

    /// <summary>Gets every other known title, used for matching a folder name.</summary>
    public List<string> AltTitles { get; } = new();

    /// <summary>Gets or sets the synopsis.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the series type (novel / manga / manhwa ...).</summary>
    public string? Type { get; set; }

    /// <summary>Gets or sets the publication status.</summary>
    public string? Status { get; set; }

    /// <summary>Gets or sets the content rating (safe / suggestive / erotica ...).</summary>
    public string? ContentRating { get; set; }

    /// <summary>Gets or sets the first publication year.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets the community rating, 0-100.</summary>
    public double? Rating { get; set; }

    /// <summary>Gets or sets the popularity score, used to break ties between matches.</summary>
    public double? Popularity { get; set; }

    /// <summary>Gets the genres, in MangaBaka's order.</summary>
    public List<string> Genres { get; } = new();

    /// <summary>Gets the tags, ordered general to specific, spoilers already removed.</summary>
    public List<string> Tags { get; } = new();

    /// <summary>Gets the authors.</summary>
    public List<string> Authors { get; } = new();

    /// <summary>Gets the artists.</summary>
    public List<string> Artists { get; } = new();

    /// <summary>Gets or sets the cover image URL.</summary>
    public string? CoverUrl { get; set; }

    /// <summary>Gets or sets how closely this result matched the query, 0-100.</summary>
    public int MatchScore { get; set; }

    /// <summary>Gets a value indicating whether this record points at another series.</summary>
    public bool IsMerged =>
        string.Equals(State, "merged", StringComparison.OrdinalIgnoreCase) && MergedWith is > 0;

    /// <summary>Gets a value indicating whether this record has been withdrawn.</summary>
    public bool IsDeleted => string.Equals(State, "deleted", StringComparison.OrdinalIgnoreCase);

    /// <summary>Gets every title this series is known by, best first.</summary>
    public IEnumerable<string> AllTitles
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Title))
            {
                yield return Title!;
            }

            if (!string.IsNullOrWhiteSpace(RomanizedTitle))
            {
                yield return RomanizedTitle!;
            }

            if (!string.IsNullOrWhiteSpace(NativeTitle))
            {
                yield return NativeTitle!;
            }

            foreach (var t in AltTitles)
            {
                yield return t;
            }
        }
    }

    /// <summary>
    /// The title to write into the library, honouring the configured preference and
    /// falling back through the others when the preferred one is missing.
    /// </summary>
    /// <param name="style">One of the <see cref="TitleStyles"/> values.</param>
    /// <returns>The chosen title, or null when the series has none.</returns>
    public string? DisplayTitle(string? style)
    {
        string?[] order = style switch
        {
            TitleStyles.Native => new[] { NativeTitle, Title, RomanizedTitle },
            TitleStyles.Romanized => new[] { RomanizedTitle, Title, NativeTitle },
            _ => new[] { Title, RomanizedTitle, NativeTitle },
        };

        return order.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? AltTitles.FirstOrDefault();
    }

    /// <summary>
    /// Reads a series out of a MangaBaka JSON object. Handles the v1 API shape, the
    /// v2 API shape, and the trimmed records this plugin writes into its local
    /// database — they differ in enough small ways that one reader is worth having.
    /// </summary>
    /// <param name="e">The series object.</param>
    /// <returns>The parsed series.</returns>
    public static MangaBakaSeries Parse(JsonElement e)
    {
        var slim = e.TryGetProperty("_slim", out _);
        var s = new MangaBakaSeries
        {
            State = Str(e, "state"),
            Title = Str(e, "title"),
            NativeTitle = Str(e, "native_title"),
            RomanizedTitle = Str(e, "romanized_title"),
            Description = Str(e, "description"),
            Type = Str(e, "type"),
            Status = Str(e, "status"),
            ContentRating = Str(e, "content_rating"),
            Year = ReadYear(e),
        };

        s.Id = Int(e, "id") ?? 0;
        s.MergedWith = Int(e, "merged_with");

        if (e.TryGetProperty("rating", out var r) && r.ValueKind == JsonValueKind.Number)
        {
            s.Rating = r.GetDouble();
        }

        if (e.TryGetProperty("popularity", out var p) && p.ValueKind == JsonValueKind.Number)
        {
            s.Popularity = p.GetDouble();
        }

        s.CoverUrl = slim ? Str(e, "cover_url") : ReadCoverUrl(e);

        if (slim)
        {
            AddStrings(e, "alt_titles", s.AltTitles);
            AddStrings(e, "genres", s.Genres);
            AddStrings(e, "tags", s.Tags);
        }
        else
        {
            ReadApiTitles(e, s);
            ReadApiTags(e, s);
        }

        AddStrings(e, "authors", s.Authors);
        AddStrings(e, "artists", s.Artists);
        return s;
    }

    /// <summary>
    /// Writes the subset of this series the plugin actually uses, in a shape
    /// <see cref="Parse"/> reads back. The published dump averages about 18 KB a
    /// record — almost all of it cover variants, relationships and raw upstream
    /// payloads — so trimming is what makes a local copy practical at all.
    /// </summary>
    /// <param name="writer">Destination writer.</param>
    /// <param name="maxTags">How many tags to keep.</param>
    /// <param name="maxDescription">Longest synopsis to keep, in characters.</param>
    public void WriteSlim(Utf8JsonWriter writer, int maxTags, int maxDescription)
    {
        writer.WriteStartObject();
        writer.WriteNumber("_slim", 1);
        writer.WriteNumber("id", Id);
        if (!string.Equals(State, "active", StringComparison.OrdinalIgnoreCase))
        {
            WriteIfSet(writer, "state", State);
        }

        if (MergedWith is > 0)
        {
            writer.WriteNumber("merged_with", MergedWith.Value);
        }

        WriteIfSet(writer, "title", Title);
        WriteIfSet(writer, "native_title", NativeTitle);
        WriteIfSet(writer, "romanized_title", RomanizedTitle);
        WriteIfSet(writer, "description", Truncate(Description, maxDescription));
        WriteIfSet(writer, "type", Type);
        WriteIfSet(writer, "status", Status);
        WriteIfSet(writer, "content_rating", ContentRating);
        WriteIfSet(writer, "cover_url", CoverUrl);

        if (Year is > 0)
        {
            writer.WriteNumber("year", Year.Value);
        }

        if (Rating is > 0)
        {
            writer.WriteNumber("rating", Math.Round(Rating.Value, 2));
        }

        if (Popularity is > 0)
        {
            writer.WriteNumber("popularity", Math.Round(Popularity.Value, 2));
        }

        WriteArray(writer, "alt_titles", AltTitles);
        WriteArray(writer, "genres", Genres);
        WriteArray(writer, "tags", Tags.Take(maxTags));
        WriteArray(writer, "authors", Authors);
        WriteArray(writer, "artists", Artists);
        writer.WriteEndObject();
    }

    /// <summary>
    /// Reduces the alternate titles to what the local database needs: the normalised
    /// forms, deduplicated, minus anything already covered by the three display
    /// titles, capped. Storing them normalised is not just smaller — the index is
    /// built from exactly this list, so a hash hit always has a title to confirm it
    /// against, which a capped raw list could not promise.
    /// </summary>
    /// <param name="maxAltTitles">How many alternate titles to keep.</param>
    public void PrepareForStorage(int maxAltTitles)
    {
        var display = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in new[] { Title, RomanizedTitle, NativeTitle })
        {
            var n = MangaBakaText.Normalize(t);
            if (n.Length != 0)
            {
                display.Add(n);
            }
        }

        var kept = new List<string>(maxAltTitles);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in AltTitles)
        {
            if (kept.Count == maxAltTitles)
            {
                break;
            }

            var n = MangaBakaText.Normalize(t);
            if (n.Length != 0 && !display.Contains(n) && seen.Add(n))
            {
                kept.Add(n);
            }
        }

        AltTitles.Clear();
        AltTitles.AddRange(kept);
    }

    private static string? Truncate(string? value, int max) =>
        value is not null && value.Length > max ? value[..max] : value;

    private static void WriteIfSet(Utf8JsonWriter writer, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            writer.WriteString(name, value);
        }
    }

    private static void WriteArray(Utf8JsonWriter writer, string name, IEnumerable<string> values)
    {
        var any = false;
        foreach (var v in values)
        {
            if (!any)
            {
                writer.WriteStartArray(name);
                any = true;
            }

            writer.WriteStringValue(v);
        }

        if (any)
        {
            writer.WriteEndArray();
        }
    }

    /// <summary>
    /// Pulls display titles out of the API's <c>titles[]</c> array.
    ///
    /// <c>is_primary</c> is per *language* — a series typically has eight or more
    /// entries flagged primary, one per language — so it says nothing about which
    /// title to show. Picking by language is the only thing that works: the last
    /// primary in the array is reliably a CJK title.
    /// </summary>
    private static void ReadApiTitles(JsonElement e, MangaBakaSeries s)
    {
        if (!e.TryGetProperty("titles", out var titles) || titles.ValueKind != JsonValueKind.Array)
        {
            AddSecondaryTitles(e, s);
            return;
        }

        string? english = null;
        string? englishOfficial = null;
        string? romanized = null;
        string? romanizedNative = null;
        string? native = null;

        foreach (var t in titles.EnumerateArray())
        {
            var name = Str(t, "title");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var lang = (Str(t, "language") ?? string.Empty).ToLowerInvariant();
            var official = HasTrait(t, "official");
            var isNative = HasTrait(t, "native");

            if (lang is "en" || lang.StartsWith("en-", StringComparison.Ordinal))
            {
                english ??= name;
                if (official)
                {
                    englishOfficial ??= name;
                }
            }
            else if (lang is "ja-latn" or "ko-latn" or "zh-latn")
            {
                // A series can carry several romanised entries, only one of which is
                // the romanisation of the native title; the rest are subtitles.
                if (isNative)
                {
                    romanizedNative ??= name;
                }

                romanized ??= name;
            }
            else if (isNative && lang is "ja" or "ko" or "zh")
            {
                native ??= name;
            }

            if (!s.AltTitles.Contains(name!, StringComparer.OrdinalIgnoreCase))
            {
                s.AltTitles.Add(name!);
            }
        }

        // v1 still sends the flat title fields and they are the better source;
        // only fill from titles[] where the flat field was absent (that is, on v2).
        s.Title ??= englishOfficial ?? english;
        s.RomanizedTitle ??= romanizedNative ?? romanized;
        s.NativeTitle ??= native;
        AddSecondaryTitles(e, s);
    }

    /// <summary>v1 also carries a language-keyed <c>secondary_titles</c> map.</summary>
    private static void AddSecondaryTitles(JsonElement e, MangaBakaSeries s)
    {
        if (!e.TryGetProperty("secondary_titles", out var map) || map.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var lang in map.EnumerateObject())
        {
            if (lang.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var entry in lang.Value.EnumerateArray())
            {
                var name = entry.ValueKind == JsonValueKind.String ? entry.GetString() : Str(entry, "title");
                if (!string.IsNullOrWhiteSpace(name)
                    && !s.AltTitles.Contains(name!, StringComparer.OrdinalIgnoreCase))
                {
                    s.AltTitles.Add(name!);
                }
            }
        }
    }

    private static bool HasTrait(JsonElement e, string trait)
    {
        if (!e.TryGetProperty("traits", out var traits) || traits.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var t in traits.EnumerateArray())
        {
            if (t.ValueKind == JsonValueKind.String
                && string.Equals(t.GetString(), trait, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Spec: <c>published.start_date</c>; v1 still emits the deprecated <c>year</c>.</summary>
    private static int? ReadYear(JsonElement e)
    {
        if (e.TryGetProperty("published", out var pub) && pub.ValueKind == JsonValueKind.Object)
        {
            var start = Str(pub, "start_date");
            if (start is { Length: >= 4 }
                && int.TryParse(start.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var y)
                && IsPlausibleYear(y))
            {
                return y;
            }
        }

        var year = Int(e, "year");
        return IsPlausibleYear(year) ? year : null;
    }

    /// <summary>
    /// Calibre and some EPUBs store an unset date as year 100 / 0100-12-31, which
    /// <c>DateTime.TryParse</c> happily accepts. Real light novels are not that old.
    /// </summary>
    internal static bool IsPlausibleYear(int? year) =>
        year is >= 1800 and <= 2100;

    /// <summary>v1 sends <c>cover.raw.url</c>; v2 sends <c>cover.raw</c> as the URL itself.</summary>
    private static string? ReadCoverUrl(JsonElement e)
    {
        if (!e.TryGetProperty("cover", out var cover) || cover.ValueKind != JsonValueKind.Object
            || !cover.TryGetProperty("raw", out var raw))
        {
            return null;
        }

        if (raw.ValueKind == JsonValueKind.String)
        {
            return raw.GetString();
        }

        return raw.ValueKind == JsonValueKind.Object ? Str(raw, "url") : null;
    }

    /// <summary>
    /// Genres and tags both live in the tag tree: v1 calls it <c>tags_v2</c>, v2
    /// calls it <c>tags</c>, and an entry is a genre when <c>is_genre</c> is set.
    /// Tags come back ordered arbitrarily, so they are sorted general-to-specific
    /// by <c>level</c> — that is what makes a "keep the first N" limit sensible.
    /// Spoiler tags are dropped outright; nobody wants "Character Dies" on a card.
    /// </summary>
    private static void ReadApiTags(JsonElement e, MangaBakaSeries s)
    {
        foreach (var key in new[] { "tags_v2", "tags" })
        {
            if (!e.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var tags = new List<(int Level, int Order, string Name)>();
            var objects = false;
            var order = 0;
            foreach (var v in arr.EnumerateArray())
            {
                if (v.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                objects = true;
                var name = Str(v, "name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (name!.Contains('_', StringComparison.Ordinal))
                {
                    name = MangaBakaText.Prettify(name);
                }

                if (IsTrue(v, "is_genre"))
                {
                    s.Genres.Add(name);
                    continue;
                }

                if (IsTrue(v, "is_spoiler"))
                {
                    continue;
                }

                var level = Int(v, "level") ?? 0;
                tags.Add((level, order++, name));
            }

            if (!objects)
            {
                continue;
            }

            foreach (var t in tags.OrderBy(t => t.Level).ThenBy(t => t.Order))
            {
                s.Tags.Add(t.Name);
            }

            return;
        }

        // Deprecated flat arrays, still present on v1: lowercase slugs.
        foreach (var value in ReadStrings(e, "genres"))
        {
            s.Genres.Add(MangaBakaText.Prettify(value));
        }

        foreach (var value in ReadStrings(e, "tags"))
        {
            s.Tags.Add(value);
        }
    }

    private static bool IsTrue(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static void AddStrings(JsonElement e, string prop, List<string> into)
    {
        foreach (var value in ReadStrings(e, prop))
        {
            into.Add(value);
        }
    }

    private static IEnumerable<string> ReadStrings(JsonElement e, string prop)
    {
        if (!e.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var v in arr.EnumerateArray())
        {
            if (v.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var s = v.GetString();
            if (!string.IsNullOrWhiteSpace(s))
            {
                yield return s!;
            }
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>
    /// Reads an integer field. The dump leaves absent values as JSON null rather
    /// than omitting them, and <c>TryGetInt32</c> throws on a null element, so the
    /// kind has to be checked first.
    /// </summary>
    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
            ? i
            : null;
}
