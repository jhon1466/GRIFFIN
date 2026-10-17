using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    /// <summary>
    /// Lobby ASMR: the unlocked scenes and characters are stored per account (<c>lobby.asmr</c>) and reported by <c>LobbyAsmrInfo</c>.
    /// Scene 1 and character 1017 are open from the start (the captured answer). ASSUMPTION (no official capture): unlocking a scene or a
    /// character is free; the character table (<c>LobbyAsmrGunData</c> col 6/7) names an item that the server does not charge.
    /// </summary>
    public sealed class LobbyAsmrHandler(IAccountStateService accountState) : LobbyAsmrHandlerBase
    {
        public sealed class AsmrState
        {
            public List<uint> Scenes { get; set; } = [1];
            public List<uint> Guns { get; set; } = [1017];
        }

        private AsmrState Load(Connection connection) =>
            connection.Account is null ? new AsmrState() : accountState.Get<AsmrState>(connection.Account.Uid, "lobby.asmr") ?? new AsmrState();

        private void Save(Connection connection, AsmrState state)
        {
            if (connection.Account is not null)
                accountState.Set(connection.Account.Uid, "lobby.asmr", state);
        }

        public override void HandleLobbyAsmrInfo(CS_LobbyAsmrInfo request, Connection connection)
        {
            AsmrState state = Load(connection);
            SC_LobbyAsmrInfo reply = new();
            reply.IIIOINLCDBH.AddRange(state.Scenes);
            reply.CPHCGPHMHKA.AddRange(state.Guns);
            connection.Send(reply);
        }

        public override void HandleLobbyAsmrUnlockScene(CS_LobbyAsmrUnlockScene request, Connection connection)
        {
            AsmrState state = Load(connection);
            if (!state.Scenes.Contains(request.IAAPPCGFLMD))
            {
                state.Scenes.Add(request.IAAPPCGFLMD);
                Save(connection, state);
            }

            connection.Send(new SC_LobbyAsmrUnlockScene { IAAPPCGFLMD = request.IAAPPCGFLMD });
        }

        public override void HandleLobbyAsmrUnlockGun(CS_LobbyAsmrUnlockGun request, Connection connection)
        {
            AsmrState state = Load(connection);
            if (!state.Guns.Contains(request.GunId))
            {
                state.Guns.Add(request.GunId);
                Save(connection, state);
            }

            connection.Send(new SC_LobbyAsmrUnlockGun { GunId = request.GunId });
        }

        public override void HandleLobbyAsmrEnd(CS_LobbyAsmrEnd request, Connection connection)
        {
            connection.Send(new SC_LobbyAsmrEnd());
        }
    }
}
