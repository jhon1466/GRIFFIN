using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    /// <summary>
    /// The client reports the time-limited items it found expired (it compares their end time with its own clock) and asks to delete them. The stacks are
    /// removed from the inventory so they are not reported again at the next login; ids the account does not own are ignored. ASSUMPTION A-20: the
    /// server trusts the client's list (the end time of an item is not stored by this server).
    /// </summary>
    public sealed class TimeLimitHandler(IItemService itemService) : TimeLimitHandlerBase
    {
        public override void HandleDeleteTimeLimitItems(CS_DeleteTimeLimitItems request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            SC_Items update = new();
            foreach (uint itemId in request.Items.Distinct())
            {
                foreach (var entity in itemService.GetPlayerItems(uid).Where(i => i.ItemId == itemId).ToList())
                {
                    if (itemService.RemoveItem(uid, entity))
                        update.Items[itemId] = 0;
                }
            }

            SC_DeleteTimeLimitItems reply = new();
            reply.Items.AddRange(request.Items);
            if (update.Items.Count > 0)
                connection.SendAutoEncrypted<Google.Protobuf.IMessage>(update, reply);
            else
                connection.Send(reply);
        }
    }
}
