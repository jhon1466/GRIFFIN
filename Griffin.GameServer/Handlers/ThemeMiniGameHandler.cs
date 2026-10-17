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

            if (request.FNEFKKLJMDN != DoodleJumpGameId)
            {
                Log.Warning("Theme minigame {GameId} start is not implemented; account {Uid} requested theme {ThemeId}.",
                    request.FNEFKKLJMDN, connection.Account.Uid, request.ThemeId);
                connection.Refuse(MsgId.MsgCsThemeMiniGameStart, HNINDAOJCKP.MiniGameNotOpen);
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

            if (request.FNEFKKLJMDN != DoodleJumpGameId)
            {
                Log.Warning("Theme minigame {GameId} finish is not implemented; account {Uid} requested theme {ThemeId}.",
                    request.FNEFKKLJMDN, connection.Account.Uid, request.ThemeId);
                connection.Refuse(MsgId.MsgCsThemeMiniGameFinish, HNINDAOJCKP.ThemeActivityNotStartOrClosed);
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
