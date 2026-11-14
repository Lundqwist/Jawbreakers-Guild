using Microsoft.EntityFrameworkCore;

namespace Jawbreakers;

public class Character
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Class { get; set; } = "";
    public string Race { get; set; } = "";
    public int Level { get; set; }
    public string ArmoryUrl { get; set; } = "";
    public string Description { get; set; } = "";
}

public class GuildEvent
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    /// <summary>Realm time, as the admin typed it. No time zone conversion anywhere.</summary>
    public DateTime StartsAt { get; set; }
    public string StartingPoint { get; set; } = "";
    public string Description { get; set; } = "";
    public int? MinLevel { get; set; }
    /// <summary>Ticked requirements from Guild:EventRequirements in appsettings.json.</summary>
    public List<string> Requirements { get; set; } = [];
}

public class Milestone
{
    public int Id { get; set; }
    public DateOnly Date { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
}

public class SiteText
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>A profession a character has, with the skill level from the Armory.</summary>
public class CharacterProfession
{
    public int Id { get; set; }
    public int CharacterId { get; set; }
    public string Profession { get; set; } = "";
    public int Skill { get; set; }
}

/// <summary>
/// A recipe the admin has ticked for a character. Only needed for recipes that come from drops, vendors or quests:
/// trainer recipes count as known as soon as the skill is high enough.
/// </summary>
public class KnownRecipe
{
    public int Id { get; set; }
    public int CharacterId { get; set; }
    public string Profession { get; set; } = "";
    /// <summary>The recipe's spell id, as in wwwroot/recipes/*.json and on Wowhead.</summary>
    public int SpellId { get; set; }
}

/// <summary>One entry of Guild:Professions in appsettings.json.</summary>
public class ProfessionInfo
{
    public string Name { get; set; } = "";
    /// <summary>Leveling guide on wow-professions.com.</summary>
    public string Guide { get; set; } = "";
    /// <summary>File name in wwwroot/recipes (without .json); empty for gathering professions.</summary>
    public string Recipes { get; set; } = "";
}

public class GuildDb(DbContextOptions<GuildDb> options) : DbContext(options)
{
    public DbSet<Character> Characters => Set<Character>();
    public DbSet<GuildEvent> Events => Set<GuildEvent>();
    public DbSet<Milestone> Milestones => Set<Milestone>();
    public DbSet<SiteText> Texts => Set<SiteText>();
    public DbSet<CharacterProfession> CharacterProfessions => Set<CharacterProfession>();
    public DbSet<KnownRecipe> KnownRecipes => Set<KnownRecipe>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<SiteText>().HasKey(t => t.Key);
    }
}

public record RosterEntry(string Name, string Class, string Race, int Level, string Slug);
public record LoginInput(string? Password);
public record ProfessionInput(string? Name, int Skill);
/// <summary>Professions: null leaves the character's professions as they are.</summary>
public record CharacterInput(string? Name, string? Class, string? Race, int Level, string? ArmoryUrl, string? Description, List<ProfessionInput>? Professions);
public record CraftersInput(string? Profession, List<int>? CharacterIds);
public record ProfessionSeed(string Name, Dictionary<string, int> Professions);
public record DescriptionInput(string? Description);
public record EventInput(string? Title, DateTime StartsAt, string? StartingPoint, string? Description, int? MinLevel, List<string>? Requirements);
public record MilestoneInput(DateOnly Date, string? Title, string? Description);
public record AboutInput(string? Text);
