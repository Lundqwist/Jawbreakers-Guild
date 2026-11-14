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

public class GuildDb(DbContextOptions<GuildDb> options) : DbContext(options)
{
    public DbSet<Character> Characters => Set<Character>();
    public DbSet<GuildEvent> Events => Set<GuildEvent>();
    public DbSet<Milestone> Milestones => Set<Milestone>();
    public DbSet<SiteText> Texts => Set<SiteText>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<SiteText>().HasKey(t => t.Key);
    }
}

public record RosterEntry(string Name, string Class, string Race, int Level, string Slug);
public record LoginInput(string? Password);
public record CharacterInput(string? Name, string? Class, string? Race, int Level, string? ArmoryUrl, string? Description);
public record DescriptionInput(string? Description);
public record EventInput(string? Title, DateTime StartsAt, string? StartingPoint, string? Description, int? MinLevel, List<string>? Requirements);
public record MilestoneInput(DateOnly Date, string? Title, string? Description);
public record AboutInput(string? Text);
