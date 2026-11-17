using System.Net;
using System.Net.Http.Json;

namespace Jawbreakers.Tests;

/// <summary>Sign-in and the checks on what the admin page sends.</summary>
public class AdminApiTests
{
    static object Character(string name = "Test Character", string @class = "Warrior", string race = "Orc", int level = 30,
        string armoryUrl = "", object? professions = null) =>
        new { name, @class, race, level, armoryUrl, description = "", professions };

    [Fact]
    public async Task Changes_need_the_admin_to_be_signed_in()
    {
        using var site = new GuildSite();
        var client = site.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("api/characters", Character())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.DeleteAsync("api/events/1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsJsonAsync("api/about", new { text = "x" })).StatusCode);
        Assert.False((await Json.Get(client, "api/meta")).GetProperty("admin").GetBoolean());
    }

    [Fact]
    public async Task A_wrong_password_is_refused()
    {
        using var site = new GuildSite();
        var client = site.CreateClient();

        var response = await client.PostAsJsonAsync("api/login", new { password = "wrong" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Wrong password.", (await Json.Read(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Signing_out_ends_the_session()
    {
        using var site = new GuildSite();
        var client = await site.AdminClient();
        Assert.True((await Json.Get(client, "api/meta")).GetProperty("admin").GetBoolean());

        await client.PostAsync("api/logout", null);

        Assert.False((await Json.Get(client, "api/meta")).GetProperty("admin").GetBoolean());
    }

    public static TheoryData<object, string> BadCharacters => new()
    {
        { Character(name: ""), "Name is required (at most 40 characters)." },
        { Character(name: new string('a', 41)), "Name is required (at most 40 characters)." },
        { Character(@class: "Death Knight"), "Pick a class from the list." },
        { Character(race: "Human"), "Pick a race from the list." },
        { Character(level: 0), "Level must be between 1 and 100." },
        { Character(armoryUrl: "javascript:alert(1)"), "The Armory link must start with http:// or https://." },
        { Character(professions: new[] { new { name = "Alchemy", skill = 301 } }), "Profession skill is between 1 and 300." },
        { Character(professions: new[] { new { name = "Jewelcrafting", skill = 10 } }), "Unknown profession." },
        { Character(professions: new[] { new { name = "Alchemy", skill = 10 }, new { name = "Alchemy", skill = 20 } }), "Each profession can only be given once." },
    };

    [Theory]
    [MemberData(nameof(BadCharacters))]
    public async Task A_character_with_bad_input_is_refused_and_not_saved(object input, string error)
    {
        using var site = new GuildSite();
        var client = await site.AdminClient();
        var before = (await Json.Get(client, "api/characters")).GetArrayLength();

        var response = await client.PostAsJsonAsync("api/characters", input);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(error, (await Json.Read(response)).GetProperty("error").GetString());
        Assert.Equal(before, (await Json.Get(client, "api/characters")).GetArrayLength());
    }

    [Fact]
    public async Task A_valid_character_is_saved_with_its_professions()
    {
        using var site = new GuildSite();
        var client = await site.AdminClient();

        var response = await client.PostAsJsonAsync("api/characters",
            Character(name: "  Test Character  ", race: "", professions: new[] { new { name = "Alchemy", skill = 150 }, new { name = "Mining", skill = 0 } }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await Json.Read(response);
        Assert.Equal("Test Character", saved.GetProperty("name").GetString()); // trimmed
        var id = saved.GetProperty("id").GetInt32();
        var skills = (await Json.Get(client, "api/crafting")).GetProperty("skills").EnumerateArray()
            .Where(s => s.GetProperty("characterId").GetInt32() == id)
            .Select(s => (s.GetProperty("profession").GetString()!, s.GetProperty("skill").GetInt32()));
        Assert.Equal(new[] { ("Alchemy", 150) }, skills); // skill 0 means "doesn't have it"
    }

    [Fact]
    public async Task Only_characters_with_the_profession_can_be_ticked_for_a_recipe()
    {
        using var site = new GuildSite();
        var client = await site.AdminClient();
        var id = (await Json.Read(await client.PostAsJsonAsync("api/characters", Character()))).GetProperty("id").GetInt32();

        var response = await client.PutAsJsonAsync("api/recipes/1245250/crafters", new { profession = "Alchemy", characterIds = new[] { id } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Only characters with that profession can be ticked.", (await Json.Read(response)).GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("", "2026-12-01T20:00:00", "Orgrimmar", "Title is required (at most 100 characters).")]
    [InlineData("Raid", "0001-01-01T00:00:00", "Orgrimmar", "Date and time are required.")]
    [InlineData("Raid", "2026-12-01T20:00:00", "", "Starting point is required (at most 100 characters).")]
    public async Task An_event_with_bad_input_is_refused(string title, string startsAt, string startingPoint, string error)
    {
        using var site = new GuildSite();
        var client = await site.AdminClient();

        var response = await client.PostAsJsonAsync("api/events", new { title, startsAt, startingPoint, description = "", requirements = Array.Empty<string>() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(error, (await Json.Read(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task An_event_keeps_the_realm_time_as_typed()
    {
        using var site = new GuildSite();
        var client = await site.AdminClient();

        var response = await client.PostAsJsonAsync("api/events", new
        {
            title = "Ragefire Chasm", startsAt = "2026-12-01T20:00:00", startingPoint = "Orgrimmar",
            description = "", minLevel = 13, requirements = new[] { "Consumables", " Consumables ", "" }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = (await Json.Get(client, "api/events")).EnumerateArray().Single();
        Assert.Equal("2026-12-01T20:00:00", saved.GetProperty("startsAt").GetString()); // no time zone added
        Assert.Equal(new[] { "Consumables" }, Json.Strings(saved.GetProperty("requirements"))); // trimmed, no blanks or doubles
    }
}
