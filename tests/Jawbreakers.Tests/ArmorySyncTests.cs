using System.Net;
using System.Text.Json;

namespace Jawbreakers.Tests;

/// <summary>
/// The "Update from the Armory" button (POST /api/armory/update) against a fake Blizzard.
/// The site starts with the 25 members in seed/roster.json, among them Zig Hale (Paladin, Undead, level 25).
/// </summary>
public class ArmorySyncTests
{
    const int SeedMembers = 25;
    const int ElixirOfMinorForce = 1245250; // spell ids from wwwroot/recipes/alchemy.json
    const int MinorArcaneElixir = 1245246;

    [Fact]
    public async Task With_the_API_it_adds_new_members_removes_those_who_left_and_updates_the_rest()
    {
        using var site = new GuildSite();
        site.Blizzard.Roster(("Zig", "Hale", "zig-hale", 26, 2, 5), ("Test", "Newcomer", "test-newcomer", 12, 1, 2));
        var client = site.CreateClient();

        var response = await client.PostAsync("api/armory/update", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await Json.Read(response)).GetProperty("result");
        Assert.Equal(new[] { "Test Newcomer" }, Json.Strings(result.GetProperty("joined")));
        Assert.Equal(SeedMembers - 1, result.GetProperty("removed").GetArrayLength());
        Assert.Contains("Zig Hale: level 25 → 26", Json.Strings(result.GetProperty("changes")));

        var characters = (await Json.Get(client, "api/characters")).EnumerateArray().ToList();
        Assert.Equal(new[] { "Zig Hale", "Test Newcomer" }, characters.Select(c => c.GetProperty("name").GetString()));
        var newcomer = characters[1];
        Assert.Equal("Warrior", newcomer.GetProperty("class").GetString());
        Assert.Equal(ArmorySync.CharacterPrefix + "test-newcomer", newcomer.GetProperty("armoryUrl").GetString());
    }

    [Fact]
    public async Task With_the_API_known_recipe_names_become_the_crafting_pages_spell_ids()
    {
        using var site = new GuildSite();
        site.Blizzard.Roster(("Zig", "Hale", "zig-hale", 26, 2, 5));
        site.Blizzard.Json("/profile/wow/character/zig-hale/professions", new
        {
            primaries = new[]
            {
                new
                {
                    profession = new { name = "Alchemy" },
                    tiers = new[]
                    {
                        new { skill_points = 75, known_recipes = new[] { new { name = "Elixir of Minor Force" }, new { name = "Minor Arcane Elixir" }, new { name = "Not A Recipe" } } }
                    }
                }
            }
        });
        var client = site.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("api/armory/update", null)).StatusCode);

        var zig = (await Json.Get(client, "api/characters")).EnumerateArray().Single().GetProperty("id").GetInt32();
        var crafting = await Json.Get(client, "api/crafting");
        Assert.True(crafting.GetProperty("exact").GetBoolean());
        Assert.Contains(crafting.GetProperty("skills").EnumerateArray(), s =>
            s.GetProperty("characterId").GetInt32() == zig && s.GetProperty("profession").GetString() == "Alchemy" && s.GetProperty("skill").GetInt32() == 75);
        var known = crafting.GetProperty("known").EnumerateArray()
            .Where(k => k.GetProperty("characterId").GetInt32() == zig)
            .Select(k => k.GetProperty("spellId").GetInt32()).Order();
        Assert.Equal(new[] { MinorArcaneElixir, ElixirOfMinorForce }, known);
    }

    [Fact]
    public async Task An_empty_guild_list_counts_as_a_failed_read_and_removes_nobody()
    {
        using var site = new GuildSite();
        site.Blizzard.Roster();
        var client = site.CreateClient();

        var response = await client.PostAsync("api/armory/update", null);

        var body = await Json.Read(response);
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("result").GetProperty("error").ValueKind);
        Assert.Equal(0, body.GetProperty("secondsLeft").GetInt32()); // a failed update doesn't make people wait
        Assert.Equal(SeedMembers, (await Json.Get(client, "api/characters")).GetArrayLength());
    }

    [Fact]
    public async Task Blizzard_refusing_the_client_leaves_the_site_as_it_was()
    {
        using var site = new GuildSite();
        site.Blizzard.Json("/token", new { error = "invalid_client" }, HttpStatusCode.Unauthorized);
        var client = site.CreateClient();

        var body = await Json.Read(await client.PostAsync("api/armory/update", null));

        Assert.Equal("The Armory could not be read. Try again in a little while.", body.GetProperty("result").GetProperty("error").GetString());
        Assert.Equal(SeedMembers, (await Json.Get(client, "api/characters")).GetArrayLength());
    }

    [Fact]
    public async Task A_second_update_within_ten_minutes_is_refused()
    {
        using var site = new GuildSite();
        site.Blizzard.Roster(("Zig", "Hale", "zig-hale", 26, 2, 5));
        var client = site.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("api/armory/update", null)).StatusCode);
        var second = await client.PostAsync("api/armory/update", null);

        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.InRange((await Json.Read(second)).GetProperty("secondsLeft").GetInt32(), 1, 600);
    }

    [Fact]
    public async Task Without_the_API_it_reads_the_public_Armory_pages_and_never_removes_anyone()
    {
        using var site = new GuildSite(useApi: false);
        site.Blizzard.Html("/en-gb/forever/eu/armory/character/zig-hale", ArmoryPage(level: 27, guild: "jawbreakers", mining: 120));
        site.Blizzard.Html("/en-gb/forever/eu/armory/character/tok-toki", ArmoryPage(level: 20, guild: "another-guild", mining: 0));
        var client = site.CreateClient();

        var response = await client.PostAsync("api/armory/update", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await Json.Read(response)).GetProperty("result");
        // Zig Hale's seed professions: Mining 40, Blacksmithing 77, ... The page only lists Mining 120.
        var zig = Assert.Single(Json.Strings(result.GetProperty("changes")), c => c.StartsWith("Zig Hale: "));
        Assert.Contains("level 25 → 27", zig);
        Assert.Contains("Mining 40 → 120", zig);
        Assert.Contains("dropped Blacksmithing", zig);
        Assert.Equal(new[] { "Tok Toki" }, Json.Strings(result.GetProperty("leftGuild")));
        Assert.Equal(SeedMembers - 2, result.GetProperty("noPage").GetArrayLength()); // the fake has no other pages
        Assert.DoesNotContain(site.Blizzard.Requests, p => p.StartsWith("/data/") || p == "/token");
        Assert.Equal(SeedMembers, (await Json.Get(client, "api/characters")).GetArrayLength());
    }

    static string ArmoryPage(int level, string guild, int mining)
    {
        object[] professions = mining > 0 ? [new { name = "Mining", tiers = new[] { new { skillPoints = mining } } }] : [];
        var state = JsonSerializer.Serialize(new
        {
            character = new
            {
                level,
                @class = new { name = level > 20 ? "Paladin" : "Warrior" },
                race = new { name = level > 20 ? "Undead" : "Orc" },
                guild = new { slug = guild },
                professions = new { primaries = professions }
            }
        });
        return $"<html><script>var characterProfileInitialState = {state};\nother();</script></html>";
    }
}
