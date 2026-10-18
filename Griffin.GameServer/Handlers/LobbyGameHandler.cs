using Google.Protobuf;
using Griffin.Common.Networking;
using Griffin.Common.Packets;
using Griffin.Common.Proto;
using Griffin.Common.Protocol;
using Griffin.Common.Table;
using Griffin.GameServer.Services;
using Serilog;

namespace Griffin.GameServer.Handlers
{
    /// <summary>
    /// The lobby games of an event (擂台挑战, "butt game", A-102): <c>LobbyGameInfo</c> lists, per game of <c>ButtgameIdData</c>, the stages won (normal and witness) and whether the
    /// final reward was claimed; the run is played by the client, which reports its result with <c>LobbyGameFinish</c>. Layout as the client reads it:
    /// <c>SC_LobbyGameInfo {1: type, 2: {1: map&lt;game, {1: map&lt;stage, {1: won}&gt;, 2: claimed, 3: map&lt;witness stage, {1: won}&gt;}&gt;}}</c>. The start, finish and claim answers
    /// repeat the request (the client reads the result and the stage back from them). Rewards of the games are not granted.
    /// </summary>
    public sealed class LobbyGameHandler(IAccountStateService accountState, ITableService tableService) : LobbyGameHandlerBase
    {
        private const string StateKey = "lobby.buttgame.v1";
        private const int ButtGameType = 1;

        private sealed class GameRecord
        {
            public HashSet<uint> Normal { get; set; } = [];

            public HashSet<uint> Witness { get; set; } = [];

            public bool Claimed { get; set; }
        }

        private Dictionary<string, GameRecord> Load(uint uid) => accountState.Get<Dictionary<string, GameRecord>>(uid, StateKey) ?? [];

        public override void HandleLobbyGameInfo(CS_LobbyGameInfo request, Connection connection)
        {
            using MemoryStream bytes = new();
            using (CodedOutputStream output = new(bytes, leaveOpen: true))
            {
                output.WriteTag(1, WireFormat.WireType.Varint);
                output.WriteInt32((int)request.Type);
                if ((int)request.Type == ButtGameType && connection.Account is not null)
                {
                    Dictionary<string, GameRecord> records = Load(connection.Account.Uid);
                    using MemoryStream info = new();
                    using (CodedOutputStream infoOut = new(info, leaveOpen: true))
                        foreach (uint game in tableService.GetTable<ButtgameIdData>().Select(row => TableFields.U(row, 1)))
                        {
                            GameRecord record = records.GetValueOrDefault(game.ToString()) ?? new GameRecord();
                            using MemoryStream entry = new();
                            using (CodedOutputStream entryOut = new(entry, leaveOpen: true))
                            {
                                WriteWins(entryOut, 1, record.Normal);
                                entryOut.WriteTag(2, WireFormat.WireType.Varint);
                                entryOut.WriteBool(record.Claimed);
                                WriteWins(entryOut, 3, record.Witness);
                            }

                            using MemoryStream pair = new();
                            using (CodedOutputStream pairOut = new(pair, leaveOpen: true))
                            {
                                pairOut.WriteTag(1, WireFormat.WireType.Varint);
                                pairOut.WriteUInt32(game);
                                pairOut.WriteTag(2, WireFormat.WireType.LengthDelimited);
                                pairOut.WriteBytes(ByteString.CopyFrom(entry.ToArray()));
                            }

                            infoOut.WriteTag(1, WireFormat.WireType.LengthDelimited);
                            infoOut.WriteBytes(ByteString.CopyFrom(pair.ToArray()));
                        }

                    output.WriteTag(2, WireFormat.WireType.LengthDelimited);
                    output.WriteBytes(ByteString.CopyFrom(info.ToArray()));
                }
            }

            connection.Send(SC_LobbyGameInfo.Parser.ParseFrom(bytes.ToArray()));
        }

        /// <summary>A map field of <c>{stage -> {1: won = true}}</c> entries.</summary>
        private static void WriteWins(CodedOutputStream output, int field, IEnumerable<uint> stages)
        {
            foreach (uint stage in stages.OrderBy(s => s))
            {
                using MemoryStream pair = new();
                using (CodedOutputStream pairOut = new(pair, leaveOpen: true))
                {
                    pairOut.WriteTag(1, WireFormat.WireType.Varint);
                    pairOut.WriteUInt32(stage);
                    pairOut.WriteTag(2, WireFormat.WireType.LengthDelimited);
                    pairOut.WriteBytes(ByteString.CopyFrom([0x08, 0x01]));
                }

                output.WriteTag(field, WireFormat.WireType.LengthDelimited);
                output.WriteBytes(ByteString.CopyFrom(pair.ToArray()));
            }
        }

        public override void HandleLobbyGameStart(CS_LobbyGameStart request, Connection connection)
        {
            Log.Information("Lobby game start: type {Type}, game {Game}, stage {Stage}.", request.Type, request.LobbyButtGameStart?.LAOKONCHCHL, request.LobbyButtGameStart?.StageId);
            connection.Send(new SC_LobbyGameStart { Type = request.Type, LobbyButtGameStart = request.LobbyButtGameStart?.Clone() });
        }

        [PacketHandler(MsgId.MsgCsLobbyGameFinish, MsgId.MsgScLobbyGameFinish)]
        public void HandleLobbyGameFinish(CS_LobbyGameFinish request, Connection connection)
        {
            SC_LobbyGameFinish reply = new() { Type = request.Type, LobbyButtGameFinish = request.LobbyButtGameFinish?.Clone() };
            if (connection.Account is not null && request.LobbyButtGameFinish is { } finish && finish.Result == FPDDLCKDDNE.LobbyGameWin && finish.JOOPJGIOLIE is { } start)
            {
                Dictionary<string, GameRecord> records = Load(connection.Account.Uid);
                GameRecord record = records.GetValueOrDefault(start.LAOKONCHCHL.ToString()) ?? new GameRecord();
                (start.Type == MJHICDHLNBG.LobbyButtGameTypeWitness ? record.Witness : record.Normal).Add(start.StageId);
                records[start.LAOKONCHCHL.ToString()] = record;
                accountState.Set(connection.Account.Uid, StateKey, records);
            }

            connection.Send(reply);
        }

        public override void HandleLobbyGameClaimUltimateRewards(CS_LobbyGameClaimUltimateRewards request, Connection connection)
        {
            if (connection.Account is not null && request.LobbyButtGameClaimUltimateRewards is { } claim)
            {
                Dictionary<string, GameRecord> records = Load(connection.Account.Uid);
                GameRecord record = records.GetValueOrDefault(claim.LAOKONCHCHL.ToString()) ?? new GameRecord();
                record.Claimed = true;
                records[claim.LAOKONCHCHL.ToString()] = record;
                accountState.Set(connection.Account.Uid, StateKey, records);
            }

            connection.Send(new SC_LobbyGameClaimUltimateRewards { Type = request.Type, LobbyButtGameClaimUltimateRewards = request.LobbyButtGameClaimUltimateRewards?.Clone() });
        }
    }
}
