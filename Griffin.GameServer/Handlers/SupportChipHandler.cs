using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Protocol;
using Griffin.GameServer.Extensions;

namespace Griffin.GameServer.Handlers
{
    public sealed class SupportChipHandler : SupportChipHandlerBase
    {
        /// <summary>No support chips are owned (the chip inventory is not simulated), so the screen opens empty.</summary>
        // No support chips exist on this account (the inventory above is empty by design), so acting on one is refused as "not found".
        public override void HandleSupportChipBreak(CS_SupportChipBreak request, Connection connection) =>
            connection.Refuse(MsgId.MsgCsSupportChipBreak, HNINDAOJCKP.SupportCoreChipNotFound);

        public override void HandleSupportChipDecompose(CS_SupportChipDecompose request, Connection connection) =>
            connection.Refuse(MsgId.MsgCsSupportChipDecompose, HNINDAOJCKP.SupportBaseChipNotFound);

        public override void HandleSupportChip(CS_SupportChip request, Connection connection) =>
            connection.Send(new SC_SupportChip());
    }
}
