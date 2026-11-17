using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Jawbreakers.Tests;

/// <summary>
/// The whole site, started in memory with its own empty database in a temporary folder.
/// Every request to Blizzard goes to <see cref="FakeBlizzard"/> instead, so the tests never use the network.
/// </summary>
public sealed class GuildSite : WebApplicationFactory<Program>
{
    public const string AdminPassword = "test-password";

    readonly string _dataDir = Path.Combine(Path.GetTempPath(), "jawbreakers-tests", Guid.NewGuid().ToString("N"));
    readonly bool _useApi;

    public FakeBlizzard Blizzard { get; } = new();

    /// <param name="useApi">True: the site has a Blizzard API client. False: it reads the public Armory pages.</param>
    public GuildSite(bool useApi = true) => _useApi = useApi;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Development: the sign-in cookie isn't marked Secure, so it works over the test client's http
        builder.UseEnvironment("Development");
        builder.UseSetting("DataDir", _dataDir);
        builder.UseSetting("Admin:Password", AdminPassword);
        builder.UseSetting("BLIZZARD_CLIENT_ID", _useApi ? "test-client" : "");
        builder.UseSetting("BLIZZARD_CLIENT_SECRET", _useApi ? "test-secret" : "");
        builder.ConfigureTestServices(services =>
            services.AddHttpClient(ArmorySync.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Blizzard));
    }

    /// <summary>A client that is signed in as the admin.</summary>
    public async Task<HttpClient> AdminClient()
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("api/login", new { password = AdminPassword });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); // lets go of the database file
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { /* the OS cleans temp folders */ }
    }
}

/// <summary>Answers the site's calls to Blizzard with canned JSON or HTML, by path. Anything else is a 404.</summary>
public sealed class FakeBlizzard : HttpMessageHandler
{
    readonly Dictionary<string, (HttpStatusCode Status, string Body, string Type)> _answers = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Requests { get; } = [];

    public FakeBlizzard()
    {
        Json("/token", new { access_token = "fake-token", expires_in = 86400 });
        Json("/data/wow/playable-class/index", new { classes = new[] { new { id = 1, name = "Warrior" }, new { id = 2, name = "Paladin" } } });
        Json("/data/wow/playable-race/index", new { races = new[] { new { id = 2, name = "Orc" }, new { id = 5, name = "Undead" } } });
    }

    public void Json(string path, object body, HttpStatusCode status = HttpStatusCode.OK) =>
        _answers[path] = (status, JsonSerializer.Serialize(body), "application/json");

    public void Html(string path, string body, HttpStatusCode status = HttpStatusCode.OK) =>
        _answers[path] = (status, body, "text/html");

    /// <summary>The guild's member list: (name, surname, slug, level, class id, race id).</summary>
    public void Roster(params (string Name, string Surname, string Slug, int Level, int ClassId, int RaceId)[] members) =>
        Json("/data/wow/guild/jawbreakers/roster", new
        {
            members = members.Select(m => new
            {
                character = new
                {
                    name = m.Name, surname = m.Surname, slug = m.Slug, level = m.Level,
                    playable_class = new { id = m.ClassId }, playable_race = new { id = m.RaceId }
                }
            })
        });

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
        lock (Requests) Requests.Add(path);
        var response = _answers.TryGetValue(path, out var a)
            ? new HttpResponseMessage(a.Status) { Content = new StringContent(a.Body, Encoding.UTF8, a.Type) }
            : new HttpResponseMessage(HttpStatusCode.NotFound);
        return Task.FromResult(response);
    }
}

static class Json
{
    public static async Task<JsonElement> Read(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    public static async Task<JsonElement> Get(HttpClient client, string path) => await Read(await client.GetAsync(path));

    public static IEnumerable<string> Strings(JsonElement array) => array.EnumerateArray().Select(e => e.GetString()!);
}
