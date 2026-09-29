using System.Text.Json;

namespace SkillMultiplier;

/// <summary>
/// The client's report, kept on disk so a server restart does not empty the page.
/// <para>
/// The server cannot enumerate skill actions itself - they exist only inside the running client - so the
/// list on the page comes from a client's own claim. Held only in memory, restarting the server loses it and
/// every client row disappears until a game reconnects and re-reports.
/// </para>
/// <para>
/// <b>Display only.</b> Nothing about applying a multiplier reads this. A wrong or stale cache can make the
/// page show the wrong rows; it cannot make the game apply the wrong numbers.
/// </para>
/// <para>
/// <see cref="CapturedUtc"/> is load-bearing rather than informational. <c>ObservedXp</c> is a per-session
/// figure - the most the game granted before any multiplier - so carried across a restart it means "in a
/// previous session", which is a different quantity from "this session". The UI says so, and this is the
/// timestamp it says it with.
/// </para>
/// </summary>
public sealed record CachedClientReport
{
    public DateTimeOffset CapturedUtc { get; set; }

    public int Reporters { get; set; }

    public List<ClientActionEntry> Actions { get; set; } = [];
}

public static class ClientReportCache
{
    /// <summary>Read the kept report, or null if there is not one to read. Never throws.</summary>
    public static CachedClientReport? Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<CachedClientReport>(File.ReadAllText(path));
        }
        catch
        {
            // An unreadable cache costs the same as no cache: the page says no client has reported yet, which
            // is the state this file exists to avoid, not a failure worth stopping a boot for.
            return null;
        }
    }

    /// <summary>
    /// Write via a temp file and a move, so a crash mid-write cannot leave a truncated report that loads as a
    /// shorter - and quietly wrong - action list.
    /// </summary>
    public static void Save(string path, CachedClientReport report)
    {
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        var tmp = path + ".tmp";

        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    public static void Delete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
