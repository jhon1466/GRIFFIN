using Google.Protobuf;
using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Protocol;
using Griffin.GameServer.Extensions;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    /// <summary>Keepsakes that offer a choice (<c>ItemData</c> type 134, payload = the offered item ids): the chosen one is granted per unit.</summary>
    public sealed class GiftPickHandler(IItemUseService itemUse) : GiftPickHandlerBase
    {
        public override void HandleUseGiftPick(CS_UseGiftPick request, Connection connection)
        {
            if (connection.Account is null)
                return;

            List<IMessage> messages = [];
            HNINDAOJCKP error = itemUse.UseGiftPick(connection.Account.Uid, request.Item, request.Select.ToList(), request.Num, messages);
            if (error != HNINDAOJCKP.None)
            {
                connection.Refuse(MsgId.MsgCsUseGiftPick, error);
                return;
            }

            messages.Add(new SC_UseGiftPick { Item = request.Item, Num = request.Num });
            connection.SendAutoEncrypted(messages.ToArray());
        }
    }
}
