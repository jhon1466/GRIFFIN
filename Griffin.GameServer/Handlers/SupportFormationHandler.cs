using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Protocol;
using Griffin.Common.Table;
using Griffin.GameServer.Extensions;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    /// <summary>
    /// Support formations (A-77): the core chips of a formation are saved (up to <see cref="SupportChips.FormationSlots"/>, no chip twice) and the formation chosen
    /// for battle is kept; the base chips and the teams of the formation are not simulated.
    /// </summary>
    public sealed class SupportFormationHandler(ITableService tableService, IAccountStateService accountState) : SupportFormationHandlerBase
    {
        public override void HandleSupportFormationModify(CS_SupportFormationModify request, Connection connection) =>
            connection.Refuse(MsgId.MsgCsSupportFormationModify, HNINDAOJCKP.SupportBaseChipNotFound);

        public override void HandleSupportFormationModifyCoreChip(CS_SupportFormationModifyCoreChip request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            List<uint> wanted = request.JHCLPBBJDFH.Where(id => id != 0).Distinct().ToList();
            List<SupportChips.ChipState> chips = SupportChips.Chips(tableService, accountState, uid);
            if (wanted.Any(id => chips.All(chip => chip.Id != id)))
            {
                connection.Refuse(MsgId.MsgCsSupportFormationModifyCoreChip, HNINDAOJCKP.SupportCoreChipNotFound);
                return;
            }

            if (wanted.Count > SupportChips.FormationSlots)
            {
                connection.Refuse(MsgId.MsgCsSupportFormationModifyCoreChip, HNINDAOJCKP.ClientInputInvalid);
                return;
            }

            SupportChips.SetFormationChips(accountState, uid, (uint)request.Id, wanted);
            SC_SupportFormationModifyCoreChip reply = new() { Id = (uint)request.Id };
            reply.ENFCKIOLJNM.AddRange(wanted);
            connection.Send(reply);
        }

        public override void HandleSupportFormationSetBattleFormationId(CS_SupportFormationSetBattleFormationId request, Connection connection)
        {
            if (connection.Account is null)
                return;

            accountState.Set<uint?>(connection.Account.Uid, SupportChips.BattleKey, request.Id);
            connection.Send(new SC_SupportFormationSetBattleFormationId { Id = request.Id });
        }
    }
}
