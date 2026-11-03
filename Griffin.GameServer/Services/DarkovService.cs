using Google.Protobuf;
using Google.Protobuf.Reflection;
using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Serilog;

namespace Griffin.GameServer.Services
{
    /// <summary>
    /// The Darkov system (危区探遗, "ES" in the client: a 3D extraction mode; ids 10373-10388 and 26000-26108). The messages come from the client
    /// (scripts/gen-darkov-proto.py, protos/darkov.proto); the client indexes the nested messages of the answers without null checks, so every answer has all its
    /// message fields present (empty); the information of the account is kept per account (A-144 in docs/assumptions.md).
    /// </summary>
    public interface IDarkovService : IGameService
    {
        /// <summary>Answers a request: <paramref name="reply"/> (null when the protocol has none) is completed and sent with the pushes the request causes.</summary>
        void Answer(Connection connection, IMessage request, IMessage? reply);
    }

    public sealed class DarkovService(IAccountStateService accountState, IGunService gunService) : IDarkovService
    {
        private const int FillDepth = 6;

        public sealed class DarkovState
        {
            public ulong RoomId { get; set; }
            public long Started { get; set; }
            public Dictionary<string, uint> Settings { get; set; } = [];
        }

        /// <summary>Every singular message field of <paramref name="message"/> (recursively, to a depth) gets an empty instance.</summary>
        public static T Fill<T>(T message, int depth = FillDepth) where T : IMessage
        {
            if (depth <= 0)
                return message;
            foreach (FieldDescriptor field in message.Descriptor.Fields.InDeclarationOrder())
            {
                if (field.FieldType != FieldType.Message || field.IsRepeated || field.IsMap)
                    continue;
                object? current = field.Accessor.GetValue(message);
                if (current is null)
                {
                    var created = (IMessage)field.MessageType.ClrType.GetConstructor(Type.EmptyTypes)!.Invoke(null);
                    field.Accessor.SetValue(message, created);
                    current = created;
                }

                Fill((IMessage)current, depth - 1);
            }

            return message;
        }

        /// <summary>The answer the official server gave (captured with scripts/capture/mitm_darkov.py, folder darkov-replay/&lt;message id&gt;.bin, A-145), when there is one.</summary>
        private static IMessage? Recorded(IMessage reply)
        {
            if (!Enum.TryParse("MsgSc" + reply.Descriptor.Name[3..], out Griffin.Common.Protocol.MsgId id))
                return null;
            string? dir = Environment.GetEnvironmentVariable("GRIFFIN_DARKOV_REPLAY");
            for (string? d = AppContext.BaseDirectory; dir is null && d is not null; d = Path.GetDirectoryName(d))
                if (Directory.Exists(Path.Combine(d, "darkov-replay")))
                    dir = Path.Combine(d, "darkov-replay");
            string file = Path.Combine(dir ?? "", $"{(int)id}.bin");
            if (dir is null || !File.Exists(file))
                return null;
            try
            {
                return reply.Descriptor.Parser.ParseFrom(File.ReadAllBytes(file));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Darkov: recorded answer {File} is not valid.", file);
                return null;
            }
        }

        private sealed class Room
        {
            public List<string> Init { get; set; } = [];
            public List<List<string>> Ticks { get; set; } = [];
        }

        private static Room? room;
        private static readonly Dictionary<uint, int> TickOf = [];

        private static Room? RecordedRoom()
        {
            if (room is not null)
                return room;
            string? dir = Environment.GetEnvironmentVariable("GRIFFIN_DARKOV_REPLAY");
            for (string? d = AppContext.BaseDirectory; dir is null && d is not null; d = Path.GetDirectoryName(d))
                if (Directory.Exists(Path.Combine(d, "darkov-replay")))
                    dir = Path.Combine(d, "darkov-replay");
            string file = Path.Combine(dir ?? "", "room.json");
            if (dir is null || !File.Exists(file))
                return null;
            try
            {
                return room = System.Text.Json.JsonSerializer.Deserialize<Room>(File.ReadAllText(file), new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Darkov: recorded room {File} is not valid.", file);
                return null;
            }
        }

        private static void PushRoom(Connection connection, IEnumerable<string> frames)
        {
            foreach (string hex in frames)
                connection.SendRawPacketsWith([((ushort)Griffin.Common.Protocol.MsgId.MsgScDarkovRoomUpdate, Convert.FromHexString(hex))]);
        }

        public void Answer(Connection connection, IMessage request, IMessage? reply)
        {
            uint uid = connection.Account!.Uid;
            if (reply is SC_GetDarkovInfo)
            {
                // the account data of the mode lives in the profile (A-145)
                connection.SendRawPacketsWith([(26001, Darkov.DarkovWire.Info(Darkov.DarkovProfiles.Load(accountState, uid)))]);
                return;
            }

            var messages = new List<IMessage>();
            if (reply is not null)
            {
                IMessage? real = Recorded(reply);
                if (real is not null)
                    reply = real;
                else
                    Fill(reply);
                messages.Add(reply);
            }

            // the dolls of the account that can be taken in (SC_GetDarkovInfo.f5: f1 = doll id)
            if (reply is SC_GetDarkovInfo info)
                foreach (var gun in gunService.GetPlayerGuns(uid))
                    info.F5.Add(new DvCAINGGPOPLE { F1 = gun.GunId, F10 = (uint)Math.Max(1, gun.Level) });

            // the acknowledgement of the operations: the frame the client sent, and one entry per operation with its index (exactly what the official server answers)
            if (request is CS_DarkovOp operations && reply is SC_DarkovOp ack)
            {
                ack.F1 = operations.F2;
                foreach (var operation in operations.F1)
                    ack.F2.Add(new DvEFAKEJHMIMO { F1 = operation.F1 });
            }

            switch (request)
            {
                case CS_DarkovStart start:
                {
                    DarkovState state = accountState.Get<DarkovState>(uid, "darkov.state") ?? new DarkovState();
                    state.RoomId = start.F1;
                    state.Started = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    accountState.Set(uid, "darkov.state", state);
                    break;
                }
                case CS_DarkovSetSetting set:
                {
                    DarkovState state = accountState.Get<DarkovState>(uid, "darkov.state") ?? new DarkovState();
                    accountState.Set(uid, "darkov.state", state);
                    break;
                }
            }

            // the world of the room: what the official server pushed in a recorded match (26015), the first burst after Loaded and one burst per heartbeat (A-145)
            if (messages.Count > 0)
                connection.SendAutoEncrypted(messages.ToArray());
            messages.Clear();
            if (request is CS_DarkovLoaded or CS_DarkovHeartBeat && RecordedRoom() is { } recorded)
            {
                if (request is CS_DarkovLoaded)
                {
                    TickOf[uid] = 0;
                    PushRoom(connection, recorded.Init);
                }
                else if (recorded.Ticks.Count > 0)
                {
                    int i = TickOf.GetValueOrDefault(uid);
                    TickOf[uid] = i + 1;
                    PushRoom(connection, recorded.Ticks[i % recorded.Ticks.Count]);
                }
            }

            Log.Information("Darkov: {Request} answered for account {Uid}{Reply}.", request.Descriptor.Name, uid, reply is null ? " (no reply in the protocol)" : "");
            if (messages.Count > 0)
                connection.SendAutoEncrypted(messages.ToArray());
        }
    }
}
