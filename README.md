# Jawbreakers Guild

The website of Jawbreakers, a Horde guild on the EU PvP ruleset of World of Warcraft: Forever.

**Live:** https://lundqwist.se/jawbreakers/

![The front page of the guild site](docs/front-page.jpg)

## What's on it

| Page | What it shows |
|---|---|
| **Guild members** | One list per class. Pick a character to read its description and professions, or follow the link to its Armory page |
| **Events** | Upcoming and past guild events: date, time (realm time), starting point in game, description and requirements |
| **Crafting** | Every profession with the characters that have it and their skill level, a button to its leveling guide on [wow-professions.com](https://www.wow-professions.com/forever), and its recipes with materials, Wowhead tooltips and the characters who can make each one |
| **About** | A text about the guild and a history line of what it has achieved |
| **Admin** (`/admin.html`) | Add and edit events, characters (with their profession skill levels), the About text and the history. Signed in, the Crafting page also lets you tick who knows a recipe that doesn't come from a trainer |

There is one admin, who signs in with a password. Visitors don't sign in.

## Built with

- C#, ASP.NET Core 8 minimal API
- Entity Framework Core with SQLite
- Plain HTML, CSS and JavaScript in `wwwroot`: no framework and no build step
- Cookie sign-in for the admin; the password comes from an environment variable, never from a file in the repository
- Docker on a Synology NAS, behind the lundqwist.se site ([Lundqwist/Website2.0](https://github.com/Lundqwist/Website2.0)), which passes `/jawbreakers/...` on to this one

## Folders

| Path | What |
|---|---|
| `Program.cs` | Startup, sign-in and every API endpoint (`/api/...`) |
| `Data.cs` | The database tables and the shapes of what the API receives |
| `appsettings.json` | The classes, races, event requirements and professions (`Guild`) |
| `seed/` | The first members and their profession skill levels, imported into an empty database |
| `wwwroot/` | The pages, `css/site.css`, `js/common.js` (header, footer, helpers) and `recipes/` |
| `deploy/`, `deploy.ps1` | Docker compose file and the script that publishes to the NAS |

## Run locally

```
dotnet run --environment Development
```

Without an admin password set, a development run makes a temporary one and prints it in the log.
Set a fixed one with the `Admin__Password` environment variable. Outside development, admin sign-in is off until it is set.

The database is created in `data/` on first start.

## Where the data comes from

- **Members**: `seed/roster.json`, taken from the guild's page on the official Armory on 2026-11-14. Characters below level 10
  have no Armory page, so they have no link and no known race.
- **Profession skill levels**: `seed/professions.json`, from each character's Armory page on the same day.
- **Recipes**: `wwwroot/recipes/<profession>.json` holds every recipe of a profession: spell id, name, category, required skill, source,
  what it makes and the materials, plus the names and icons of the items involved. Taken from
  [Wowhead's WoW: Forever database](https://www.wowhead.com/forever) on 2026-11-14 (2,215 recipes). Each recipe that is learned from an item (a pattern, plans, formula and so on) also carries that item (`recipe`: id, name, quality), matched by name against Wowhead's recipe item lists; the page shows it as a link, and "Learned from trainer" for the others. The page links each recipe
  and material to Wowhead, whose `power.js` script shows the tooltips. The category (armor slot, weapon type, potion, enchant
  slot and so on) comes from the made item's type on Wowhead, or for enchants from the recipe name.

All three are one-time imports.

### Update from the Armory

The Guild members page has an **Update from the Armory** button that anyone can press, at most once an hour (the server
keeps the time of the last update in the database and refuses earlier presses). `ArmorySync.cs` then updates the site and lists what changed.

With a Blizzard API client it uses the official API (OAuth2 client credentials, namespace `profile-forever-eu`):

- the guild's member list: new members are added, members who have left are removed (with their professions and recipes);
- each character's level, class and race;
- each character's profession skill levels and the recipes it knows (characters below level 10 have no profile).

Create the client at https://develop.battle.net (API Access) and give the site its id and secret as the environment
variables `BLIZZARD_CLIENT_ID` and `BLIZZARD_CLIENT_SECRET` (on the NAS: in the `.env` file). They are never stored in the repository.

Without a client it reads the public Armory page of every character already on the site (level, class, race, skill levels)
and cannot add or remove members.

Who can make a recipe: once an update with the API has run, exactly the characters who know the recipe. Before that (or
without a client): for a trainer recipe, everyone with the profession and enough skill; for a recipe from a drop, vendor or
quest, only the characters the admin has ticked.

## Settings

`appsettings.json` → `Guild`: the class lists, the races, the requirement checkboxes on the admin page (`EventRequirements`),
and the professions with their guide links and recipe files (`Professions`).

| Setting | Environment variable | What |
|---|---|---|
| `Admin:Password` | `Admin__Password` | The admin password. Empty outside development = sign-in off |
| `DataDir` | `DataDir` | Folder for the database and the cookie keys (default `data/`) |
| `PathBase` | `PathBase` | The address the site lives under, for example `/jawbreakers` |

## Deploy

The site runs with `PathBase=/jawbreakers`. All addresses in `wwwroot` are relative, so the pages work both there and at the root locally.

`.\deploy.ps1` publishes to `Z:\jawbreakers\app` on the NAS; `deploy\docker-compose.yml` runs it there on `127.0.0.1:8095`.
The admin password goes in a `.env` file next to the compose file (see `deploy\.env.example`). Restart the `jawbreakers`
container afterwards. The `data` folder on the NAS is never touched by a deploy.

## Credits

World of Warcraft and the Armory belong to Blizzard Entertainment; this is a fan site and is not affiliated with Blizzard.
Recipe data, icons and tooltips come from Wowhead. The leveling guides are by wow-professions.com.
