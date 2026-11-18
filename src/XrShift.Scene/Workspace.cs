using System.Text.Json;
using System.Text.Json.Serialization;

namespace XrShift.Scene;

/// <summary>A named arrangement of screens, e.g. "Work" or "Movie".</summary>
public sealed record Profile
{
    public string Name { get; init; } = "Main";
    public Layout Layout { get; init; } = new();
}

/// <summary>All of the wearer's profiles and which one is in use. Saved as JSON.</summary>
public sealed record Workspace
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public IReadOnlyList<Profile> Profiles { get; init; } = [new Profile()];
    public int Active { get; init; }

    [JsonIgnore]
    public Profile Current => Profiles[Math.Clamp(Active, 0, Profiles.Count - 1)];

    /// <summary>Replaces the arrangement of the profile in use.</summary>
    public Workspace WithLayout(Layout layout) => ReplaceCurrent(Current with { Layout = layout });

    public Workspace Rename(string name) =>
        string.IsNullOrWhiteSpace(name) ? this : ReplaceCurrent(Current with { Name = name.Trim() });

    public Workspace Select(int index) =>
        index < 0 || index >= Profiles.Count ? this : this with { Active = index };

    /// <summary>Adds a copy of the profile in use under a new name and switches to it.</summary>
    public Workspace AddCopy(string? name = null)
    {
        string chosen = string.IsNullOrWhiteSpace(name) ? FreeName() : name.Trim();
        // Screens get new identities so the two profiles can be edited apart.
        Layout copy = Current.Layout with
        {
            Screens = Current.Layout.Screens.Select(s => s with { Id = Guid.NewGuid().ToString("N")[..8] }).ToList(),
        };
        return this with { Profiles = [.. Profiles, new Profile { Name = chosen, Layout = copy }], Active = Profiles.Count };
    }

    /// <summary>Removes the profile in use. The last one cannot be removed.</summary>
    public Workspace RemoveCurrent()
    {
        if (Profiles.Count <= 1) return this;
        int at = Math.Clamp(Active, 0, Profiles.Count - 1);
        List<Profile> rest = Profiles.Where((_, i) => i != at).ToList();
        return this with { Profiles = rest, Active = Math.Min(at, rest.Count - 1) };
    }

    Workspace ReplaceCurrent(Profile profile)
    {
        int at = Math.Clamp(Active, 0, Profiles.Count - 1);
        return this with { Profiles = Profiles.Select((p, i) => i == at ? profile : p).ToList(), Active = at };
    }

    string FreeName()
    {
        for (int n = Profiles.Count + 1; ; n++)
        {
            string name = $"Profile {n}";
            if (!Profiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))) return name;
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Reads a saved file. Also accepts the older kind that held a single arrangement.</summary>
    public static bool TryParse(string json, out Workspace workspace)
    {
        workspace = new Workspace();
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;

            if (!document.RootElement.TryGetProperty(nameof(Profiles), out _))
            {
                if (!Layout.TryParse(json, out Layout single)) return false;
                workspace = new Workspace { Profiles = [new Profile { Layout = single }] };
                return true;
            }

            Workspace? parsed = JsonSerializer.Deserialize<Workspace>(json, Json);
            if (parsed?.Profiles is null) return false;
            List<Profile> profiles = parsed.Profiles
                .Where(p => p is not null)
                .Select(p => new Profile
                {
                    Name = string.IsNullOrWhiteSpace(p.Name) ? "Profile" : p.Name.Trim(),
                    Layout = (p.Layout ?? new Layout()).Sanitized(),
                })
                .ToList();
            if (profiles.Count == 0) profiles.Add(new Profile());
            workspace = new Workspace { Profiles = profiles, Active = Math.Clamp(parsed.Active, 0, profiles.Count - 1) };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
