using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Jawbreakers;

/// <summary>What the update did, shown on the Guild members page.</summary>
public record ArmoryResult(int Checked, List<string> Changes, List<string> NoPage, List<string> LeftGuild, List<string> Failed);

/// <summary>
/// Updates the characters from their public Armory pages: level, class, race and profession skill levels.
/// Each character page carries its data as JSON in a script tag ("var characterProfileInitialState = {...}").
/// It only reads pages of characters already on the site: the Armory has no public list of guild members to read,
/// so new members are added on the admin page (the name is enough) and filled in by the next update.
/// </summary>
public class ArmorySync(IConfiguration config, ILogger<ArmorySync> log)
{
    public const string CharacterPrefix = "https://worldofwarcraft.blizzard.com/en-gb/forever/eu/armory/character/";
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(60);
    const string Marker = "var characterProfileInitialState = ";

    static readonly HttpClient Http = CreateClient();
    static readonly SemaphoreSlim OneAtATime = new(1, 1);

    static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; JawbreakersGuildSite/1.0)");
        return http;
    }

    record Page(string Url, int Level, string Class, string Race, string GuildSlug, Dictionary<string, int>? Professions);
    enum Outcome { Ok, NoPage, Failed }

    /// <summary>When the last update started (UTC), or null if there has been none.</summary>
    public static async Task<DateTime?> LastStarted(GuildDb db)
    {
        var row = await db.Texts.FindAsync("armory-update");
        return row is not null && DateTime.TryParse(row.Value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t : null;
    }

    public static int SecondsLeft(DateTime? lastStarted) =>
        lastStarted is null ? 0 : Math.Max(0, (int)Math.Ceiling((lastStarted.Value + Cooldown - DateTime.UtcNow).TotalSeconds));

    static async Task SetText(GuildDb db, string key, string value)
    {
        var row = await db.Texts.FindAsync(key);
        if (row is null) db.Texts.Add(new SiteText { Key = key, Value = value });
        else row.Value = value;
        await db.SaveChangesAsync();
    }

    /// <summary>Runs the update, or returns null when the last one started less than an hour ago (or one is running).</summary>
    public async Task<ArmoryResult?> Run(GuildDb db)
    {
        if (!await OneAtATime.WaitAsync(0)) return null;
        try
        {
            if (SecondsLeft(await LastStarted(db)) > 0) return null;
            await SetText(db, "armory-update", DateTime.UtcNow.ToString("O"));

            var classes = config.GetSection("Guild:Classes").Get<string[]>() ?? [];
            var races = config.GetSection("Guild:Races").Get<string[]>() ?? [];
            var professions = (config.GetSection("Guild:Professions").Get<ProfessionInfo[]>() ?? []).Select(p => p.Name).ToHashSet();
            var guildSlug = config["Armory:GuildSlug"] ?? "jawbreakers";

            var characters = await db.Characters.OrderBy(c => c.Name).ToListAsync();

            // Read the pages first (four at a time), then change the database in one go.
            var pages = new (Outcome outcome, Page? page)[characters.Count];
            await Parallel.ForEachAsync(Enumerable.Range(0, characters.Count), new ParallelOptions { MaxDegreeOfParallelism = 4 },
                async (i, _) => pages[i] = await Read(characters[i]));

            var result = new ArmoryResult(characters.Count, [], [], [], []);
            for (var i = 0; i < characters.Count; i++)
            {
                var c = characters[i];
                var (outcome, page) = pages[i];
                if (outcome == Outcome.NoPage) { result.NoPage.Add(c.Name); continue; }
                if (outcome == Outcome.Failed || page is null) { result.Failed.Add(c.Name); continue; }

                var changes = new List<string>();
                if (c.ArmoryUrl != page.Url) { c.ArmoryUrl = page.Url; changes.Add("now has an Armory page"); }
                if (page.Level is >= 1 and <= 100 && page.Level != c.Level) { changes.Add($"level {c.Level} → {page.Level}"); c.Level = page.Level; }
                if (classes.Contains(page.Class) && page.Class != c.Class) { changes.Add($"class {c.Class} → {page.Class}"); c.Class = page.Class; }
                if (races.Contains(page.Race) && page.Race != c.Race) { changes.Add(c.Race.Length > 0 ? $"race {c.Race} → {page.Race}" : $"race {page.Race}"); c.Race = page.Race; }

                if (page.Professions is not null)
                {
                    var wanted = page.Professions.Where(p => professions.Contains(p.Key) && p.Value is >= 1 and <= 300).ToDictionary(p => p.Key, p => p.Value);
                    var current = await db.CharacterProfessions.Where(p => p.CharacterId == c.Id).ToListAsync();
                    foreach (var old in current.Where(p => !wanted.ContainsKey(p.Profession)))
                    {
                        db.CharacterProfessions.Remove(old);
                        changes.Add($"dropped {old.Profession}");
                    }
                    foreach (var (name, skill) in wanted)
                    {
                        var row = current.FirstOrDefault(p => p.Profession == name);
                        if (row is null)
                        {
                            db.CharacterProfessions.Add(new CharacterProfession { CharacterId = c.Id, Profession = name, Skill = skill });
                            changes.Add($"{name} {skill}");
                        }
                        else if (row.Skill != skill)
                        {
                            changes.Add($"{name} {row.Skill} → {skill}");
                            row.Skill = skill;
                        }
                    }
                }

                if (changes.Count > 0) result.Changes.Add($"{c.Name}: {string.Join(", ", changes)}");
                if (!string.Equals(page.GuildSlug, guildSlug, StringComparison.OrdinalIgnoreCase)) result.LeftGuild.Add(c.Name);
            }
            await db.SaveChangesAsync();

            // Nothing could be read at all (the Armory is down?): don't make people wait an hour to try again.
            if (result.Failed.Count > 0 && result.Failed.Count + result.NoPage.Count == characters.Count)
                await SetText(db, "armory-update", "");
            await SetText(db, "armory-result", JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            return result;
        }
        finally { OneAtATime.Release(); }
    }

    public static async Task<ArmoryResult?> LastResult(GuildDb db)
    {
        var row = await db.Texts.FindAsync("armory-result");
        try { return row is null ? null : JsonSerializer.Deserialize<ArmoryResult>(row.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (JsonException) { return null; }
    }

    async Task<(Outcome, Page?)> Read(Character c)
    {
        // Only ever fetches Armory character pages, whatever link the admin has typed in.
        // A character without a link (below level 10 when it was added) gets the address its name gives.
        var url = c.ArmoryUrl.StartsWith(CharacterPrefix, StringComparison.OrdinalIgnoreCase)
            ? c.ArmoryUrl
            : CharacterPrefix + Uri.EscapeDataString(c.Name.Trim().ToLowerInvariant().Replace(' ', '-')).ToLowerInvariant();
        try
        {
            using var response = await Http.GetAsync(url);
            // The Armory answers 500 (or 404) for a character that has no page: below level 10, or gone.
            if ((int)response.StatusCode is 404 or 500) return (Outcome.NoPage, null);
            if (!response.IsSuccessStatusCode) return (Outcome.Failed, null);
            var html = await response.Content.ReadAsStringAsync();
            var start = html.IndexOf(Marker, StringComparison.Ordinal);
            if (start < 0) return (Outcome.Failed, null);

            using var doc = ParseFirstValue(html[(start + Marker.Length)..]);
            if (!doc.RootElement.TryGetProperty("character", out var ch)) return (Outcome.Failed, null);

            Dictionary<string, int>? skills = null;
            if (ch.TryGetProperty("professions", out var prof) && prof.ValueKind == JsonValueKind.Object)
            {
                skills = [];
                foreach (var group in new[] { "primaries", "secondaries" })
                {
                    if (!prof.TryGetProperty(group, out var list) || list.ValueKind != JsonValueKind.Array) continue;
                    foreach (var p in list.EnumerateArray())
                    {
                        var name = Name(p);
                        if (name.Length == 0 || !p.TryGetProperty("tiers", out var tiers) || tiers.ValueKind != JsonValueKind.Array) continue;
                        var best = tiers.EnumerateArray()
                            .Select(t => t.TryGetProperty("skillPoints", out var s) && s.TryGetInt32(out var n) ? n : 0).DefaultIfEmpty(0).Max();
                        if (best > 0) skills[name] = best;
                    }
                }
            }

            return (Outcome.Ok, new Page(url,
                ch.TryGetProperty("level", out var level) && level.TryGetInt32(out var l) ? l : 0,
                Name(ch, "class"), Name(ch, "race"),
                ch.TryGetProperty("guild", out var guild) && guild.ValueKind == JsonValueKind.Object && guild.TryGetProperty("slug", out var slug) ? slug.GetString() ?? "" : "",
                skills));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            log.LogWarning("Armory update: could not read {Url}: {Message}", url, e.Message);
            return (Outcome.Failed, null);
        }
    }

    // Reads the one JSON value the text starts with and ignores the rest of the page.
    static JsonDocument ParseFirstValue(string text)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(text));
        return JsonDocument.ParseValue(ref reader);
    }

    // The "name" of an element, or of one of its properties: {"class": {"name": "Priest"}}
    static string Name(JsonElement e, string? property = null)
    {
        if (property is not null && (!e.TryGetProperty(property, out e) || e.ValueKind != JsonValueKind.Object)) return "";
        return e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
    }
}
