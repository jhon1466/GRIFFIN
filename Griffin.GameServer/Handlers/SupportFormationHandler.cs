using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Protocol;
using Griffin.GameServer.Extensions;

namespace Griffin.GameServer.Handlers
{
    /// <summary>Support formations are built from chips; the account owns none, so every edit is refused as "chip not found".</summary>
    public sealed class SupportFormationHandler : SupportFormationHandlerBase
    {
        public override void HandleSupportFormationModify(CS_SupportFormationModify request, Connection connection) =>
            connection.Refuse(MsgId.MsgCsSupportFormationModify, HNINDAOJCKP.SupportBaseChipNotFound);

        public override void HandleSupportFormationModifyCoreChip(CS_SupportFormationModifyCoreChip request, Connection connection) =>
            connection.Refuse(MsgId.MsgCsSupportFormationModifyCoreChip, HNINDAOJCKP.SupportCoreChipNotFound);
    }
}
