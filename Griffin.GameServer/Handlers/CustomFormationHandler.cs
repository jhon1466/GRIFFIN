using Google.Protobuf;
using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    /// <summary>
    /// Custom squads (编队), stored per account in <c>AccountStates</c> (<c>formations</c>: id → serialized formation). The
    /// client sends a whole formation (name + characters with weapon, mods and talents) and it is returned as it was saved.
    /// Assumption: the list the client receives is ordered by formation id starting at 1, with an empty entry for ids that
    /// were never saved (the container has no ids of its own).
    /// </summary>
    public sealed class CustomFormationHandler(IAccountStateService accountState) : CustomFormationHandlerBase
    {
        private const string StateKey = "formations";

        /// <summary>Squads the client always shows (the overview lists at least seven).</summary>
        private const uint DefaultSquads = 7;

        public override void HandleGetCustomFormation(CS_GetCustomFormation request, Connection connection)
        {
            PBEBLABCGEP container = new();
            if (connection.Account is not null)
            {
                // The client creates its squads itself and then fills them with this list by position. An EMPTY list left every squad without data and the
                // squad screen froze as soon as one was edited (the reference server has the same bug: it always sends an empty list). The squad overview of
                // the client shows seven squads at least, so the list always has that many entries (an entry without squads is an empty squad) (A-154).
                Dictionary<uint, string> saved = Load(connection.Account.Uid);
                for (uint id = 1; id <= Math.Max(DefaultSquads, saved.Count == 0 ? 0 : saved.Keys.Max()); id++)
                    container.KMPIHKFBAEP.Add(saved.TryGetValue(id, out string? data) ? Parse(data) : new CLHJOEOIFHP { Name = string.Empty });
            }

            connection.Send(new SC_GetCustomFormation { OGNHICHKFAD = container });
        }

        public override void HandleCustomFormationModify(CS_CustomFormationModify request, Connection connection)
        {
            if (connection.Account is null || request.Id == 0)
                return;

            uint uid = connection.Account.Uid;
            Dictionary<uint, string> saved = Load(uid);
            CLHJOEOIFHP formation = request.FOAHOCCEHMB ?? new CLHJOEOIFHP();
            saved[request.Id] = Convert.ToBase64String(formation.ToByteArray());
            accountState.Set(uid, StateKey, saved);

            connection.Send(new SC_CustomFormationModify { Id = request.Id, FOAHOCCEHMB = formation });
        }

        public override void HandleCustomFormationModifyName(CS_CustomFormationModifyName request, Connection connection)
        {
            if (connection.Account is null || request.Id == 0)
                return;

            uint uid = connection.Account.Uid;
            Dictionary<uint, string> saved = Load(uid);
            CLHJOEOIFHP formation = saved.TryGetValue(request.Id, out string? data) ? Parse(data) : new CLHJOEOIFHP();
            formation.Name = request.Name ?? string.Empty;
            saved[request.Id] = Convert.ToBase64String(formation.ToByteArray());
            accountState.Set(uid, StateKey, saved);

            connection.Send(new SC_CustomFormationModifyName { Id = request.Id, Name = formation.Name });
        }

        public override void HandleCustomFormationCopy(CS_CustomFormationCopy request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            Dictionary<uint, string> saved = Load(uid);
            if (!saved.TryGetValue(request.ENGKHJJNFGD, out string? source))
                return;

            SC_CustomFormationCopy response = new();
            foreach (uint target in request.GDJBIEKCEJC.Distinct().Where(id => id != 0 && id != request.ENGKHJJNFGD))
            {
                saved[target] = source;
                response.FOAHOCCEHMB[target] = Parse(source);
            }

            accountState.Set(uid, StateKey, saved);
            connection.Send(response);
        }

        private Dictionary<uint, string> Load(uint uid) =>
            accountState.Get<Dictionary<uint, string>>(uid, StateKey) ?? [];

        private static CLHJOEOIFHP Parse(string base64) => CLHJOEOIFHP.Parser.ParseFrom(Convert.FromBase64String(base64));
    }
}
