using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Jawbreakers;

/// <summary>What the update did, shown on the Guild members page.</summary>
public record ArmoryResult(int Checked, List<string> Changes, List<string> NoPage, List<string> LeftGuild, List<string> Failed)
{
    /// <summary>New guild members that were added (only with Blizzard's API).</summary>
    public List<string> Joined { get; init; } = [];
    /// <summary>Characters that were removed because they are no longer in the guild (only with Blizzard's API).</summary>
    public List<string> Removed { get; init; } = [];
    /// <summary>Set when the update could not run at all.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Updates the site from the Armory, at most once an hour.
///
/// With a Blizzard API client (BLIZZARD_CLIENT_ID and BLIZZARD_CLIENT_SECRET, from develop.battle.net) it reads the
/// official API: the guild's member list (new members are added, those who left are removed), each character's level,
/// class and race, its profession skill levels and the recipes it knows.
///
/// Without one it reads the public Armory page of each character already on the site (level, class, race, skill
/// levels) and cannot add or remove members.
/// </summary>
public class ArmorySync(IConfiguration config, IWebHostEnvironment env, ILogger<ArmorySync> log)
{
    public const string CharacterPrefix = "https://worldofwarcraft.blizzard.com/en-gb/forever/eu/armory/character/";
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(60);
    const string Marker = "var characterProfileInitialState = ";
    const string Api = "https://eu.api.blizzard.com";
    const string ProfileNamespace = "namespace=profile-forever-eu&locale=en_GB";
    const string StaticNamespace = "namespace=static-forever-eu&locale=en_GB";

    static readonly HttpClient Http = CreateClient();
    static readonly SemaphoreSlim OneAtATime = new(1, 1);
    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    string? _token;
    DateTime _tokenExpires;

