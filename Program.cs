using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Jawbreakers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Everything that changes at runtime (database, cookie keys) lives in one folder.
var dataDir = builder.Configuration["DataDir"] ?? Path.Combine(builder.Environment.ContentRootPath, "data");
Directory.CreateDirectory(dataDir);

builder.Services.AddDbContext<GuildDb>(o => o.UseSqlite($"Data Source={Path.Combine(dataDir, "jawbreakers.db")}"));
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.Cookie.Name = "jawbreakers_admin";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    // The live site is always https, even though the proxies in front hand the request over as http
    if (!builder.Environment.IsDevelopment()) o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    o.ExpireTimeSpan = TimeSpan.FromDays(14);
    o.SlidingExpiration = true;
    o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
});
builder.Services.AddAuthorization();
builder.Services.AddHttpClient(ArmorySync.HttpClientName, http =>
{
    http.Timeout = TimeSpan.FromSeconds(20);
    http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; JawbreakersGuildSite/1.0)");
});
builder.Services.AddSingleton<ArmorySync>();

var app = builder.Build();

var classes = app.Configuration.GetSection("Guild:Classes").Get<string[]>() ?? [];
var races = app.Configuration.GetSection("Guild:Races").Get<string[]>() ?? [];
var requirements = app.Configuration.GetSection("Guild:EventRequirements").Get<string[]>() ?? [];
var professions = app.Configuration.GetSection("Guild:Professions").Get<ProfessionInfo[]>() ?? [];

