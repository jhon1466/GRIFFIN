using System.Collections.Concurrent;
using System.Text.Json;
using Griffin.Common.Networking;
using Griffin.Common.Packets;
using Serilog;

namespace Griffin.GameServer.Services.Darkov
{
    /// <summary>Rooms in progress, by account, by id and by the connection that plays them.</summary>
    public static class DarkovRooms
    {
        public static readonly ConcurrentDictionary<uint, DarkovRoom> Active = new();
        public static readonly ConcurrentDictionary<ulong, DarkovRoom> ById = new();
        public static readonly ConcurrentDictionary<string, DarkovRoom> ByConnection = new();
        private static long counter;

        public static ulong NewRoomId() => 1877000000000000000UL + (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000 + (ulong)(Interlocked.Increment(ref counter) % 1000);
    }

    /// <summary>
    /// The room messages of the mode (march, start, loaded, heartbeat, operations) read from their wire bytes: the checked-in proto types do not describe them (A-145).
    /// The client plays the room on a second connection of its own (the "DarKov channel", opened at the address the march answer gives), where no login happens: the room
    /// is found by its id (start, loaded, heartbeat) and then by the connection.
    /// </summary>
    public sealed class DarkovRawHandler(IAccountStateService accountState, ConnectionManager connections) : IRawPacketHandler
    {
        private const ushort March = 26002, Exit = 26004, Start = 26006, Loaded = 26008, Heartbeat = 26010, Op = 26012;
        private const ushort ExitReply = 26005;

        public bool TryHandle(Connection connection, ushort rawMsgId, byte[] payload)
        {
            if (DarkovHub.Handled.Contains(rawMsgId))
            {
                if (connection.Account is null)
                    return false;
                uint accountUid = connection.Account.Uid;
                DarkovProfile profile = DarkovProfiles.Load(accountState, accountUid);
                var (answer, changed) = DarkovHub.Handle(rawMsgId, PbMsg.Parse(payload), profile);
                if (changed)
                    DarkovProfiles.Save(accountState, accountUid, profile);
                foreach ((ushort id, byte[] data) in answer)
                    connection.SendRawPacketsWith([(id, data)]);
                return true;
            }

            if (rawMsgId is not (March or Exit or Start or Loaded or Heartbeat or Op))
                return false;

            PbMsg request = PbMsg.Parse(payload);
            List<(ushort, byte[])> packets;
            DarkovRoom? room;
            switch (rawMsgId)
            {
                case March:
                {
                    if (connection.Account is null)
                        return false;
                    uint uid = connection.Account.Uid;
                    DarkovProfile profile = DarkovProfiles.Load(accountState, uid);
                    uint stage = (uint)request.U(1);
                    uint poi = (uint)request.U(6);
                    room = new DarkovRoom(DarkovRooms.NewRoomId(), stage, DarkovProfiles.Clone(profile), poi) { Uid = uid };
                    if (DarkovRooms.Active.TryGetValue(uid, out DarkovRoom? previous))
                        DarkovRooms.ById.TryRemove(previous.RoomId, out _);
                    DarkovRooms.Active[uid] = room;
                    DarkovRooms.ById[room.RoomId] = room;
                    if (!profile.Stages.ContainsKey(stage))
                    {
                        profile.Stages[stage] = new DExplore { Since = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
                        DarkovProfiles.Save(accountState, uid, profile);
                    }

                    packets = room.March(connection.LocalAddress);
                    Log.Information("Darkov: room {Room} created for account {Uid} on stage {Stage} ({Units} units), room server {Address}.", room.RoomId, uid, stage, room.Units.Count, connection.LocalAddress);
                    break;
                }
                case Start:
                    room = Find(connection, request.U(1));
                    if (room is null)
                        return false;
                    DarkovRooms.ByConnection[connection.Id] = room;
                    packets = room.Start();
                    break;
                case Loaded:
                    room = Find(connection, request.U(1));
                    if (room is null)
                        return false;
                    DarkovRooms.ByConnection[connection.Id] = room;
                    packets = room.Loaded(request.I(2));
                    break;
                case Heartbeat:
                    room = Find(connection, request.U(1));
                    if (room is null)
                        return false;
                    packets = room.Heartbeat(request.I(2));
                    break;
                case Op:
                    room = Find(connection, 0);
                    if (room is null)
                        return false;
                    packets = room.Ops(request);
                    break;
                default:
                    room = Find(connection, 0);
                    if (room is null)
                    {
                        connection.SendRawPacketsWith([(ExitReply, [])]);
                        return true;
                    }

                    packets = room.Abandon();
                    packets.Add((ExitReply, []));
                    break;
            }

            foreach ((ushort id, byte[] data) in packets)
                connection.SendRawPacketsWith([(id, data)]);
            if (room.Finished && !room.Committed)
                Commit(room);
            return true;
        }

        private DarkovRoom? Find(Connection connection, ulong roomId)
        {
            if (roomId != 0 && DarkovRooms.ById.TryGetValue(roomId, out DarkovRoom? byId))
                return Bind(connection, byId);
            if (DarkovRooms.ByConnection.TryGetValue(connection.Id, out DarkovRoom? bound))
                return bound;
            if (connection.Account is not null && DarkovRooms.Active.TryGetValue(connection.Account.Uid, out DarkovRoom? byAccount))
                return Bind(connection, byAccount);

            // a connection of the room channel that has not said which room it plays (operations carry no room id): the room of the same client that is in progress
            string address = Host(connection.Id);
            var candidates = DarkovRooms.Active.Values.Where(r => !r.Finished && r.Started
                && connections.Connections.Any(c => c.Account?.Uid == r.Uid && Host(c.Id) == address)).ToList();
            return candidates.Count == 1 ? Bind(connection, candidates[0]) : null;
        }

        private static DarkovRoom Bind(Connection connection, DarkovRoom room)
        {
            DarkovRooms.ByConnection[connection.Id] = room;
            return room;
        }

        private static string Host(string endpoint) => endpoint[..Math.Max(0, endpoint.LastIndexOf(':'))];

        /// <summary>The match is over: what the player brought out goes back to the account and the hub (on the main connection) is refreshed.</summary>
        private void Commit(DarkovRoom room)
        {
            room.Committed = true;
            DarkovProfile profile = DarkovProfiles.Load(accountState, room.Uid);
            room.Settle(profile);
            DarkovProfiles.Save(accountState, room.Uid, profile);
            Connection? main = connections.Connections.FirstOrDefault(c => c.Account?.Uid == room.Uid);
            if (main is not null)
            {
                if (room.Win)
                    main.SendRawPacketsWith([(26030, new PbW().V(1, profile.Exp).ToArray())]);
                main.SendRawPacketsWith([(DarkovQuests.GetReply, new PbW().B(1, DarkovQuests.List(profile)).ToArray())]);
                main.SendRawPacketsWith([(DarkovHub.ExplorationReply, DarkovHub.ExplorationList(profile))]);
                main.SendRawPacketsWith([(26001, DarkovWire.Info(profile))]);
            }

            Log.Information("Darkov: room {Room} of account {Uid} finished ({Result}), exp now {Exp}.", room.RoomId, room.Uid, room.Win ? "won" : "lost", profile.Exp);
        }
    }

