using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace EncounterTimeline;

public enum CactbotEventType
{
    Unknown,
    StartsUsing,
    Ability,
    GainsEffect,
    LosesEffect,
    HeadMarker,
    Tether,
    AddedCombatant,
    CombatantMemory,
    ActorControlExtra,
    ActorSetPos,
    SpawnNpcExtra,
    AbilityExtra
}

[Flags]
public enum TimelineStateHint
{
    None = 0,
    Raidwide = 1 << 0,
    Tankbuster = 1 << 1,
    Knockback = 1 << 2
}

public sealed record CactbotTrigger(
    int ZoneID,
    string TriggerSetID,
    string TriggerID,
    CactbotEventType EventType,
    IReadOnlyList<uint> IDs,
    IReadOnlyList<string> Sources,
    IReadOnlyList<uint> NpcBaseIDs,
    TimelineStateHint StateHint,
    bool HasDynamicFilter,
    string FileName,
    int LineNumber);

public sealed class CactbotTriggerCatalog
{
    private static readonly Regex ZoneEntry = new("'(?<name>[^']+)'\\s*:\\s*(?<id>\\d+)", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex TriggerSetDeclaration = new(@"\bconst\s+triggerSet(?:\s*:[^=]+)?\s*=\s*\{", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ConstObjectDeclaration = new(@"\bconst\s+(?<name>[A-Za-z_$][A-Za-z0-9_$]*)(?:\s*:[^=]+)?\s*=\s*\{", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ConstArrayDeclaration = new(@"\bconst\s+(?<name>[A-Za-z_$][A-Za-z0-9_$]*)(?:\s*:[^=]+)?\s*=\s*\[", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ConstScalarDeclaration = new(@"\bconst\s+(?<name>[A-Za-z_$][A-Za-z0-9_$]*)(?:\s*:[^=]+)?\s*=\s*'(?<value>[^']*)'", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ObjectStringEntry = new(@"(?:'(?<quoted>[^']+)'|(?<bare>[A-Za-z_$][A-Za-z0-9_$]*))\s*:\s*'(?<value>[^']+)'", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex QuotedString = new("'(?<value>[^']*)'|\\\"(?<double>[^\\\"]*)\\\"", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ZoneReference = new(@"\bZoneId\.(?<name>[A-Za-z0-9_]+)", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ObjectKeys = new(@"^Object\.keys\((?<name>[A-Za-z_$][A-Za-z0-9_$]*)\)$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex MemberReference = new("^(?<name>[A-Za-z_$][A-Za-z0-9_$]*)\\[['\\\"](?<key>[^'\\\"]+)['\\\"]\\]$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Identifier = new(@"^[A-Za-z_$][A-Za-z0-9_$]*$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private CactbotTriggerCatalog(string rootDirectory, IReadOnlyList<CactbotTrigger> triggers)
    {
        RootDirectory = rootDirectory;
        Triggers = triggers;
        TriggersByZone = triggers
            .GroupBy(trigger => trigger.ZoneID)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<CactbotTrigger>)group.OrderBy(trigger => trigger.FileName, StringComparer.OrdinalIgnoreCase).ThenBy(trigger => trigger.LineNumber).ToArray());
    }

    public string RootDirectory { get; }
    public IReadOnlyList<CactbotTrigger> Triggers { get; }
    public IReadOnlyDictionary<int, IReadOnlyList<CactbotTrigger>> TriggersByZone { get; }

    public IReadOnlyList<CactbotTrigger> TriggersForZone(int zoneID)
        => TriggersByZone.GetValueOrDefault(zoneID, []);

    public static CactbotTriggerCatalog Load(string cactbotDirectory)
    {
        var root = Path.GetFullPath(cactbotDirectory);
        var zonePath = Path.Combine(root, "resources", "zone_id.ts");
        var dataDirectory = Path.Combine(root, "ui", "raidboss", "data");
        if (!File.Exists(zonePath) || !Directory.Exists(dataDirectory))
            throw new DirectoryNotFoundException($"A cactbot source checkout was not found below '{root}'.");

        var zones = ZoneEntry.Matches(File.ReadAllText(zonePath))
            .ToDictionary(match => match.Groups["name"].Value, match => int.Parse(match.Groups["id"].Value, CultureInfo.InvariantCulture), StringComparer.Ordinal);
        List<CactbotTrigger> triggers = [];
        foreach (var path in Directory.EnumerateFiles(dataDirectory, "*.ts", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            triggers.AddRange(ParseTriggerFile(root, path, zones));
        return new(root, triggers);
    }

    private static IEnumerable<CactbotTrigger> ParseTriggerFile(string root, string path, IReadOnlyDictionary<string, int> zones)
    {
        var text = File.ReadAllText(path);
        var declaration = TriggerSetDeclaration.Match(text);
        if (!declaration.Success)
            yield break;

        var objectStart = declaration.Index + declaration.Length - 1;
        var objectEnd = FindMatching(text, objectStart, '{', '}');
        if (objectEnd < 0)
            throw new FormatException($"Unterminated trigger set in '{path}'.");
        var triggerSet = text.Substring(objectStart + 1, objectEnd - objectStart - 1);
        var properties = ParseProperties(triggerSet);
        if (!properties.TryGetValue("zoneId", out var zoneExpression) || !properties.TryGetValue("triggers", out var triggerExpression))
            yield break;

        var zoneIDs = ZoneReference.Matches(zoneExpression)
            .Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .Where(zones.ContainsKey)
            .Select(name => zones[name])
            .ToArray();
        if (zoneIDs.Length == 0)
            yield break;

        var triggerSetID = LiteralStrings(properties.GetValueOrDefault("id", "")).FirstOrDefault() ?? Path.GetFileNameWithoutExtension(path);
        var symbols = StaticSymbols.Parse(text);
        var arrayStart = triggerExpression.IndexOf('[');
        if (arrayStart < 0)
            yield break;
        var arrayEnd = FindMatching(triggerExpression, arrayStart, '[', ']');
        if (arrayEnd < 0)
            throw new FormatException($"Unterminated triggers array in '{path}'.");

        foreach (var triggerObject in TopLevelObjects(triggerExpression.Substring(arrayStart + 1, arrayEnd - arrayStart - 1)))
        {
            var triggerProperties = ParseProperties(triggerObject.Text);
            var triggerID = LiteralStrings(triggerProperties.GetValueOrDefault("id", "")).FirstOrDefault();
            var typeText = LiteralStrings(triggerProperties.GetValueOrDefault("type", "")).FirstOrDefault();
            if (triggerID == null || typeText == null || !Enum.TryParse<CactbotEventType>(typeText, out var eventType))
                continue;
            if (!triggerProperties.TryGetValue("netRegex", out var netRegexExpression))
                continue;

            var netRegexStart = netRegexExpression.IndexOf('{');
            if (netRegexStart < 0)
                continue;
            var netRegexEnd = FindMatching(netRegexExpression, netRegexStart, '{', '}');
            if (netRegexEnd < 0)
                continue;
            var filters = ParseProperties(netRegexExpression.Substring(netRegexStart + 1, netRegexEnd - netRegexStart - 1));
            var identifierField = eventType switch
            {
                CactbotEventType.GainsEffect or CactbotEventType.LosesEffect => "effectId",
                CactbotEventType.Tether => filters.ContainsKey("tetherId") ? "tetherId" : "id",
                _ => "id"
            };
            var ids = ResolveNumeric(filters.GetValueOrDefault(identifierField), symbols, NumericKind.Hex);
            var sources = ResolveStrings(filters.GetValueOrDefault("source"), symbols);
            var npcBaseIDs = ResolveNumeric(filters.GetValueOrDefault("npcBaseId"), symbols, NumericKind.DecimalUnlessHexLetter);
            var stateHint = ClassifyStateHint(triggerID, triggerProperties.GetValueOrDefault("response", ""));
            var dynamic = filters.Any(property => property.Key != "capture" && ResolveStrings(property.Value, symbols).Count == 0)
                || filters.ContainsKey(identifierField) && ids.Count == 0
                || filters.ContainsKey("npcBaseId") && npcBaseIDs.Count == 0;
            var lineNumber = 1 + text.AsSpan(0, objectStart + 1 + triggerObject.Offset).Count('\n');
            var relativePath = Path.GetRelativePath(root, path).Replace('\\', '/');
            foreach (var zoneID in zoneIDs)
                yield return new(zoneID, triggerSetID, triggerID, eventType, ids, sources, npcBaseIDs, stateHint, dynamic, relativePath, lineNumber);
        }
    }

    private static TimelineStateHint ClassifyStateHint(string triggerID, string response)
    {
        var result = TimelineStateHint.None;
        var responseKinds = Regex.Matches(response, @"\bResponses\.(?<kind>aoe|bigAoe|tankBuster|sharedTankBuster|tankBusterSwap|knockback|knockbackOn)\s*\(", RegexOptions.CultureInvariant)
            .Select(match => match.Groups["kind"].Value);
        foreach (var kind in responseKinds)
        {
            result |= kind switch
            {
                "aoe" or "bigAoe" => TimelineStateHint.Raidwide,
                "tankBuster" or "sharedTankBuster" or "tankBusterSwap" => TimelineStateHint.Tankbuster,
                "knockback" or "knockbackOn" => TimelineStateHint.Knockback,
                _ => TimelineStateHint.None
            };
        }

        var normalizedID = new string(triggerID.Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        if (normalizedID.Contains("raidwide", StringComparison.Ordinal))
            result |= TimelineStateHint.Raidwide;
        if (normalizedID.Contains("tankbuster", StringComparison.Ordinal))
            result |= TimelineStateHint.Tankbuster;
        if (normalizedID.Contains("knockback", StringComparison.Ordinal))
            result |= TimelineStateHint.Knockback;
        return result;
    }

    private enum NumericKind
    {
        Hex,
        DecimalUnlessHexLetter
    }

    private static IReadOnlyList<uint> ResolveNumeric(string? expression, StaticSymbols symbols, NumericKind kind)
    {
        if (expression == null)
            return [];
        List<uint> result = [];
        foreach (var value in ResolveStrings(expression, symbols))
        {
            var numberStyle = kind == NumericKind.Hex || value.Any(character => character is >= 'A' and <= 'F' or >= 'a' and <= 'f')
                ? NumberStyles.AllowHexSpecifier
                : NumberStyles.None;
            if (uint.TryParse(value, numberStyle, CultureInfo.InvariantCulture, out var parsed))
                result.Add(parsed);
        }
        return result.Distinct().ToArray();
    }

    private static IReadOnlyList<string> ResolveStrings(string? expression, StaticSymbols symbols)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return [];
        var trimmed = expression.Trim();
        var literals = LiteralStrings(trimmed);
        if (literals.Count > 0)
            return literals;

        var objectKeys = ObjectKeys.Match(trimmed);
        if (objectKeys.Success && symbols.ObjectMembers.TryGetValue(objectKeys.Groups["name"].Value, out var members))
            return members.Keys.ToArray();
        var member = MemberReference.Match(trimmed);
        if (member.Success && symbols.ObjectMembers.TryGetValue(member.Groups["name"].Value, out members) && members.TryGetValue(member.Groups["key"].Value, out var memberValue))
            return [memberValue];
        if (Identifier.IsMatch(trimmed) && symbols.Values.TryGetValue(trimmed, out var values))
            return values;
        return [];
    }

    private static IReadOnlyList<string> LiteralStrings(string expression)
        => QuotedString.Matches(expression)
            .Select(match => match.Groups["value"].Success ? match.Groups["value"].Value : match.Groups["double"].Value)
            .ToArray();

    private sealed class StaticSymbols
    {
        public Dictionary<string, IReadOnlyList<string>> Values { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, IReadOnlyDictionary<string, string>> ObjectMembers { get; } = new(StringComparer.Ordinal);

        public static StaticSymbols Parse(string text)
        {
            var result = new StaticSymbols();
            foreach (Match match in ConstScalarDeclaration.Matches(text))
                result.Values[match.Groups["name"].Value] = [match.Groups["value"].Value];
            foreach (Match match in ConstArrayDeclaration.Matches(text))
            {
                var start = match.Index + match.Length - 1;
                var end = FindMatching(text, start, '[', ']');
                if (end < 0)
                    continue;
                var values = LiteralStrings(text.Substring(start + 1, end - start - 1));
                if (values.Count > 0)
                    result.Values[match.Groups["name"].Value] = values;
            }
            foreach (Match match in ConstObjectDeclaration.Matches(text))
            {
                var start = match.Index + match.Length - 1;
                var end = FindMatching(text, start, '{', '}');
                if (end < 0)
                    continue;
                Dictionary<string, string> members = new(StringComparer.Ordinal);
                foreach (Match entry in ObjectStringEntry.Matches(text.Substring(start + 1, end - start - 1)))
                {
                    var key = entry.Groups["quoted"].Success ? entry.Groups["quoted"].Value : entry.Groups["bare"].Value;
                    members[key] = entry.Groups["value"].Value;
                }
                if (members.Count > 0)
                    result.ObjectMembers[match.Groups["name"].Value] = members;
            }
            return result;
        }
    }

    private readonly record struct ObjectSlice(string Text, int Offset);

    private static IEnumerable<ObjectSlice> TopLevelObjects(string text)
    {
        var index = 0;
        while (index < text.Length)
        {
            index = SkipTrivia(text, index);
            if (index >= text.Length)
                yield break;
            if (text[index] != '{')
            {
                ++index;
                continue;
            }
            var end = FindMatching(text, index, '{', '}');
            if (end < 0)
                yield break;
            yield return new(text.Substring(index + 1, end - index - 1), index);
            index = end + 1;
        }
    }

    private static Dictionary<string, string> ParseProperties(string text)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        var segmentStart = 0;
        foreach (var comma in TopLevelSeparators(text, ','))
        {
            AddProperty(text.AsSpan(segmentStart, comma - segmentStart), result);
            segmentStart = comma + 1;
        }
        AddProperty(text.AsSpan(segmentStart), result);
        return result;
    }

    private static void AddProperty(ReadOnlySpan<char> segment, IDictionary<string, string> result)
    {
        var raw = segment.ToString();
        var value = raw[SkipTrivia(raw, 0)..].Trim();
        if (value.Length == 0)
            return;
        var colon = TopLevelSeparators(value, ':').FirstOrDefault(-1);
        if (colon < 0)
            return;
        var key = value[..colon].Trim();
        if (Identifier.IsMatch(key))
            result[key] = value[(colon + 1)..].Trim();
    }

    private static IEnumerable<int> TopLevelSeparators(string text, char separator)
    {
        var braces = 0;
        var brackets = 0;
        var parentheses = 0;
        var state = LexState.Code;
        for (var index = 0; index < text.Length; ++index)
        {
            var character = text[index];
            if (AdvanceLexState(text, ref index, ref state))
                continue;
            switch (character)
            {
                case '{': ++braces; break;
                case '}': --braces; break;
                case '[': ++brackets; break;
                case ']': --brackets; break;
                case '(': ++parentheses; break;
                case ')': --parentheses; break;
                default:
                    if (character == separator && braces == 0 && brackets == 0 && parentheses == 0)
                        yield return index;
                    break;
            }
        }
    }

    private enum LexState
    {
        Code,
        SingleQuote,
        DoubleQuote,
        Template,
        LineComment,
        BlockComment
    }

    private static int FindMatching(string text, int start, char open, char close)
    {
        var depth = 0;
        var state = LexState.Code;
        for (var index = start; index < text.Length; ++index)
        {
            var character = text[index];
            if (AdvanceLexState(text, ref index, ref state))
                continue;
            if (character == open)
                ++depth;
            else if (character == close && --depth == 0)
                return index;
        }
        return -1;
    }

    private static bool AdvanceLexState(string text, ref int index, ref LexState state)
    {
        var character = text[index];
        if (state == LexState.Code)
        {
            if (character == '/' && index + 1 < text.Length && text[index + 1] == '/')
            {
                state = LexState.LineComment;
                ++index;
                return true;
            }
            if (character == '/' && index + 1 < text.Length && text[index + 1] == '*')
            {
                state = LexState.BlockComment;
                ++index;
                return true;
            }
            state = character switch
            {
                '\'' => LexState.SingleQuote,
                '"' => LexState.DoubleQuote,
                '`' => LexState.Template,
                _ => state
            };
            return state != LexState.Code;
        }

        if (state == LexState.LineComment)
        {
            if (character is '\r' or '\n')
                state = LexState.Code;
            return true;
        }
        if (state == LexState.BlockComment)
        {
            if (character == '*' && index + 1 < text.Length && text[index + 1] == '/')
            {
                state = LexState.Code;
                ++index;
            }
            return true;
        }
        if (character == '\\')
        {
            ++index;
            return true;
        }
        if (state == LexState.SingleQuote && character == '\'' || state == LexState.DoubleQuote && character == '"' || state == LexState.Template && character == '`')
            state = LexState.Code;
        return true;
    }

    private static int SkipTrivia(string text, int index)
    {
        while (index < text.Length)
        {
            if (char.IsWhiteSpace(text[index]) || text[index] == ',')
            {
                ++index;
                continue;
            }
            if (text[index] == '/' && index + 1 < text.Length && text[index + 1] == '/')
            {
                index = text.IndexOf('\n', index + 2);
                if (index < 0)
                    return text.Length;
                continue;
            }
            if (text[index] == '/' && index + 1 < text.Length && text[index + 1] == '*')
            {
                var end = text.IndexOf("*/", index + 2, StringComparison.Ordinal);
                if (end < 0)
                    return text.Length;
                index = end + 2;
                continue;
            }
            break;
        }
        return index;
    }
}