// The admin password comes from configuration (environment variable Admin__Password), never from a file in the repo.
var adminPassword = app.Configuration["Admin:Password"];
if (string.IsNullOrEmpty(adminPassword))
{
    if (app.Environment.IsDevelopment())
    {
        adminPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        app.Logger.LogWarning("Admin__Password is not set. Temporary admin password for this run: {Password}", adminPassword);
    }
    else
    {
        app.Logger.LogWarning("Admin__Password is not set: admin sign-in is disabled.");
    }
}

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<GuildDb>();
    db.Database.EnsureCreated();
    // EnsureCreated does nothing for a database that already exists, so tables added later are created here.
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS "CharacterProfessions" ("Id" INTEGER NOT NULL CONSTRAINT "PK_CharacterProfessions" PRIMARY KEY AUTOINCREMENT,
            "CharacterId" INTEGER NOT NULL, "Profession" TEXT NOT NULL, "Skill" INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS "KnownRecipes" ("Id" INTEGER NOT NULL CONSTRAINT "PK_KnownRecipes" PRIMARY KEY AUTOINCREMENT,
            "CharacterId" INTEGER NOT NULL, "Profession" TEXT NOT NULL, "SpellId" INTEGER NOT NULL);
        """);
    if (!db.Texts.Any(t => t.Key == "about"))
    {
        db.Texts.Add(new SiteText
        {
            Key = "about",
            Value = "Jawbreakers is a Horde guild on the EU PvP ruleset of World of Warcraft: Forever, founded in 2026. Our Guild Master is Zig Hale.\n\nThis text is edited on the admin page."
        });
        db.SaveChanges();
    }

    // First start only: fill the member lists from seed/roster.json (taken from the official Armory, 2026-11-14).
    // Characters below level 10 have no Armory page, so they have no link and no known race.
    var rosterFile = Path.Combine(app.Environment.ContentRootPath, "seed", "roster.json");
    if (!db.Characters.Any() && File.Exists(rosterFile))
    {
        const string armory = "https://worldofwarcraft.blizzard.com/en-gb/forever/eu/armory/character/";
        var roster = System.Text.Json.JsonSerializer.Deserialize<List<RosterEntry>>(
            File.ReadAllText(rosterFile), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)) ?? [];
        db.Characters.AddRange(roster.Select(r => new Character
        {
            Name = r.Name, Class = r.Class, Race = r.Race, Level = r.Level,
            ArmoryUrl = r.Slug.Length > 0 ? armory + r.Slug : ""
        }));
        db.SaveChanges();
    }

    // Same for professions: seed/professions.json holds each character's skill levels from the Armory (2026-11-14).
    var professionsFile = Path.Combine(app.Environment.ContentRootPath, "seed", "professions.json");
    if (!db.CharacterProfessions.Any() && File.Exists(professionsFile))
    {
        var seed = System.Text.Json.JsonSerializer.Deserialize<List<ProfessionSeed>>(
            File.ReadAllText(professionsFile), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)) ?? [];
        var ids = db.Characters.ToDictionary(c => c.Name, c => c.Id);
        foreach (var s in seed.Where(s => ids.ContainsKey(s.Name)))
            db.CharacterProfessions.AddRange(s.Professions
                .Where(p => professions.Any(x => x.Name == p.Key))
                .Select(p => new CharacterProfession { CharacterId = ids[s.Name], Profession = p.Key, Skill = p.Value }));
        db.SaveChanges();
    }
}

// Behind the NAS's reverse proxy the request arrives as plain http from localhost;
// this restores the visitor's https and address, so the sign-in cookie is marked Secure.
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                     | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
});

// The live site is at https://lundqwist.se/jawbreakers/ (PathBase=/jawbreakers); locally it runs at the root.
// Pages use relative addresses, so they need the trailing slash: /jawbreakers -> /jawbreakers/
var pathBase = app.Configuration["PathBase"];
if (!string.IsNullOrEmpty(pathBase))
{
    app.UsePathBase(pathBase);
    app.Use(async (http, next) =>
    {
        if (http.Request.PathBase.HasValue && !http.Request.Path.HasValue)
            http.Response.Redirect(http.Request.PathBase + "/" + http.Request.QueryString);
        else if (!http.Request.PathBase.HasValue)
            http.Response.StatusCode = 404; // only answers under its own address
        else
            await next();
    });
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

var api = app.MapGroup("/api");
var admin = api.MapGroup("").RequireAuthorization();

static IResult Bad(string message) => Results.BadRequest(new { error = message });
static string Clean(string? s) => (s ?? "").Trim();

api.MapGet("/meta", (ClaimsPrincipal user) => new
{
    classes,
    races,
    requirements,
    professions,
    admin = user.Identity?.IsAuthenticated == true
});

// ---- Sign-in ----

api.MapPost("/login", async (LoginInput input, HttpContext http) =>
{
    var given = SHA256.HashData(Encoding.UTF8.GetBytes(input.Password ?? ""));
    var wanted = SHA256.HashData(Encoding.UTF8.GetBytes(adminPassword ?? ""));
    if (string.IsNullOrEmpty(adminPassword) || !CryptographicOperations.FixedTimeEquals(given, wanted))
    {
        await Task.Delay(800); // slows down guessing
        return Results.Json(new { error = "Wrong password." }, statusCode: 401);
    }
    var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], CookieAuthenticationDefaults.AuthenticationScheme);
    await http.SignInAsync(new ClaimsPrincipal(identity));
    return Results.NoContent();
});

api.MapPost("/logout", async (HttpContext http) =>
{
    await http.SignOutAsync();
    return Results.NoContent();
});

// ---- Characters ----

api.MapGet("/characters", async (GuildDb db) =>
    await db.Characters.OrderByDescending(c => c.Level).ThenBy(c => c.Name).ToListAsync());

string? ApplyCharacter(Character c, CharacterInput input)
{
    var name = Clean(input.Name);
    var url = Clean(input.ArmoryUrl);
    if (name.Length is 0 or > 40) return "Name is required (at most 40 characters).";
    if (!classes.Contains(input.Class)) return "Pick a class from the list.";
    var race = Clean(input.Race); // empty means not known yet
    if (race.Length > 0 && !races.Contains(race)) return "Pick a race from the list.";
    if (input.Level is < 1 or > 100) return "Level must be between 1 and 100.";
    if (url.Length > 0 && !IsWebUrl(url)) return "The Armory link must start with http:// or https://.";
    if (Clean(input.Description).Length > 2000) return "Description is at most 2000 characters.";
    c.Name = name;
    c.Class = input.Class!;
    c.Race = race;
    c.Level = input.Level;
    c.ArmoryUrl = url;
    c.Description = Clean(input.Description);
    return null;
}

// Replaces the character's professions with the ones given (skill 0 = doesn't have it). Null leaves them alone.
string? ApplyProfessions(GuildDb db, Character c, List<ProfessionInput>? input)
{
    if (input is null) return null;
    var wanted = input.Where(p => p.Skill > 0).ToList();
    if (wanted.Any(p => !professions.Any(x => x.Name == p.Name))) return "Unknown profession.";
    if (wanted.Any(p => p.Skill > 300)) return "Profession skill is between 1 and 300.";
    if (wanted.Select(p => p.Name).Distinct().Count() != wanted.Count) return "Each profession can only be given once.";
    db.CharacterProfessions.RemoveRange(db.CharacterProfessions.Where(p => p.CharacterId == c.Id));
    foreach (var p in wanted)
        db.CharacterProfessions.Add(new CharacterProfession { CharacterId = c.Id, Profession = p.Name!, Skill = p.Skill });
    return null;
}

static bool IsWebUrl(string url) =>
    url.Length <= 300 && Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

admin.MapPost("/characters", async (CharacterInput input, GuildDb db) =>
{
    var c = new Character();
    if (ApplyCharacter(c, input) is { } error) return Bad(error);
    db.Characters.Add(c);
    await db.SaveChangesAsync(); // gives the character its id
    if (ApplyProfessions(db, c, input.Professions) is { } professionError)
    {
        db.Characters.Remove(c);
        await db.SaveChangesAsync();
        return Bad(professionError);
    }
    await db.SaveChangesAsync();
    return Results.Ok(c);
});

admin.MapPut("/characters/{id:int}", async (int id, CharacterInput input, GuildDb db) =>
{
    var c = await db.Characters.FindAsync(id);
    if (c is null) return Results.NotFound();
    if ((ApplyCharacter(c, input) ?? ApplyProfessions(db, c, input.Professions)) is { } error) return Bad(error);
    await db.SaveChangesAsync();
    return Results.Ok(c);
});

admin.MapPut("/characters/{id:int}/description", async (int id, DescriptionInput input, GuildDb db) =>
{
    var c = await db.Characters.FindAsync(id);
    if (c is null) return Results.NotFound();
    var text = Clean(input.Description);
    if (text.Length > 2000) return Bad("Description is at most 2000 characters.");
    c.Description = text;
    await db.SaveChangesAsync();
    return Results.Ok(c);
});

admin.MapDelete("/characters/{id:int}", async (int id, GuildDb db) =>
{
    await db.Characters.Where(c => c.Id == id).ExecuteDeleteAsync();
    await db.CharacterProfessions.Where(p => p.CharacterId == id).ExecuteDeleteAsync();
    await db.KnownRecipes.Where(k => k.CharacterId == id).ExecuteDeleteAsync();
    return Results.NoContent();
});

// Picture, gear and stats for the character window. 404 when there is nothing to show (no API client, or below level 10).
api.MapGet("/characters/{id:int}/sheet", async (int id, GuildDb db, ArmorySync armory) =>
{
    var c = await db.Characters.FindAsync(id);
    var sheet = c is null ? null : await armory.CharacterSheet(c);
    return sheet is null ? Results.NotFound() : Results.Ok(sheet);
});

// ---- Update from the Armory ----
// Anyone can start it from the Guild members page, but only once every ten minutes (see ArmorySync.cs).

api.MapGet("/armory", async (GuildDb db) =>
{
    var last = await ArmorySync.LastStarted(db);
    return new { lastUpdate = last, secondsLeft = ArmorySync.SecondsLeft(last), result = await ArmorySync.LastResult(db) };
});

api.MapPost("/armory/update", async (GuildDb db, ArmorySync armory) =>
{
    var result = await armory.Run(db);
    var last = await ArmorySync.LastStarted(db);
    return result is null
        ? Results.Json(new { error = "The members were updated less than ten minutes ago.", secondsLeft = ArmorySync.SecondsLeft(last) }, statusCode: 429)
        : Results.Ok(new { lastUpdate = last, secondsLeft = ArmorySync.SecondsLeft(last), result });
});

// ---- Crafting ----

api.MapGet("/crafting", async (GuildDb db) => new
{
    // True once Blizzard's API has told which recipes each character knows: then nothing is guessed from skill levels
    exact = await db.Texts.AnyAsync(t => t.Key == "armory-exact"),
    skills = await db.CharacterProfessions.Select(p => new { p.CharacterId, p.Profession, p.Skill }).ToListAsync(),
    known = await db.KnownRecipes.Select(k => new { k.CharacterId, k.Profession, k.SpellId }).ToListAsync()
});

// Sets which characters know one recipe (for recipes that don't come from a trainer).
admin.MapPut("/recipes/{spellId:int}/crafters", async (int spellId, CraftersInput input, GuildDb db) =>
{
    var profession = Clean(input.Profession);
    var ids = (input.CharacterIds ?? []).Distinct().ToList();
    if (spellId <= 0 || !professions.Any(p => p.Name == profession && p.Recipes.Length > 0)) return Bad("Unknown recipe.");
    var allowed = await db.CharacterProfessions.Where(p => p.Profession == profession).Select(p => p.CharacterId).ToListAsync();
    if (ids.Except(allowed).Any()) return Bad("Only characters with that profession can be ticked.");
    await db.KnownRecipes.Where(k => k.SpellId == spellId && k.Profession == profession).ExecuteDeleteAsync();
    db.KnownRecipes.AddRange(ids.Select(id => new KnownRecipe { CharacterId = id, Profession = profession, SpellId = spellId }));
    await db.SaveChangesAsync();
    return Results.NoContent();
});

// ---- Events ----

api.MapGet("/events", async (GuildDb db) => await db.Events.OrderBy(e => e.StartsAt).ToListAsync());

static string? ApplyEvent(GuildEvent e, EventInput input)
{
    var title = Clean(input.Title);
    var reqs = (input.Requirements ?? []).Select(Clean).Where(r => r.Length > 0).Distinct().ToList();
    if (title.Length is 0 or > 100) return "Title is required (at most 100 characters).";
    if (input.StartsAt.Year < 2000) return "Date and time are required.";
    if (Clean(input.StartingPoint).Length is 0 or > 100) return "Starting point is required (at most 100 characters).";
    if (Clean(input.Description).Length > 4000) return "Description is at most 4000 characters.";
    if (input.MinLevel is < 1 or > 100) return "Level requirement must be between 1 and 100.";
    if (reqs.Count > 30 || reqs.Any(r => r.Length > 60)) return "Too many or too long requirements.";
    e.Title = title;
    e.StartsAt = DateTime.SpecifyKind(input.StartsAt, DateTimeKind.Unspecified);
    e.StartingPoint = Clean(input.StartingPoint);
    e.Description = Clean(input.Description);
    e.MinLevel = input.MinLevel;
    e.Requirements = reqs;
    return null;
}

admin.MapPost("/events", async (EventInput input, GuildDb db) =>
{
    var e = new GuildEvent();
    if (ApplyEvent(e, input) is { } error) return Bad(error);
    db.Events.Add(e);
    await db.SaveChangesAsync();
    return Results.Ok(e);
});

admin.MapPut("/events/{id:int}", async (int id, EventInput input, GuildDb db) =>
{
    var e = await db.Events.FindAsync(id);
    if (e is null) return Results.NotFound();
    if (ApplyEvent(e, input) is { } error) return Bad(error);
    await db.SaveChangesAsync();
    return Results.Ok(e);
});

admin.MapDelete("/events/{id:int}", async (int id, GuildDb db) =>
{
    await db.Events.Where(e => e.Id == id).ExecuteDeleteAsync();
    return Results.NoContent();
});

// ---- About and guild history ----

api.MapGet("/about", async (GuildDb db) => new
{
    text = (await db.Texts.FindAsync("about"))?.Value ?? "",
    milestones = await db.Milestones.OrderByDescending(m => m.Date).ToListAsync()
});

admin.MapPut("/about", async (AboutInput input, GuildDb db) =>
{
    var text = Clean(input.Text);
    if (text.Length > 20000) return Bad("The text is at most 20000 characters.");
    var row = await db.Texts.FindAsync("about");
    if (row is null) db.Texts.Add(new SiteText { Key = "about", Value = text });
    else row.Value = text;
    await db.SaveChangesAsync();
    return Results.NoContent();
});

admin.MapPost("/milestones", async (MilestoneInput input, GuildDb db) =>
{
    var title = Clean(input.Title);
    if (title.Length is 0 or > 100) return Bad("Title is required (at most 100 characters).");
    if (input.Date.Year < 2000) return Bad("Date is required.");
    if (Clean(input.Description).Length > 1000) return Bad("Description is at most 1000 characters.");
    var m = new Milestone { Date = input.Date, Title = title, Description = Clean(input.Description) };
    db.Milestones.Add(m);
    await db.SaveChangesAsync();
    return Results.Ok(m);
});

admin.MapDelete("/milestones/{id:int}", async (int id, GuildDb db) =>
{
    await db.Milestones.Where(m => m.Id == id).ExecuteDeleteAsync();
    return Results.NoContent();
});

app.Run();

// Lets the tests (tests/Jawbreakers.Tests) start the site in memory with WebApplicationFactory<Program>.
public partial class Program;
