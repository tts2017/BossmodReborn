using System.IO;
using System.Text.Json;

using static FFXIVClientStructs.FFXIV.Client.Game.InstanceContent.InstanceContentDeepDungeon;

namespace BossMod.Global.DeepDungeon;

abstract partial class AutoClear : ZoneModule
{
    private sealed class LiveMapPoint
    {
        public float X { get; set; }
        public float Z { get; set; }
        public int Samples { get; set; }
    }

    private sealed class LiveMapRoom
    {
        public LiveMapPoint? North { get; set; }
        public LiveMapPoint? South { get; set; }
        public LiveMapPoint? West { get; set; }
        public LiveMapPoint? East { get; set; }
        public bool HasVisitedBounds { get; set; }
        public float MinX { get; set; }
        public float MinZ { get; set; }
        public float MaxX { get; set; }
        public float MaxZ { get; set; }
    }

    private static Dictionary<string, Dictionary<int, LiveMapRoom>>? _liveMapData;
    private string _liveMapObservationKey = "";
    private int _liveMapObservedRoom = -1;
    private WPos _liveMapLastPosition;
    private bool _liveMapInConnection;
    private int _liveMapConnectionFromRoom = -1;
    private WPos _liveMapConnectionExitPosition;
    private volatile bool _liveMapDirty; // cleared on the framework thread, set again by a failed background write
    // one options instance: a fresh one per save rebuilds the serializer's type metadata every time (saves run every few seconds)
    private static readonly JsonSerializerOptions LiveMapJsonOptions = new() { WriteIndented = true };
    private DateTime _liveMapNextSaveAt;
    private static Task _liveMapWriteTask = Task.CompletedTask; // chained so that background writes never overlap

    private string CurrentLiveMapKey => $"{(int)Palace.DungeonId}.{Palace.Floor / 10 + 1}.{Palace.Progress.Tileset}";
    private static string LiveMapDataPath => Path.Combine(Service.PluginInterface.ConfigDirectory.FullName, "DeepDungeonMapObservations.json");