    static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; JawbreakersGuildSite/1.0)");
        return http;
    }

    /// <summary>One character as the Armory has it. Professions is null when they could not be read.</summary>
    record Sheet(string Name, string Slug, int Level, string Class, string Race, string GuildSlug,
        Dictionary<string, int>? Professions, Dictionary<string, List<string>>? KnownRecipes);
    enum Outcome { Ok, NoPage, Failed }

    string ClientId => config["BLIZZARD_CLIENT_ID"] ?? "";
    string ClientSecret => config["BLIZZARD_CLIENT_SECRET"] ?? "";
    string GuildSlug => config["Armory:GuildSlug"] ?? "jawbreakers";
    public bool UsesApi => ClientId.Length > 0 && ClientSecret.Length > 0;

    /// <summary>When the last update started (UTC), or null if there has been none.</summary>
    public static async Task<DateTime?> LastStarted(GuildDb db)
    {
        var row = await db.Texts.FindAsync("armory-update");
        return row is not null && DateTime.TryParse(row.Value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t : null;
    }

    public static int SecondsLeft(DateTime? lastStarted) =>
        lastStarted is null ? 0 : Math.Max(0, (int)Math.Ceiling((lastStarted.Value + Cooldown - DateTime.UtcNow).TotalSeconds));

    public static async Task<ArmoryResult?> LastResult(GuildDb db)
    {
        var row = await db.Texts.FindAsync("armory-result");
        try { return row is null ? null : JsonSerializer.Deserialize<ArmoryResult>(row.Value, Web); }
        catch (JsonException) { return null; }
    }

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

            ArmoryResult result;
            try
            {
                result = UsesApi ? await RunWithApi(db) : await RunWithPages(db);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
            {
                log.LogWarning("Armory update failed: {Message}", e.Message);
                db.ChangeTracker.Clear();
                result = new ArmoryResult(0, [], [], [], []) { Error = "The Armory could not be read. Try again in a little while." };
            }

            // Nothing could be read at all (the Armory is down?): don't make people wait an hour to try again.
            if (result.Error is not null || (result.Failed.Count > 0 && result.Failed.Count + result.NoPage.Count == result.Checked))
                await SetText(db, "armory-update", "");
            await SetText(db, "armory-result", JsonSerializer.Serialize(result, Web));
            return result;
        }
        finally { OneAtATime.Release(); }
    }

    // ---- With Blizzard's API ----

    async Task<ArmoryResult> RunWithApi(GuildDb db)
    {
        var classNames = await IdNames("playable-class", "classes");
        var raceNames = await IdNames("playable-race", "races");

        using var rosterDoc = await GetJson($"{Api}/data/wow/guild/{Uri.EscapeDataString(GuildSlug)}/roster?{ProfileNamespace}")
            ?? throw new InvalidOperationException("The guild was not found in Blizzard's API.");
        var members = rosterDoc.RootElement.GetProperty("members").EnumerateArray().Select(m => m.GetProperty("character")).Select(ch => new Sheet(
            Name: $"{Text(ch, "name")} {Text(ch, "surname")}".Trim(),
            Slug: Text(ch, "slug"),
            Level: ch.TryGetProperty("level", out var level) && level.TryGetInt32(out var l) ? l : 0,
            Class: classNames.GetValueOrDefault(Id(ch, "playable_class"), ""),
            Race: raceNames.GetValueOrDefault(Id(ch, "playable_race"), ""),
            GuildSlug: GuildSlug, Professions: null, KnownRecipes: null))
            .Where(m => m.Name.Length > 0 && m.Slug.Length > 0).ToList();
        // An empty list would remove every character from the site: treat it as a failed read instead.
        if (members.Count == 0) throw new InvalidOperationException("Blizzard's API returned an empty guild list.");

        // Professions and known recipes, four characters at a time. Characters below level 10 have no profile.
        var sheets = new Sheet[members.Count];
        var failed = new bool[members.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, members.Count), new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (i, _) =>
        {
            sheets[i] = members[i];
            if (members[i].Level < 10) return;
            try
            {
                using var doc = await GetJson($"{Api}/profile/wow/character/{Uri.EscapeDataString(Uri.UnescapeDataString(members[i].Slug))}/professions?{ProfileNamespace}");
                if (doc is null) return;
                var skills = new Dictionary<string, int>();
                var known = new Dictionary<string, List<string>>();
                foreach (var group in new[] { "primaries", "secondaries" })
                {
                    if (!doc.RootElement.TryGetProperty(group, out var list) || list.ValueKind != JsonValueKind.Array) continue;
                    foreach (var p in list.EnumerateArray())
                    {
                        var name = Name(p, "profession");
                        if (name.Length == 0 || !p.TryGetProperty("tiers", out var tiers) || tiers.ValueKind != JsonValueKind.Array) continue;
                        var best = 0;
                        var recipes = new List<string>();
                        foreach (var tier in tiers.EnumerateArray())
                        {
                            if (tier.TryGetProperty("skill_points", out var s) && s.TryGetInt32(out var n)) best = Math.Max(best, n);
                            if (tier.TryGetProperty("known_recipes", out var k) && k.ValueKind == JsonValueKind.Array)
                                recipes.AddRange(k.EnumerateArray().Select(r => Text(r, "name")).Where(r => r.Length > 0));
                        }
                        if (best > 0) skills[name] = best;
                        known[name] = recipes;
                    }
                }
                sheets[i] = members[i] with { Professions = skills, KnownRecipes = known };
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
            {
                log.LogWarning("Armory update: could not read the professions of {Name}: {Message}", members[i].Name, e.Message);
                failed[i] = true;
            }
        });

        var classes = config.GetSection("Guild:Classes").Get<string[]>() ?? [];
        var existing = await db.Characters.ToListAsync();
        var result = new ArmoryResult(members.Count, [], [], [], []);

        // Members who have left the guild
        var inGuild = members.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in existing.Where(c => !inGuild.Contains(c.Name)).ToList())
        {
            await db.CharacterProfessions.Where(p => p.CharacterId == gone.Id).ExecuteDeleteAsync();
            await db.KnownRecipes.Where(k => k.CharacterId == gone.Id).ExecuteDeleteAsync();
            db.Characters.Remove(gone);
            existing.Remove(gone);
            result.Removed.Add(gone.Name);
        }

        // New members. A class the site doesn't know can't be shown in any list, so such a character is skipped.
        foreach (var m in sheets.Where(m => !existing.Any(c => string.Equals(c.Name, m.Name, StringComparison.OrdinalIgnoreCase))))
        {
            if (!classes.Contains(m.Class)) { result.Failed.Add(m.Name); continue; }
            var c = new Character { Name = m.Name, Class = m.Class, Level = Math.Clamp(m.Level, 1, 100) };
            db.Characters.Add(c);
            existing.Add(c);
            result.Joined.Add(m.Name);
        }
        await db.SaveChangesAsync(); // gives the new characters their ids

        var spellIds = RecipeSpellIds();
        for (var i = 0; i < sheets.Length; i++)
        {
            var m = sheets[i];
            var c = existing.FirstOrDefault(c => string.Equals(c.Name, m.Name, StringComparison.OrdinalIgnoreCase));
            if (c is null) continue;
            var changes = new List<string>();
            c.Name = m.Name; // follows the Armory's spelling
            await Apply(db, c, m, changes);

            // The Armory website only has a page for characters of level 10 and up
            var url = m.Level >= 10 ? CharacterPrefix + Uri.EscapeDataString(Uri.UnescapeDataString(m.Slug)) : "";
            if (c.ArmoryUrl.Length == 0 && url.Length > 0) changes.Add("now has an Armory page");
            c.ArmoryUrl = url;
            if (m.Level < 10) result.NoPage.Add(c.Name);
            if (failed[i]) result.Failed.Add(c.Name);

            if (m.KnownRecipes is not null)
            {
                var wanted = m.KnownRecipes
                    .Where(p => spellIds.ContainsKey(p.Key))
                    .SelectMany(p => p.Value.SelectMany(name => spellIds[p.Key].GetValueOrDefault(name, [])).Distinct().Select(id => (Profession: p.Key, SpellId: id)))
                    .ToHashSet();
                var current = await db.KnownRecipes.Where(k => k.CharacterId == c.Id).ToListAsync();
                var before = current.Count;
                db.KnownRecipes.RemoveRange(current.Where(k => !wanted.Contains((k.Profession, k.SpellId))));
                var have = current.Select(k => (k.Profession, k.SpellId)).ToHashSet();
                var added = wanted.Where(w => !have.Contains(w)).ToList();
                db.KnownRecipes.AddRange(added.Select(w => new KnownRecipe { CharacterId = c.Id, Profession = w.Profession, SpellId = w.SpellId }));
                // Not on the first import of a character's recipes: that would be a change line for everyone
                if (before > 0 && added.Count > 0) changes.Add(added.Count == 1 ? "1 new recipe" : $"{added.Count} new recipes");
            }

            if (changes.Count > 0 && !result.Joined.Contains(c.Name)) result.Changes.Add($"{c.Name}: {string.Join(", ", changes)}");
        }
        await db.SaveChangesAsync();
        await SetText(db, "armory-exact", "1"); // the Crafting page now goes by the recipes each character really knows
        return result;
    }

    async Task<string> Token()
    {
        if (_token is not null && DateTime.UtcNow < _tokenExpires) return _token;
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth.battle.net/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials" })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{ClientId}:{ClientSecret}")));
        using var response = await Http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Blizzard refused the API client ({(int)response.StatusCode}). Check BLIZZARD_CLIENT_ID and BLIZZARD_CLIENT_SECRET.");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        _token = doc.RootElement.GetProperty("access_token").GetString()!;
        _tokenExpires = DateTime.UtcNow.AddSeconds(doc.RootElement.GetProperty("expires_in").GetInt32() - 300);
        return _token;
    }

    /// <summary>GET with the API token. Null for 404 (no such character or guild).</summary>
    async Task<JsonDocument?> GetJson(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await Token());
        using var response = await Http.SendAsync(request);
        if ((int)response.StatusCode == 404) return null;
        if ((int)response.StatusCode == 401) _token = null; // a new token next time
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    // The roster gives classes and races as ids: this reads their names ("classes": [{"id": 5, "name": "Priest"}]).
    async Task<Dictionary<int, string>> IdNames(string index, string property)
    {
        using var doc = await GetJson($"{Api}/data/wow/{index}/index?{StaticNamespace}")
            ?? throw new InvalidOperationException($"Blizzard's API has no {index} list.");
        return doc.RootElement.GetProperty(property).EnumerateArray()
            .ToDictionary(e => e.GetProperty("id").GetInt32(), e => Text(e, "name"));
    }

    /// <summary>Profession name → recipe name → the spell ids used on the Crafting page (wwwroot/recipes/*.json).</summary>
    Dictionary<string, Dictionary<string, List<int>>> RecipeSpellIds()
    {
        var all = new Dictionary<string, Dictionary<string, List<int>>>();
        foreach (var p in config.GetSection("Guild:Professions").Get<ProfessionInfo[]>() ?? [])
        {
            var file = Path.Combine(env.WebRootPath, "recipes", p.Recipes + ".json");
            if (p.Recipes.Length == 0 || !File.Exists(file)) continue;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var byName = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in doc.RootElement.GetProperty("recipes").EnumerateArray())
            {
                var name = Text(r, "name");
                if (!byName.TryGetValue(name, out var ids)) byName[name] = ids = [];
                ids.Add(r.GetProperty("id").GetInt32());
            }
            all[p.Name] = byName;
        }
        return all;
    }

    // ---- Without the API: the public Armory pages ----

    async Task<ArmoryResult> RunWithPages(GuildDb db)
    {
        var characters = await db.Characters.OrderBy(c => c.Name).ToListAsync();

        // Read the pages first (four at a time), then change the database in one go.
        var pages = new (Outcome outcome, Sheet? sheet)[characters.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, characters.Count), new ParallelOptions { MaxDegreeOfParallelism = 4 },
            async (i, _) => pages[i] = await ReadPage(characters[i]));

        var result = new ArmoryResult(characters.Count, [], [], [], []);
        for (var i = 0; i < characters.Count; i++)
        {
            var c = characters[i];
            var (outcome, sheet) = pages[i];
            if (outcome == Outcome.NoPage) { result.NoPage.Add(c.Name); continue; }
            if (outcome == Outcome.Failed || sheet is null) { result.Failed.Add(c.Name); continue; }

            var changes = new List<string>();
            var url = CharacterPrefix + sheet.Slug;
            if (c.ArmoryUrl != url) { c.ArmoryUrl = url; changes.Add("now has an Armory page"); }
            await Apply(db, c, sheet, changes);
            if (changes.Count > 0) result.Changes.Add($"{c.Name}: {string.Join(", ", changes)}");
            if (!string.Equals(sheet.GuildSlug, GuildSlug, StringComparison.OrdinalIgnoreCase)) result.LeftGuild.Add(c.Name);
        }
        await db.SaveChangesAsync();
        return result;
    }

    async Task<(Outcome, Sheet?)> ReadPage(Character c)
    {
        // Only ever fetches Armory character pages, whatever link the admin has typed in.
        // A character without a link (below level 10 when it was added) gets the address its name gives.
        var slug = c.ArmoryUrl.StartsWith(CharacterPrefix, StringComparison.OrdinalIgnoreCase)
            ? c.ArmoryUrl[CharacterPrefix.Length..]
            : Uri.EscapeDataString(c.Name.Trim().ToLowerInvariant().Replace(' ', '-'));
        var url = CharacterPrefix + slug;
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
                        var name = Text(p, "name");
                        if (name.Length == 0 || !p.TryGetProperty("tiers", out var tiers) || tiers.ValueKind != JsonValueKind.Array) continue;
                        var best = tiers.EnumerateArray()
                            .Select(t => t.TryGetProperty("skillPoints", out var s) && s.TryGetInt32(out var n) ? n : 0).DefaultIfEmpty(0).Max();
                        if (best > 0) skills[name] = best;
                    }
                }
            }

            return (Outcome.Ok, new Sheet(c.Name, slug,
                ch.TryGetProperty("level", out var level) && level.TryGetInt32(out var l) ? l : 0,
                Name(ch, "class"), Name(ch, "race"), Name(ch, "guild", "slug"), skills, null));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            log.LogWarning("Armory update: could not read {Url}: {Message}", url, e.Message);
            return (Outcome.Failed, null);
        }
    }

    // ---- Shared ----

    /// <summary>Level, class, race and profession skill levels: changes the character and notes what changed.</summary>
    async Task Apply(GuildDb db, Character c, Sheet sheet, List<string> changes)
    {
        var classes = config.GetSection("Guild:Classes").Get<string[]>() ?? [];
        var races = config.GetSection("Guild:Races").Get<string[]>() ?? [];
        var professions = (config.GetSection("Guild:Professions").Get<ProfessionInfo[]>() ?? []).Select(p => p.Name).ToHashSet();

        if (sheet.Level is >= 1 and <= 100 && sheet.Level != c.Level) { changes.Add($"level {c.Level} → {sheet.Level}"); c.Level = sheet.Level; }
        if (classes.Contains(sheet.Class) && sheet.Class != c.Class) { changes.Add($"class {c.Class} → {sheet.Class}"); c.Class = sheet.Class; }
        if (races.Contains(sheet.Race) && sheet.Race != c.Race) { changes.Add(c.Race.Length > 0 ? $"race {c.Race} → {sheet.Race}" : $"race {sheet.Race}"); c.Race = sheet.Race; }
        if (sheet.Professions is null) return;

        var wanted = sheet.Professions.Where(p => professions.Contains(p.Key) && p.Value is >= 1 and <= 300).ToDictionary(p => p.Key, p => p.Value);
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

    // Reads the one JSON value the text starts with and ignores the rest of the page.
    static JsonDocument ParseFirstValue(string text)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(text));
        return JsonDocument.ParseValue(ref reader);
    }

    static string Text(JsonElement e, string property) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    // A text inside a nested object: {"class": {"name": "Priest"}}
    static string Name(JsonElement e, string property, string inner = "name") =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out var v) ? Text(v, inner) : "";

    static int Id(JsonElement e, string property) =>
        e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Object && v.TryGetProperty("id", out var id) && id.TryGetInt32(out var n) ? n : 0;
}
