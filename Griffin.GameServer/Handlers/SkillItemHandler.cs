using Griffin.Common.Networking;
using Griffin.Common.Proto;

namespace Griffin.GameServer.Handlers
{
    public sealed class SkillItemHandler : SkillItemHandlerBase
    {
        public override void HandleGetDelTalentSkillItems(CS_GetDelTalentSkillItems request, Connection connection)
        {
            connection.Send(new SC_GetDelTalentSkillItems());
        }

        // The three public talent items of the account are fixed, so a dismantle is only confirmed.
        public override void HandlePublicSkillItemDismantle(CS_PublicSkillItemDismantle request, Connection connection) =>
            connection.Send(new SC_PublicSkillItemDismantle { Ids = { request.Ids } });
    }
}
