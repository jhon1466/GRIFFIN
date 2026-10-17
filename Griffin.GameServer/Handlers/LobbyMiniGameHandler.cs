using Griffin.Common.Networking;
using Griffin.Common.Packets;
using Griffin.Common.Proto;
using Griffin.Common.Protocol;
using Griffin.Common.Table;
using Griffin.GameServer.Extensions;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    public sealed class LobbyMiniGameHandler(ITableService tables, ILobbyMiniGameService miniGames) : LobbyMiniGameHandlerBase
    {
        public override void HandleLobbyMiniGameInfo(CS_LobbyMiniGameInfo request, Connection connection)
        {
            if (connection.Account is null)
                return;

            Dictionary<uint, MiniGameState> states = States(connection.Account.Uid);
            SC_LobbyMiniGameInfo response = new();
            foreach (LobbyMinigameGamelistData game in tables.GetTable<LobbyMinigameGamelistData>())
                response.PDGDDBPGDKD[game.Id] = LobbyMiniGameService.BuildInfo(game, states.GetValueOrDefault(game.Id) ?? new MiniGameState());

            connection.Send(response);
        }

        [PacketHandler(MsgId.MsgCsLobbyMiniGameRank, MsgId.MsgScLobbyMiniGameRank)]
        public void HandleLobbyMiniGameRank(CS_LobbyMiniGameRank request, Connection connection)
        {
            // This private server has no authoritative friend/guild roster to rank against.
            // Preserve the requested game and ranking scope without fabricating competitors.
            connection.Send(new SC_LobbyMiniGameRank
            {
                LAOKONCHCHL = request.LAOKONCHCHL,
                OCGNAONELAA = (SC_LobbyMiniGameRank.Types.PFGFIJEGOEJ)(int)request.OCGNAONELAA,
            });
        }

        /// <summary>The skills of the doodle jump are not stored: the answer repeats the request with no skill levels (ASSUMPTION A-26).</summary>
        public override void HandleLobbyMiniGameGetSkill(CS_LobbyMiniGameGetSkill request, Connection connection) =>
            connection.Send(new SC_LobbyMiniGameGetSkill { LAOKONCHCHL = request.LAOKONCHCHL, JJOKKNFDAGE = request.JJOKKNFDAGE });

        public override void HandleLobbyMiniGameStart(CS_LobbyMiniGameStart request, Connection connection)
        {
            if (connection.Account is null)
                return;

            LobbyMinigameGamelistData? game = tables.GetTable<LobbyMinigameGamelistData>()
                .FirstOrDefault(row => row.Id == request.LAOKONCHCHL);
            if (game is null)
            {
                connection.Refuse(MsgId.MsgCsLobbyMiniGameStart, HNINDAOJCKP.ItemConfigNotExist);
                return;
            }

            if (!miniGames.Start(connection.Account.Uid, game.Id, request.StageId))
            {
                MiniGameState state = States(connection.Account.Uid).GetValueOrDefault(game.Id) ?? new MiniGameState();
                connection.Refuse(MsgId.MsgCsLobbyMiniGameStart, state.Unlocked
                    ? HNINDAOJCKP.ClientInputInvalid
                    : HNINDAOJCKP.UserSystemLocked);
                return;
            }

            connection.Send(new SC_LobbyMiniGameStart { LAOKONCHCHL = game.Id, StageId = request.StageId });
        }

        private Dictionary<uint, MiniGameState> States(uint uid) => miniGames.States(uid);
    }
}