    private static Dictionary<string, Dictionary<int, LiveMapRoom>> LiveMapData()
    {
        if (_liveMapData != null)
            return _liveMapData;

        try
        {
            if (File.Exists(LiveMapDataPath))
            {
                var json = File.ReadAllText(LiveMapDataPath);
                _liveMapData = JsonSerializer.Deserialize<Dictionary<string, Dictionary<int, LiveMapRoom>>>(json);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (JsonException)
        {
        }

        WarmLiveMapSerializer();
        return _liveMapData ??= [];
    }

    private static int _liveMapSerializerWarmStarted;

    // The first serialization with LiveMapJsonOptions builds its converters: about 30 ms (measured in the deep dungeon harness,
    // later saves take 0.1 ms), which landed on the framework thread at the first throttled save of every session. Build them in
    // the background when the data is first loaded instead; the options' type cache is thread-safe.
    private static void WarmLiveMapSerializer()
    {
        if (System.Threading.Interlocked.Exchange(ref _liveMapSerializerWarmStarted, 1) != 0)
            return;
        _ = Task.Run(() =>
        {
            try
            {
                JsonSerializer.Serialize(new Dictionary<string, Dictionary<int, LiveMapRoom>> { [""] = new() { [0] = new() { North = new() } } }, LiveMapJsonOptions);
            }
            catch (Exception)
            {
                // only a warm-up: the real save reports nothing either and simply retries later
            }
        });
    }

    private void SaveLiveMapData(bool synchronous = false)
    {
        if (!_liveMapDirty || _liveMapData == null)
            return;

        // serialize on the framework thread (the data is only mutated there) and hand the file write to a background task
        var json = JsonSerializer.Serialize(_liveMapData, LiveMapJsonOptions);
        var path = LiveMapDataPath;
        _liveMapDirty = false;
        if (synchronous)
        {
            try
            {
                _liveMapWriteTask.Wait();
            }
            catch (AggregateException)
            {
            }
            if (!WriteLiveMapFile(path, json))
                _liveMapDirty = true;
            return;
        }

        _liveMapWriteTask = _liveMapWriteTask.ContinueWith(_ =>
        {
            if (!WriteLiveMapFile(path, json))
                _liveMapDirty = true; // retry on the next throttled save
        }, TaskScheduler.Default);
    }

    private static bool WriteLiveMapFile(string path, string json)
    {
        var temporaryPath = path + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, path, true);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void SaveLiveMapDataThrottled()
    {
        if (World.CurrentTime < _liveMapNextSaveAt)
            return;

        _liveMapNextSaveAt = World.FutureTime(5);
        SaveLiveMapData();
    }

    private void ApplyLiveMapData()
    {
        if (Palace.DungeonId != DeepDungeonState.DungeonType.POTD
            || !LiveMapData().TryGetValue(CurrentLiveMapKey, out var learnedRooms))
            return;

        foreach (var (roomIndex, learned) in learnedRooms)
        {
            if ((uint)roomIndex >= (uint)Palace.Rooms.Length || (byte)Palace.Rooms[roomIndex] == 0)
                continue;

            var hasExistingRoom = RoomNavigation.TryGetValue(roomIndex, out var room);
            var learnedCenter = LearnedCenter(learned);
            if (!hasExistingRoom && learnedCenter == default)
                continue;

            room ??= new(default, default, default, default, default);
            RoomNavigation[roomIndex] = room with
            {
                Center = room.Center != default ? room.Center : learnedCenter,
                North = room.North != default ? room.North : LearnedWall(learned.North),
                South = room.South != default ? room.South : LearnedWall(learned.South),
                West = room.West != default ? room.West : LearnedWall(learned.West),
                East = room.East != default ? room.East : LearnedWall(learned.East)
            };
        }
    }

    private static Wall LearnedCenter(LiveMapRoom room)
    {
        if (!room.HasVisitedBounds || !ValidCoordinate(room.MinX, room.MinZ) || !ValidCoordinate(room.MaxX, room.MaxZ))
            return default;

        var span = new WDir(room.MaxX - room.MinX, room.MaxZ - room.MinZ);
        if (span.LengthSq() < 64f)
            return default;

        return new(new WPos((room.MinX + room.MaxX) * 0.5f, (room.MinZ + room.MaxZ) * 0.5f).Rounded(0.1f), 0.25f);
    }

    private static Wall LearnedWall(LiveMapPoint? point) => point != null && point.Samples > 0 && ValidCoordinate(point.X, point.Z)
        ? new(new WPos(point.X, point.Z).Rounded(0.1f), 0.25f)
        : default;

    private static bool ValidCoordinate(float x, float z) => float.IsFinite(x) && float.IsFinite(z);

    private void ObserveLiveMap(Actor player)
    {
        if (Palace.DungeonId != DeepDungeonState.DungeonType.POTD)
            return;

        var key = CurrentLiveMapKey;
        if (_liveMapObservationKey != key)
        {
            ResetLiveMapObservation();
            _liveMapObservationKey = key;
        }

        var room = FindReportedPlayerRoom(player);
        if (room <= 0 || room >= Palace.Rooms.Length || (byte)Palace.Rooms[room] == 0)
        {
            if (!_liveMapInConnection && _liveMapObservedRoom > 0)
            {
                _liveMapInConnection = true;
                _liveMapConnectionFromRoom = _liveMapObservedRoom;
                _liveMapConnectionExitPosition = _liveMapLastPosition != default
                    ? WPos.Lerp(_liveMapLastPosition, player.Position, 0.5f).Rounded(0.1f)
                    : player.Position.Rounded(0.1f);
            }
            _liveMapLastPosition = player.Position;
            return;
        }

        var rooms = LiveMapData().GetOrAdd(key);
        var learnedRoom = rooms.GetOrAdd(room);
        var movementSinceLastObservation = _liveMapLastPosition != default ? (player.Position - _liveMapLastPosition).Length() : 0f;
        if (_liveMapObservedRoom != room || _liveMapLastPosition == default || movementSinceLastObservation <= 12f)
            _liveMapDirty |= ExpandVisitedBounds(learnedRoom, player.Position);

        var previousRoomIndex = _liveMapInConnection ? _liveMapConnectionFromRoom : _liveMapObservedRoom;
        if (previousRoomIndex > 0 && previousRoomIndex != room)
        {
            var exitPosition = _liveMapInConnection
                ? _liveMapConnectionExitPosition
                : WPos.Lerp(_liveMapLastPosition, player.Position, 0.5f).Rounded(0.1f);
            var entryPosition = WPos.Lerp(_liveMapLastPosition, player.Position, 0.5f).Rounded(0.1f);
            if ((_liveMapInConnection || movementSinceLastObservation <= 12f) && TryConnectionDirection(previousRoomIndex, room, out var direction))
            {
                var previousRoom = rooms.GetOrAdd(previousRoomIndex);
                _liveMapDirty |= AddConnectionSample(previousRoom, direction, exitPosition);
                _liveMapDirty |= AddConnectionSample(learnedRoom, Opposite(direction), entryPosition);
                var navigationChanged = ApplyObservedConnection(previousRoomIndex, previousRoom, direction);
                navigationChanged |= ApplyObservedConnection(room, learnedRoom, Opposite(direction));
                if (navigationChanged)
                    ResetNavigationProgress();
                SaveLiveMapDataThrottled();
            }
        }

        _liveMapInConnection = false;
        _liveMapConnectionFromRoom = -1;
        _liveMapConnectionExitPosition = default;
        _liveMapObservedRoom = room;
        _liveMapLastPosition = player.Position;
    }

    private static bool ExpandVisitedBounds(LiveMapRoom room, WPos position)
    {
        if (!room.HasVisitedBounds)
        {
            room.HasVisitedBounds = true;
            room.MinX = room.MaxX = position.X;
            room.MinZ = room.MaxZ = position.Z;
            return true;
        }

        var minX = Math.Min(room.MinX, position.X);
        var minZ = Math.Min(room.MinZ, position.Z);
        var maxX = Math.Max(room.MaxX, position.X);
        var maxZ = Math.Max(room.MaxZ, position.Z);
        if (minX == room.MinX && minZ == room.MinZ && maxX == room.MaxX && maxZ == room.MaxZ)
            return false;

        room.MinX = minX;
        room.MinZ = minZ;
        room.MaxX = maxX;
        room.MaxZ = maxZ;
        return true;
    }

    private bool TryConnectionDirection(int fromRoom, int toRoom, out Direction direction)
    {
        switch (toRoom - fromRoom)
        {
            case -5: direction = Direction.North; break;
            case 5: direction = Direction.South; break;
            case 1: direction = Direction.East; break;
            case -1: direction = Direction.West; break;
            default:
                direction = default;
                return false;
        }
        return FloorPathfind.HasConnection(Palace.Rooms, fromRoom, direction);
    }

    private static bool AddConnectionSample(LiveMapRoom room, Direction direction, WPos position)
    {
        var point = direction switch
        {
            Direction.North => room.North,
            Direction.South => room.South,
            Direction.East => room.East,
            Direction.West => room.West,
            _ => null
        };
        if (point == null)
        {
            point = new LiveMapPoint();
            switch (direction)
            {
                case Direction.North: room.North = point; break;
                case Direction.South: room.South = point; break;
                case Direction.East: room.East = point; break;
                case Direction.West: room.West = point; break;
                default: return false;
            }
        }

        ++point.Samples;
        point.X += (position.X - point.X) / point.Samples;
        point.Z += (position.Z - point.Z) / point.Samples;
        return true;
    }

    private bool ApplyObservedConnection(int roomIndex, LiveMapRoom learnedRoom, Direction direction)
    {
        var learnedSide = LearnedWall(ConnectionPoint(learnedRoom, direction));
        if (learnedSide == default)
            return false;

        if (!RoomNavigation.TryGetValue(roomIndex, out var room))
            room = new(LearnedCenter(learnedRoom), default, default, default, default);
        else if (room.Center == default)
        {
            var learnedCenter = LearnedCenter(learnedRoom);
            if (learnedCenter != default)
                room = room with { Center = learnedCenter };
        }

        var existingSide = Side(room, direction);
        if (existingSide != default)
            return false;

        RoomNavigation[roomIndex] = WithSide(room, direction, learnedSide);
        return true;
    }

    private static LiveMapPoint? ConnectionPoint(LiveMapRoom room, Direction direction) => direction switch
    {
        Direction.North => room.North,
        Direction.South => room.South,
        Direction.East => room.East,
        Direction.West => room.West,
        _ => null
    };

    private void FlushLiveMapData() => SaveLiveMapData(synchronous: true); // called from Dispose, where a background task might not get to run

    private void ResetLiveMapObservation()
    {
        SaveLiveMapData();
        _liveMapObservationKey = "";
        _liveMapNextSaveAt = default;
        _liveMapObservedRoom = -1;
        _liveMapLastPosition = default;
        _liveMapInConnection = false;
        _liveMapConnectionFromRoom = -1;
        _liveMapConnectionExitPosition = default;
    }
}
