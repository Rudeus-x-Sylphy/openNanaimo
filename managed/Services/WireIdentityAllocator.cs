using System.Collections.Concurrent;

namespace OpenNanaimo.Adapter.Services;

/// <summary>
/// Allocates protocol identities independently from persistent Character.Id values.
/// Persistent database keys are not wire identities: scene IDs are 12-bit and
/// character UIDs are 16-bit, so truncation/clamping would create collisions.
/// The allocator lives for the adapter process and is reset when a new adapter
/// service starts; every assigned value remains stable for that process.
/// </summary>
internal static class WireIdentityAllocator
{
    private const ushort SceneMaximum = 0x0FFF;
    private static readonly object Gate = new();
    private static readonly Dictionary<long, ushort> SceneByCharacter = [];
    private static readonly HashSet<ushort> UsedScene = [];
    private static readonly Dictionary<long, ushort> CharacterByDatabaseId = [];
    private static readonly HashSet<ushort> UsedCharacter = [];
    private static ushort _nextScene = 1;
    private static ushort _nextCharacter = 1;

    internal static void Reset()
    {
        lock (Gate)
        {
            SceneByCharacter.Clear();
            UsedScene.Clear();
            CharacterByDatabaseId.Clear();
            UsedCharacter.Clear();
            _nextScene = 1;
            _nextCharacter = 1;
        }
    }

    internal static ushort GetSceneEntityId(long characterId)
    {
        if (characterId <= 0) throw new ArgumentOutOfRangeException(nameof(characterId));
        lock (Gate)
        {
            if (SceneByCharacter.TryGetValue(characterId, out var existing))
                return existing;
            var assigned = Allocate(SceneMaximum, ref _nextScene, UsedScene, characterId);
            SceneByCharacter.Add(characterId, assigned);
            return assigned;
        }
    }

    internal static ushort GetCharacterUid(long characterId)
    {
        if (characterId <= 0) throw new ArgumentOutOfRangeException(nameof(characterId));
        lock (Gate)
        {
            if (CharacterByDatabaseId.TryGetValue(characterId, out var existing))
                return existing;
            var assigned = Allocate(ushort.MaxValue, ref _nextCharacter, UsedCharacter, characterId);
            CharacterByDatabaseId.Add(characterId, assigned);
            return assigned;
        }
    }

    private static ushort Allocate(ushort maximum, ref ushort cursor, HashSet<ushort> used, long characterId)
    {
        // Keep low database IDs readable/stable when no previous allocation has
        // claimed that value, while never using a database ID as the fallback.
        if (characterId <= maximum && characterId >= 1)
        {
            var preferred = (ushort)characterId;
            if (used.Add(preferred))
                return preferred;
        }

        var start = cursor;
        do
        {
            var candidate = cursor;
            cursor = cursor == maximum ? (ushort)1 : (ushort)(cursor + 1);
            if (used.Add(candidate))
                return candidate;
        }
        while (cursor != start);

        throw new InvalidOperationException($"No free wire identity remains in 1..{maximum} for character {characterId}.");
    }
}
