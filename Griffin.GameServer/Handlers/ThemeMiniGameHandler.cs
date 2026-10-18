using Google.Protobuf;
using Griffin.Common.Networking;
using Griffin.Common.Packets;
using Griffin.Common.Proto;
using Griffin.Common.Protocol;
using Griffin.GameServer.Extensions;
using Griffin.GameServer.Services;
using Serilog;

namespace Griffin.GameServer.Handlers
{
    public sealed class ThemeMiniGameHandler(IAccountStateService accountState) : ThemeMiniGameHandlerBase
    {
        private const string StateKey = "theme-minigame.v1";
        private const string RedPointStateKey = "theme-minigame.red-points.v1";
        private const uint DoodleJumpGameId = 21005;

        /// <summary>"I Wanna" platform challenge of the 擂台挑战 event (A-101): the client plays it locally and reports result, score and stars of the stage.</summary>
        private const uint IwannaGameId = 28001;
        private const string IwannaRecordKey = "theme-minigame.iwanna.v1";

        private sealed record IwannaRecord(uint Score, uint Stars);

        [PacketHandler(MsgId.MsgCsThemeMiniGameGetSkill, MsgId.MsgScThemeMiniGameGetSkill)]
        public override void HandleThemeMiniGameGetSkill(CS_ThemeMiniGameGetSkill request, Connection connection)
        {
            // Skill levels are represented only for the motorcycle variant in this protocol.
            // For other variants, return the requested identifiers with the default empty skill state.
            connection.Send(new SC_ThemeMiniGameGetSkill
            {
                ThemeId = request.ThemeId,
                FNEFKKLJMDN = request.FNEFKKLJMDN,
                JJOKKNFDAGE = request.JJOKKNFDAGE,
            });
        }

        [PacketHandler(MsgId.MsgCsThemeMiniGameRecordRedPoint, MsgId.MsgScThemeMiniGameRecordRedPoint)]
        public void HandleThemeMiniGameRecordRedPoint(CS_ThemeMiniGameRecordRedPoint request, Connection connection)
        {
            if (connection.Account is null)
                return;

            SC_ThemeMiniGameRecordRedPoint response = new()
            {
                ThemeId = request.ThemeId,
                FNEFKKLJMDN = request.FNEFKKLJMDN,
            };
            if (request.IwannaTouch is { StageId: > 0 } read)
            {
                Dictionary<string, bool> readStages = accountState.Get<Dictionary<string, bool>>(
                    connection.Account.Uid, RedPointStateKey) ?? [];
                readStages[$"{request.ThemeId}:{request.FNEFKKLJMDN}:{read.StageId}"] = true;
                accountState.Set(connection.Account.Uid, RedPointStateKey, readStages);
                response.IwannaTouch = read.Clone();
            }

            connection.Send(response);
        }

        public override void HandleThemeMiniGameInfo(CS_ThemeMiniGameInfo request, Connection connection)
        {
            if (connection.Account is null)
                return;

            Dictionary<string, ThemeMiniGameState> states =
                accountState.Get<Dictionary<string, ThemeMiniGameState>>(connection.Account.Uid, StateKey) ?? [];
            ThemeMiniGameState state = GetState(states, request.ThemeId, request.FNEFKKLJMDN);

            SC_ThemeMiniGameInfo response = new()
            {
                ThemeId = request.ThemeId,
                FNEFKKLJMDN = request.FNEFKKLJMDN,
            };
            if (request.FNEFKKLJMDN == DoodleJumpGameId)
            {
                response.DoodleJumpData = new FOMAJCHMPFL
                {
                    KNFOFFBOCML = state.Unlocked,
                    CGFMBCCDFPG = state.BestScore,
                    Duration = state.Duration,
                    Times = state.Times,
                };
            }

            if (request.FNEFKKLJMDN == IwannaGameId)
            {
                // iwanna_data (field 9) as the client reads it: {1: map<stage, {1: best score, 2: stars}>, 2: bit mask of the stages whose red dot was read}. The checked-in schema
                // of this oneof is another build's, so the bytes are written by hand and parsed back (the unknown fields travel unchanged).
                Dictionary<string, IwannaRecord> records = accountState.Get<Dictionary<string, IwannaRecord>>(connection.Account.Uid, IwannaRecordKey) ?? [];
                using MemoryStream data = new();
                using (CodedOutputStream output = new(data, leaveOpen: true))
                    foreach ((string stage, IwannaRecord record) in records.Where(pair => uint.TryParse(pair.Key, out _)).OrderBy(pair => uint.Parse(pair.Key)))
                    {
                        using MemoryStream entry = new();
                        using (CodedOutputStream inner = new(entry, leaveOpen: true))
                        {
                            inner.WriteTag(1, WireFormat.WireType.Varint);
                            inner.WriteUInt32(uint.Parse(stage));
                            inner.WriteTag(2, WireFormat.WireType.LengthDelimited);
                            using MemoryStream value = new();
                            using (CodedOutputStream valueOut = new(value, leaveOpen: true))
                            {
                                valueOut.WriteTag(1, WireFormat.WireType.Varint);
                                valueOut.WriteUInt32(record.Score);
                                valueOut.WriteTag(2, WireFormat.WireType.Varint);
                                valueOut.WriteUInt32(record.Stars);
                            }

                            inner.WriteBytes(ByteString.CopyFrom(value.ToArray()));
                        }

                        output.WriteTag(1, WireFormat.WireType.LengthDelimited);
                        output.WriteBytes(ByteString.CopyFrom(entry.ToArray()));
                    }

                using MemoryStream message = new();
                using (CodedOutputStream outer = new(message, leaveOpen: true))
                {
                    outer.WriteTag(1, WireFormat.WireType.Varint);
                    outer.WriteUInt32(request.ThemeId);
                    outer.WriteTag(2, WireFormat.WireType.Varint);
                    outer.WriteUInt32(request.FNEFKKLJMDN);
                    outer.WriteTag(9, WireFormat.WireType.LengthDelimited);
                    outer.WriteBytes(ByteString.CopyFrom(data.ToArray()));
                }

                connection.Send(SC_ThemeMiniGameInfo.Parser.ParseFrom(message.ToArray()));
                return;
            }

            connection.Send(response);
        }

