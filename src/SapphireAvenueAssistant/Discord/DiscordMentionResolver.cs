using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SapphireAvenueAssistant.Configuration;

namespace SapphireAvenueAssistant.Discord;

// A resolved "@Name" in a game line: the raw-content span it covers and the member it pings.
public readonly record struct GameMention(int Start, int Length, string UserId);

// Turns "@Name" typed in the CWLS into a real Discord ping when Name is an exact (case-insensitive)
// match for exactly one guild member's server nickname, display name or username. Names come from
// members seen in guild MESSAGE_CREATE events plus the member search endpoint, which needs no
// privileged intent. Anything unknown, ambiguous or slow stays plain text: resolution never fails
// or meaningfully delays a publication.
public sealed class DiscordMentionResolver(
    HttpClient httpClient,
    SapphireAvenueOptions options,
    TimeProvider timeProvider,
    ILogger<DiscordMentionResolver> logger)
{
    private const int MaximumPhraseWords = 4;
    private const int MaximumMentionTokens = 10;
    private const int MaximumPingedUsers = 5;
    private const int SearchLimit = 100;
    private const int MaximumObservedUsers = 5000;
    private const string TrailingPunctuation = ".,!?;:)]}'\"";
    private static readonly TimeSpan ResolutionBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SearchCacheLifetime = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan MaximumSearchBackoff = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, MemberNames> observed = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SearchEntry> searches = new(StringComparer.OrdinalIgnoreCase);
    private long searchBackoffUntilTicks;

    // Passive name source: the author and mentioned members of a guild message. Covers global_name,
    // which member search does not reliably match.
    public void Observe(JsonElement message)
    {
        if (!options.Discord.ResolveGameMentions ||
            !message.TryGetProperty("guild_id", out var guildId) ||
            guildId.ValueKind != JsonValueKind.String ||
            !string.Equals(guildId.GetString(), options.Discord.GuildId, StringComparison.Ordinal) ||
            message.TryGetProperty("webhook_id", out _))
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        if (message.TryGetProperty("author", out var author) &&
            message.TryGetProperty("member", out var member))
        {
            Remember(ReadMember(author, member), now);
        }

        if (message.TryGetProperty("mentions", out var mentions) && mentions.ValueKind == JsonValueKind.Array)
        {
            foreach (var mention in mentions.EnumerateArray())
            {
                // Gateway mentions carry a partial guild member under "member"; without it the
                // nick is unknown, and recording the user would wrongly clear a known nick.
                if (mention.ValueKind == JsonValueKind.Object &&
                    mention.TryGetProperty("member", out var mentionMember))
                {
                    Remember(ReadMember(mention, mentionMember), now);
                }
            }
        }

        TrimObserved();
    }

    public async Task<IReadOnlyList<GameMention>> ResolveAsync(
        string content,
        CancellationToken cancellationToken)
    {
        if (!options.Discord.ResolveGameMentions ||
            !options.Discord.CanPublish ||
            !content.Contains('@'))
        {
            return [];
        }

        var tokens = FindTokens(content);
        if (tokens.Count == 0)
        {
            return [];
        }

        var searched = new Dictionary<string, MemberNames[]>(StringComparer.OrdinalIgnoreCase);
        using (var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            budget.CancelAfter(ResolutionBudget);
            var queries = tokens
                .Select(token => token.SearchQuery)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var results = await Task.WhenAll(queries.Select(query => SearchAsync(query, budget.Token)));
            cancellationToken.ThrowIfCancellationRequested();
            for (var index = 0; index < queries.Length; index++)
            {
                searched[queries[index]] = results[index];
            }
        }

        var mentions = new List<GameMention>();
        var pinged = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in tokens)
        {
            var candidates = observed.Values.Concat(searched[token.SearchQuery]);
            if (Match(content, token, candidates) is not { } mention)
            {
                continue;
            }

            if (!pinged.Contains(mention.UserId))
            {
                if (pinged.Count >= MaximumPingedUsers)
                {
                    continue;
                }

                pinged.Add(mention.UserId);
            }

            mentions.Add(mention);
        }

        return mentions;
    }

    private static GameMention? Match(string content, MentionToken token, IEnumerable<MemberNames> candidates)
    {
        var members = candidates.ToArray();
        for (var wordCount = token.Words.Count; wordCount >= 1; wordCount--)
        {
            var end = token.Words[wordCount - 1].End;
            var phrase = content[token.Words[0].Start..end];

            // "@Fran," means Fran; a name that really ends in punctuation still matches as typed.
            foreach (var (text, length) in Variants(phrase))
            {
                var name = NormalizeName(text);
                if (name.Length == 0)
                {
                    continue;
                }

                var matches = members
                    .Where(member => member.Matches(name))
                    .Select(member => member.UserId)
                    .Distinct(StringComparer.Ordinal)
                    .Take(2)
                    .ToArray();
                if (matches.Length == 1)
                {
                    return new GameMention(token.AtIndex, token.Words[0].Start - token.AtIndex + length, matches[0]);
                }

                if (matches.Length > 1)
                {
                    // Ambiguous: never fall back to a shorter phrase that might pick someone else.
                    return null;
                }
            }
        }

        return null;
    }

    private static IEnumerable<(string Text, int Length)> Variants(string phrase)
    {
        yield return (phrase, phrase.Length);
        var trimmed = phrase.TrimEnd(TrailingPunctuation.ToCharArray());
        if (trimmed.Length != phrase.Length && trimmed.Length > 0)
        {
            yield return (trimmed, trimmed.Length);
        }
    }

    private static List<MentionToken> FindTokens(string content)
    {
        var tokens = new List<MentionToken>();
        for (var index = 0; index < content.Length && tokens.Count < MaximumMentionTokens; index++)
        {
            if (content[index] != '@')
            {
                continue;
            }

            // Only "@" at the start or after whitespace/punctuation: "fran@example.com" is not a
            // mention, and raw "<@id>" markup is left to render inertly.
            if (index > 0 && (char.IsLetterOrDigit(content[index - 1]) || content[index - 1] == '<'))
            {
                continue;
            }

            var words = new List<(int Start, int End)>();
            var position = index + 1;
            while (words.Count < MaximumPhraseWords && position < content.Length && !char.IsWhiteSpace(content[position]))
            {
                var start = position;
                while (position < content.Length && !char.IsWhiteSpace(content[position]))
                {
                    position++;
                }

                words.Add((start, position));
                while (position < content.Length && char.IsWhiteSpace(content[position]))
                {
                    position++;
                }
            }

            if (words.Count == 0)
            {
                continue;
            }

            var firstWord = content[words[0].Start..words[0].End].TrimEnd(TrailingPunctuation.ToCharArray());
            if (firstWord.Length == 0 || firstWord.Contains('@'))
            {
                continue;
            }

            tokens.Add(new MentionToken(index, words, firstWord.Length > 100 ? firstWord[..100] : firstWord));
        }

        return tokens;
    }

    private async Task<MemberNames[]> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (searches.TryGetValue(query, out var cached) && cached.ExpiresAt > now)
        {
            return cached.Members;
        }

        if (now.UtcTicks < Interlocked.Read(ref searchBackoffUntilTicks))
        {
            return [];
        }

        var relativePath =
            $"guilds/{Uri.EscapeDataString(options.Discord.GuildId)}/members/search" +
            $"?query={Uri.EscapeDataString(query)}&limit={SearchLimit}";
        using var request = new HttpRequestMessage(HttpMethod.Get, relativePath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bot", options.Discord.BotToken);
        request.Headers.UserAgent.ParseAdd("Sapphire-Avenue-Discord-Bridge/1.0");

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfter = await DiscordApiClient.ReadRetryAfterAsync(response, cancellationToken);
                var delay = retryAfter is { } value && value > TimeSpan.Zero
                    ? (value <= MaximumSearchBackoff ? value : MaximumSearchBackoff)
                    : TimeSpan.FromSeconds(5);
                Interlocked.Exchange(ref searchBackoffUntilTicks, (now + delay).UtcTicks);
                logger.LogDebug("Discord member search is rate-limited for {Delay}; game mentions use cached names.", delay);
                return [];
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Discord member search returned HTTP {Status}; game mentions use cached names only.",
                    (int)response.StatusCode);
                return [];
            }

            using var payload = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            if (payload.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var members = payload.RootElement
                .EnumerateArray()
                .Where(member => member.ValueKind == JsonValueKind.Object && member.TryGetProperty("user", out _))
                .Select(member => ReadMember(member.GetProperty("user"), member))
                .OfType<MemberNames>()
                .ToArray();
            foreach (var member in members)
            {
                Remember(member, now);
            }

            searches[query] = new SearchEntry(now + SearchCacheLifetime, members);
            if (searches.Count > 500)
            {
                foreach (var expired in searches.Where(entry => entry.Value.ExpiresAt <= now).ToArray())
                {
                    searches.TryRemove(expired.Key, out _);
                }
            }

            return members;
        }
        catch (OperationCanceledException)
        {
            // Budget expiry or HttpClient timeout. The caller rethrows real shutdown cancellation
            // once every search has settled; a search itself only ever reports "no result".
            logger.LogDebug("Discord member search for a game mention timed out.");
            return [];
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            logger.LogWarning(exception, "Discord member search failed; game mentions use cached names only.");
            return [];
        }
    }

    private void Remember(MemberNames? member, DateTimeOffset now)
    {
        if (member is null)
        {
            return;
        }

        observed[member.UserId] = member with { SeenAt = now };
    }

    private void TrimObserved()
    {
        if (observed.Count <= MaximumObservedUsers)
        {
            return;
        }

        foreach (var stale in observed.Values
            .OrderBy(member => member.SeenAt)
            .Take(observed.Count - (MaximumObservedUsers * 9 / 10))
            .ToArray())
        {
            observed.TryRemove(stale.UserId, out _);
        }
    }

    private static MemberNames? ReadMember(JsonElement user, JsonElement member)
    {
        if (user.ValueKind != JsonValueKind.Object ||
            !user.TryGetProperty("id", out var id) ||
            id.ValueKind != JsonValueKind.String ||
            !DiscordOptions.IsSnowflake(id.GetString() ?? string.Empty) ||
            (user.TryGetProperty("bot", out var bot) && bot.ValueKind == JsonValueKind.True) ||
            (user.TryGetProperty("system", out var system) && system.ValueKind == JsonValueKind.True))
        {
            return null;
        }

        return new MemberNames(
            id.GetString()!,
            member.ValueKind == JsonValueKind.Object ? ReadName(member, "nick") : null,
            ReadName(user, "global_name"),
            ReadName(user, "username"),
            default);
    }

    private static string? ReadName(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? NormalizeName(value.GetString() ?? string.Empty) is { Length: > 0 } name ? name : null
            : null;

    private static string NormalizeName(string value)
    {
        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private sealed record MemberNames(
        string UserId,
        string? Nick,
        string? GlobalName,
        string? Username,
        DateTimeOffset SeenAt)
    {
        public bool Matches(string name) =>
            string.Equals(Nick, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(GlobalName, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Username, name, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record SearchEntry(DateTimeOffset ExpiresAt, MemberNames[] Members);

    private sealed record MentionToken(int AtIndex, List<(int Start, int End)> Words, string SearchQuery);
}
