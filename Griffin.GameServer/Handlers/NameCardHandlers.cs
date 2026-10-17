using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    /// <summary>
    /// Name card gameplay slots: <c>SetNameCardGameplaySlots</c> (stored by <see cref="PreferenceResponder"/>) is what the options query
    /// returns as the selected slots. ASSUMPTION: the list of selectable gameplays (field 1) is empty because the server keeps none.
    /// </summary>
    public sealed class QueryNameCardHandler(IAccountStateService accountState) : QueryNameCardHandlerBase
    {
        public override void HandleQueryNameCardGameplayOptions(CS_QueryNameCardGameplayOptions request, Connection connection)
        {
            SC_QueryNameCardGameplayOptions reply = new();
            if (connection.Account is not null
                && PreferenceResponder.Recall<CS_SetNameCardGameplaySlots>(accountState, connection.Account.Uid, "SetNameCardGameplaySlots") is { } slots)
                reply.GPDFKEMELKI.AddRange(slots.GPDFKEMELKI);
            connection.Send(reply);
        }
    }
}