        [PacketHandler(MsgId.MsgCsThemeMiniGameRank, MsgId.MsgScThemeMiniGameRank)]
        public void HandleThemeMiniGameRank(CS_ThemeMiniGameRank request, Connection connection)
        {
            // There is no real friend/guild population for a single-player local server.
            // Return the requested scope with no fabricated players or scores.
            connection.Send(new SC_ThemeMiniGameRank
            {
                ThemeId = request.ThemeId,
                FNEFKKLJMDN = request.FNEFKKLJMDN,
                OCGNAONELAA = (SC_ThemeMiniGameRank.Types.IGHPEHHAANA)(int)request.OCGNAONELAA,
            });
        }

        public override void HandleThemeMiniGameStart(CS_ThemeMiniGameStart request, Connection connection)
        {
            if (connection.Account is null)
                return;

            if (request.FNEFKKLJMDN == IwannaGameId)
            {
                // the unlock by time of each stage is decided by the client (activity opening + IwannaStageData column 3); the server only opens the run
                connection.Send(new SC_ThemeMiniGameStart { ThemeId = request.ThemeId, FNEFKKLJMDN = request.FNEFKKLJMDN, StageId = request.StageId });
                return;
            }

            if (request.FNEFKKLJMDN != DoodleJumpGameId)
            {
                // any other mini game of an event (candy catch, card flip, audition, motorcycle...): the run opens and the client plays it locally; its records are not kept (A-101)
                Log.Information("Theme minigame {GameId} start: generic answer for account {Uid}, theme {ThemeId}, stage {StageId}.",
                    request.FNEFKKLJMDN, connection.Account.Uid, request.ThemeId, request.StageId);
                connection.Send(new SC_ThemeMiniGameStart { ThemeId = request.ThemeId, FNEFKKLJMDN = request.FNEFKKLJMDN, StageId = request.StageId });
                return;
            }

            Dictionary<string, ThemeMiniGameState> states = GetStates(connection.Account.Uid);
            string key = Key(request.ThemeId, request.FNEFKKLJMDN);
            ThemeMiniGameState state = GetState(states, request.ThemeId, request.FNEFKKLJMDN) with
            {
                Unlocked = true,
                StageId = request.StageId,
                StartedAtUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            states[key] = state;
            accountState.Set(connection.Account.Uid, StateKey, states);

            connection.Send(new SC_ThemeMiniGameStart
            {
                ThemeId = request.ThemeId,
                FNEFKKLJMDN = request.FNEFKKLJMDN,
                StageId = request.StageId,
            });
        }

        [PacketHandler(MsgId.MsgCsThemeMiniGameFinish, MsgId.MsgScThemeMiniGameFinish)]
        public void HandleThemeMiniGameFinish(CS_ThemeMiniGameFinish request, Connection connection)
        {
            if (connection.Account is null)
                return;

            if (request.FNEFKKLJMDN == IwannaGameId)
            {
                // inner message of the report and of the answer: {1: result (1 win, 2 lose), 2: score, 3: stars}
                uint result = 0, score = 0, stars = 0, tag;
                CodedInputStream input = new(request.Iwanna?.ToByteArray() ?? []);
                while ((tag = input.ReadTag()) != 0)
                {
                    switch (tag >> 3)
                    {
                        case 1: result = input.ReadUInt32(); break;
                        case 2: score = input.ReadUInt32(); break;
                        case 3: stars = input.ReadUInt32(); break;
                        default: input.SkipLastField(); break;
                    }
                }

                Dictionary<string, IwannaRecord> records = accountState.Get<Dictionary<string, IwannaRecord>>(connection.Account.Uid, IwannaRecordKey) ?? [];
                string stageKey = request.StageId.ToString();
                IwannaRecord known = records.GetValueOrDefault(stageKey) ?? new IwannaRecord(0, 0);
                if (result == 1)
                    records[stageKey] = new IwannaRecord(Math.Max(known.Score, score), Math.Max(known.Stars, stars));
                accountState.Set(connection.Account.Uid, IwannaRecordKey, records);

                using MemoryStream answer = new();
                using (CodedOutputStream inner = new(answer, leaveOpen: true))
                {
                    inner.WriteTag(1, WireFormat.WireType.Varint);
                    inner.WriteUInt32(result);
                    inner.WriteTag(2, WireFormat.WireType.Varint);
                    inner.WriteUInt32(score);
                    inner.WriteTag(3, WireFormat.WireType.Varint);
                    inner.WriteUInt32(stars);
                }

                SC_ThemeMiniGameFinish reply = new()
                {
                    ThemeId = request.ThemeId,
                    FNEFKKLJMDN = request.FNEFKKLJMDN,
                    StageId = request.StageId,
                    Score = request.Score,
                    Iwanna = SC_ThemeMiniGameFinish.Types.IJIBOJHKFKA.Parser.ParseFrom(answer.ToArray()),
                };
                connection.Send(reply);
                return;
            }

            if (request.FNEFKKLJMDN != DoodleJumpGameId)
            {
                Log.Information("Theme minigame {GameId} finish: generic answer for account {Uid}, theme {ThemeId}, stage {StageId}, score {Score}.",
                    request.FNEFKKLJMDN, connection.Account.Uid, request.ThemeId, request.StageId, request.Score);
                connection.Send(new SC_ThemeMiniGameFinish
                {
                    ThemeId = request.ThemeId,
                    FNEFKKLJMDN = request.FNEFKKLJMDN,
                    StageId = request.StageId,
                    Score = request.Score,
                    CGFMBCCDFPG = request.Score,
                });
                return;
            }

            Dictionary<string, ThemeMiniGameState> states = GetStates(connection.Account.Uid);
            string key = Key(request.ThemeId, request.FNEFKKLJMDN);
            ThemeMiniGameState state = GetState(states, request.ThemeId, request.FNEFKKLJMDN);
            if (state.StartedAtUnixSeconds == 0 || state.StageId != request.StageId)
            {
                Log.Warning("Theme minigame finish rejected for account {Uid}: no matching start for theme {ThemeId}, stage {StageId}.",
                    connection.Account.Uid, request.ThemeId, request.StageId);
                connection.Refuse(MsgId.MsgCsThemeMiniGameFinish, HNINDAOJCKP.ClientInputInvalid);
                return;
            }

            // Doodle Jump runs in the client's local simulation. Keep the score it reports, but
            // do not mint rewards or unlocks from it; those require a proven server-side rule.
            state = state with
            {
                BestScore = Math.Max(state.BestScore, request.Score),
                Duration = request.Duration,
                Times = state.Times == uint.MaxValue ? uint.MaxValue : state.Times + 1,
                StageId = 0,
                StartedAtUnixSeconds = 0,
            };
            states[key] = state;
            accountState.Set(connection.Account.Uid, StateKey, states);

            connection.Send(new SC_ThemeMiniGameFinish
            {
                ThemeId = request.ThemeId,
                FNEFKKLJMDN = request.FNEFKKLJMDN,
                StageId = request.StageId,
                Score = request.Score,
                CGFMBCCDFPG = state.BestScore,
            });
        }

        private Dictionary<string, ThemeMiniGameState> GetStates(uint uid) =>
            accountState.Get<Dictionary<string, ThemeMiniGameState>>(uid, StateKey) ?? [];

        private static string Key(uint themeId, uint gameId) => $"{themeId}:{gameId}";

        private static ThemeMiniGameState GetState(Dictionary<string, ThemeMiniGameState> states, uint themeId, uint gameId)
        {
            string key = Key(themeId, gameId);
            if (states.TryGetValue(key, out ThemeMiniGameState? state))
                return state;

            state = new ThemeMiniGameState(themeId, gameId, 0, 0, 0, false, 0, 0);
            states[key] = state;
            return state;
        }

        private sealed record ThemeMiniGameState(
            uint ThemeId,
            uint GameId,
            uint BestScore,
            uint Duration,
            uint Times,
            bool Unlocked,
            uint StageId,
            long StartedAtUnixSeconds);
    }
}
