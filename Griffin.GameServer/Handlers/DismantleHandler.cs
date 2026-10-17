using Google.Protobuf;
using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    /// <summary>
    /// Batch dismantling of owned entities by uid. Only weapons are dismantled here (unlocked and unequipped, product from the
    /// weapon table: see <see cref="IWeaponProgressService.Dismantle"/>); the answer lists the uids that were removed.
    /// </summary>
    public sealed class DismantleHandler(IWeaponProgressService weapons) : DismantleHandlerBase
    {
        public override void HandleDismantleEntities(CS_DismantleEntities request, Connection connection)
        {
            if (connection.Account is null)
                return;

            List<IMessage> messages = [];
            IReadOnlyList<uint> removed = weapons.Dismantle(connection.Account.Uid, request.Ids.Select(id => (uint)id), messages);
            SC_DismantleEntities response = new();
            response.Ids.AddRange(removed.Select(id => (ulong)id));
            messages.Add(response);
            connection.SendAutoEncrypted(messages.ToArray());
        }
    }
}