    /// <summary>Loads, saves and creates the Darkov profile of an account.</summary>
    public static class DarkovProfiles
    {
        private const string Key = "darkov.profile";

        public static DarkovProfile Load(IAccountStateService state, uint uid)
        {
            DarkovProfile? profile = state.Get<DarkovProfile>(uid, Key);
            if (profile is null)
            {
                profile = Starter();
                Save(state, uid, profile);
            }

            profile.Normalize();
            return profile;
        }

        public static void Save(IAccountStateService state, uint uid, DarkovProfile profile) => state.Set(uid, Key, profile);

        public static DarkovProfile Clone(DarkovProfile profile) => JsonSerializer.Deserialize<DarkovProfile>(JsonSerializer.Serialize(profile))!;

        /// <summary>What a new account starts with: a pistol, some ammunition and bandages.</summary>
        public static DarkovProfile Starter()
        {
            var profile = new DarkovProfile { Level = 1, Exp = 0 };
            profile.Normalize();
            var items = RawTables.ById("EsItemData");
            DItem Make(uint cfg, uint count)
            {
                var item = new DItem { Uid = profile.NewUid(), Cfg = cfg, Count = count };
                if (items.TryGetValue(cfg, out PbMsg? info) && info.U(13) > 0)
                    item.DurCur = item.DurMax = (uint)info.U(13);
                return item;
            }

            profile.Equip[1] = Make(3052001, 1);
            profile.Bag[0] = Make(3071001, 90);
            profile.Bag[1] = Make(3130201, 5);
            profile.Bag[2] = Make(3130101, 3);
            profile.Bag[3] = Make(3010101, 2);
            profile.QuickSlots[0] = 3130201;
            profile.Ammo[3041001] = 5000;
            profile.Ammo[3041002] = 1;
            return profile;
        }
    }
}
