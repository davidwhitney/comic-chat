namespace ComicChat.Irc;

/// <summary>Port of the CM_* channel mode flags (defines.h:173-182).</summary>
[Flags]
public enum ChannelModes
{
    None = 0,
    Private = 1,
    Hidden = 2,
    InviteOnly = 4,
    TopicHost = 8,
    NoExtern = 16,
    Moderated = 32,
    UserLimit = 64,
    ChannelKey = 128,
    NoFormat = 256,
    Mic = 512,
}

/// <summary>
/// A channel and its membership. Port of CRoomInfo (roomlist.h) as used by CIrcProto.
/// </summary>
public sealed class RoomInfo(string name)
{
    private readonly Dictionary<string, UserInfo> _users = new(StringComparer.OrdinalIgnoreCase);

    public string Name { get; set; } = name;
    public string? Topic { get; set; }
    public string? Key { get; set; }
    public ChannelModes Modes { get; set; }
    public int UserLimit { get; set; }

    public IReadOnlyCollection<UserInfo> Users => _users.Values;

    public UserInfo? Find(string nick) => _users.GetValueOrDefault(nick);

    /// <summary>Get an existing user or add a new one. Mirrors LookupPui's add-if-missing behaviour.</summary>
    public UserInfo GetOrAdd(string nick, string? fullName = null)
    {
        if (_users.TryGetValue(nick, out var existing))
        {
            if (fullName is not null) existing.FullName = fullName;
            return existing;
        }

        var user = new UserInfo(nick, fullName);
        _users[nick] = user;
        return user;
    }

    public bool Remove(string nick) => _users.Remove(nick);

    public void Rename(string oldNick, string newNick)
    {
        if (!_users.Remove(oldNick, out var user)) return;
        user.Name = newNick;
        _users[newNick] = user;
    }

    public void Clear() => _users.Clear();

    /// <summary>
    /// Add a nick from a NAMES (353) reply, decoding its leading status character.
    /// Port of the SC_* status prefixes (defines.h:105-108).
    /// </summary>
    public UserInfo AddFromNames(string entry)
    {
        var flags = UserFlags.None;
        int i = 0;

        // A nick may carry more than one status prefix, so consume all of them.
        while (i < entry.Length)
        {
            switch (entry[i])
            {
                case '.': flags |= UserFlags.Owner; break;      // SC_OWNER
                case '@': flags |= UserFlags.Operator; break;   // SC_HOST
                case '>': flags |= UserFlags.Spectator; break;  // SC_SPECTATOR
                case '+': flags |= UserFlags.HasVoice; break;   // SC_HASVOICE
                default: goto done;
            }
            i++;
        }

    done:
        var user = GetOrAdd(entry[i..]);
        user.Flags |= flags;
        return user;
    }

    public override string ToString() => Name;
}
