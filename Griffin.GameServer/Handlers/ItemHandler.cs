using Google.Protobuf;
using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Protocol;
using Griffin.GameServer.Extensions;
using Griffin.Database.Entities;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    public sealed class ItemHandler(IInventoryService inventoryService, IItemUseService itemUse) : ItemHandlerBase
    {
        private const int ItemsPerResponse = 7000;

        public override void HandleItems(CS_Items request, Connection connection)
        {
            if (connection.Account == null) return;

            // resources (currencies) travel in SC_Resource, not in the item list
            ItemEntity[] items = inventoryService.GetPlayerInventory<ItemEntity>(connection.Account.Uid)
                .Where(item => item.Type != ResourceService.ResourceItemType)
                .ToArray();

            if (items.Length == 0)
            {
                connection.SendAutoEncrypted(new SC_Items());
                return;
            }

            int offset = 0;
            while (offset < items.Length)
            {
                SC_Items response = new SC_Items();
                int end = Math.Min(offset + ItemsPerResponse, items.Length);

                for (int i = offset; i < end; i++)
                    response.Items[items[i].ItemId] = items[i].Count;

                connection.SendAutoEncrypted(response);
                offset += ItemsPerResponse;
            }
        }

        public override void HandleItemDismantle(CS_ItemDismantle request, Connection connection)
        {
            if (connection.Account is null)
                return;

            List<IMessage> messages = [];
            HNINDAOJCKP error = itemUse.Dismantle(connection.Account.Uid, request.IdAndNum.ToDictionary(p => p.Key, p => p.Value), messages);
            if (error != HNINDAOJCKP.None)
            {
                connection.Refuse(MsgId.MsgCsItemDismantle, error);
                return;
            }

            SC_ItemDismantle response = new();
            foreach ((uint id, uint num) in request.IdAndNum)
                response.IdAndNum[id] = num;
            messages.Insert(0, response);
            connection.SendAutoEncrypted(messages.ToArray());
        }

        public override void HandleComposeItems(CS_ComposeItems request, Connection connection)
        {
            if (connection.Account is null)
                return;

            List<IMessage> messages = [];
            HNINDAOJCKP error = itemUse.Compose(connection.Account.Uid, request.ItemId, request.ItemNum, messages);
            if (error != HNINDAOJCKP.None)
            {
                connection.Refuse(MsgId.MsgCsComposeItems, error);
                return;
            }

            // The client applies the answer to its own counts (adds the composed items, spends the materials): the totals must come AFTER it, or they are applied twice (A-152).
            messages.Insert(0, new SC_ComposeItems { ItemId = request.ItemId, ItemNum = request.ItemNum, UseItems = request.UseItems });
            connection.SendAutoEncrypted(messages.ToArray());
        }

        public override void HandleUseItems(CS_UseItems request, Connection connection)
        {
            if (connection.Account is null)
                return;

            List<IMessage> messages = [];
            HNINDAOJCKP error = itemUse.Use(connection.Account.Uid, request.ItemId, request.Num, messages);
            if (error != HNINDAOJCKP.None)
            {
                connection.Refuse(MsgId.MsgCsUseItems, error);
                return;
            }

            messages.Insert(0, new SC_UseItems { ItemId = request.ItemId, Num = request.Num });
            connection.SendAutoEncrypted(messages.ToArray());
        }
    }
}
