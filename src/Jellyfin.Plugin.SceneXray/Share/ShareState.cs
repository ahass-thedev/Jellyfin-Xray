using System.Text.Json;
using Jellyfin.Plugin.SceneXray.Storage;

namespace Jellyfin.Plugin.SceneXray.Share;

/// <summary>
/// The membership, in <c>&lt;data&gt;/scenexray/share/install.json</c> (design §1): the install
/// token and the invite's Access service token. Nothing about the server goes into it.
/// </summary>
public sealed class Membership
{
    public string Token { get; set; } = string.Empty;

    public string AccessClientId { get; set; } = string.Empty;

    public string AccessClientSecret { get; set; } = string.Empty;

    public DateTime JoinedAt { get; set; }
}

/// <summary>One title waiting to be submitted (the background outbox).</summary>
public sealed class OutboxEntry
{
    public Guid ItemId { get; set; }

    public int Attempts { get; set; }

    public DateTime Due { get; set; }
}

/// <summary>A submission this install made, so names for its unknown faces find their way back.</summary>
public sealed class Contributed
{
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the timeline's signature when it was submitted: unknown ids belong to that analysis.</summary>
    public string Signature { get; set; } = string.Empty;

    public DateTime At { get; set; }
}

/// <summary>Files under <c>share/</c>: the membership, the outbox and cached fingerprints.</summary>
public sealed class ShareState
{
    private readonly object _lock = new();

    public ShareState(string root)
    {
        Root = root;
        Directory.CreateDirectory(Path.Combine(root, "outbox"));
        Directory.CreateDirectory(Path.Combine(root, "fp"));
    }

    public string Root { get; }

    private string MembershipPath => Path.Combine(Root, "install.json");

    public Membership? Membership
    {
        get
        {
            lock (_lock)
            {
                try
                {
                    return File.Exists(MembershipPath) ? JsonSerializer.Deserialize<Membership>(File.ReadAllText(MembershipPath), TimelineStore.Json) : null;
                }
                catch (JsonException)
                {
                    return null;
                }
            }
        }
    }

    public void SaveMembership(Membership m)
    {
        lock (_lock)
        {
            string tmp = MembershipPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(m, TimelineStore.Json));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            File.Move(tmp, MembershipPath, overwrite: true);
        }
    }

    public void DeleteMembership()
    {
        lock (_lock)
        {
            File.Delete(MembershipPath);
        }
    }

    private string OutboxPath(Guid id) => Path.Combine(Root, "outbox", id.ToString("N") + ".json");

    public void Enqueue(Guid itemId, DateTime due, int attempts = 0)
    {
        lock (_lock)
        {
            File.WriteAllText(OutboxPath(itemId), JsonSerializer.Serialize(new OutboxEntry { ItemId = itemId, Attempts = attempts, Due = due }, TimelineStore.Json));
        }
    }

    public void Dequeue(Guid itemId)
    {
        lock (_lock)
        {
            File.Delete(OutboxPath(itemId));
        }
    }

    public List<OutboxEntry> Outbox()
    {
        lock (_lock)
        {
            var list = new List<OutboxEntry>();
            foreach (string f in Directory.EnumerateFiles(Path.Combine(Root, "outbox"), "*.json"))
            {
                try
                {
                    if (JsonSerializer.Deserialize<OutboxEntry>(File.ReadAllText(f), TimelineStore.Json) is { } e)
                    {
                        list.Add(e);
                    }
                }
                catch (JsonException)
                {
                    File.Delete(f);
                }
            }

            return list;
        }
    }

    /// <summary>Turning sharing off discards queued submissions (design §5).</summary>
    public void ClearOutbox()
    {
        lock (_lock)
        {
            foreach (string f in Directory.EnumerateFiles(Path.Combine(Root, "outbox")))
            {
                File.Delete(f);
            }
        }
    }

    public string FingerprintDir => Path.Combine(Root, "fp");

    private string ContributedPath => Path.Combine(Root, "contributed.json");

    public Dictionary<Guid, Contributed> ContributedSubmissions()
    {
        lock (_lock)
        {
            try
            {
                return File.Exists(ContributedPath) ? JsonSerializer.Deserialize<Dictionary<Guid, Contributed>>(File.ReadAllText(ContributedPath), TimelineStore.Json) ?? [] : [];
            }
            catch (JsonException)
            {
                return [];
            }
        }
    }

    /// <summary>Remembers a submission; older ones of the same title are forgotten (their unknown ids are stale).</summary>
    public void RecordContribution(Guid submissionId, Guid itemId, string signature)
    {
        lock (_lock)
        {
            var map = ContributedSubmissions();
            foreach (var old in map.Where(kv => kv.Value.ItemId == itemId).Select(kv => kv.Key).ToList())
            {
                map.Remove(old);
            }

            map[submissionId] = new Contributed { ItemId = itemId, Signature = signature, At = DateTime.UtcNow };
            File.WriteAllText(ContributedPath, JsonSerializer.Serialize(map, TimelineStore.Json));
        }
    }

    private string UpdatesSincePath => Path.Combine(Root, "updates-since.txt");

    /// <summary>Gets or sets the last consensus revision the refresh has seen.</summary>
    public long UpdatesSince
    {
        get
        {
            lock (_lock)
            {
                return File.Exists(UpdatesSincePath) && long.TryParse(File.ReadAllText(UpdatesSincePath), out long v) ? v : 0;
            }
        }

        set
        {
            lock (_lock)
            {
                File.WriteAllText(UpdatesSincePath, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
    }

    /// <summary>After leaving: everything but the received timelines goes (they are this server's copy).</summary>
    public void ClearMembership()
    {
        lock (_lock)
        {
            File.Delete(MembershipPath);
            File.Delete(ContributedPath);
            File.Delete(UnknownsSincePath);
            File.Delete(UpdatesSincePath);
        }

        ClearOutbox();
    }

    private string UnknownsSincePath => Path.Combine(Root, "unknowns-since.txt");

    /// <summary>Gets or sets the last unknown-name revision applied.</summary>
    public long UnknownsSince
    {
        get
        {
            lock (_lock)
            {
                return File.Exists(UnknownsSincePath) && long.TryParse(File.ReadAllText(UnknownsSincePath), out long v) ? v : 0;
            }
        }

        set
        {
            lock (_lock)
            {
                File.WriteAllText(UnknownsSincePath, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
    }
}
