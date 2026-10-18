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
        /// <summary>CS: formation (1), slot (2, 1-based), team (3, 0 = take it out). Answers the formation as the room shows it (A-93).</summary>
        public override void HandleSupportFormationModify(CS_SupportFormationModify request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            if (request.Id is 0 || request.Id > SupportTeams.Formations(tableService) || request.Slot is 0 or > SupportTeams.Slots)
            {
                connection.Refuse(MsgId.MsgCsSupportFormationModify, HNINDAOJCKP.ClientInputInvalid);
                return;
            }

            (uint slotType, bool open) = SupportTeams.SlotInfo(tableService, (int)request.Slot);
            if (!open)
            {
                connection.Refuse(MsgId.MsgCsSupportFormationModify, HNINDAOJCKP.SupportTeamSlotLocked);
                return;
            }

            uint team = request.COCHLPMJPOJ;
            if (team != 0 && (!SupportTeams.Owned(tableService).Contains(team) || SupportTeams.TypeOf(tableService, team) != slotType))
            {
                connection.Refuse(MsgId.MsgCsSupportFormationModify, HNINDAOJCKP.SupportNotFound);
                return;
            }

            List<SupportTeams.SlotState> slots = SupportTeams.SlotsOf(accountState, uid, request.Id);
            if (team != 0)
                foreach (SupportTeams.SlotState other in slots.Where(s => s.Team == team))
                {
                    other.Team = 0;
                    other.Ammunition = [];
                }

            slots[(int)request.Slot - 1].Team = team;
            slots[(int)request.Slot - 1].Ammunition = [];
            SupportTeams.Save(accountState, uid, request.Id, slots);
            connection.Send(new SC_SupportFormationModify { Id = request.Id, FOAHOCCEHMB = FormationMessage(uid, request.Id) });
        }

        /// <summary>CS: formation (1), slot (2), the order of the ammunition of the team in it (3).</summary>
        public override void HandleSupportFormationAmmunitionSequence(CS_SupportFormationAmmunitionSequence request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            if (request.Id is 0 || request.Slot is 0 or > SupportTeams.Slots)
            {
                connection.Refuse(MsgId.MsgCsSupportFormationAmmunitionSequence, HNINDAOJCKP.ClientInputInvalid);
                return;
            }

            List<SupportTeams.SlotState> slots = SupportTeams.SlotsOf(accountState, uid, request.Id);
            slots[(int)request.Slot - 1].Ammunition = request.JIHKJNEPGBB.ToList();
            SupportTeams.Save(accountState, uid, request.Id, slots);
            SC_SupportFormationAmmunitionSequence reply = new() { Id = request.Id, Slot = request.Slot };
            reply.JIHKJNEPGBB.AddRange(request.JIHKJNEPGBB);
            connection.Send(reply);
        }

        /// <summary>CS: formation (1) and, per frame (stat 1..6), the chips of its slots (0 = empty). Kept as sent; the chips must be owned and none placed twice (A-97).</summary>
        public override void HandleSupportFormationModifyBaseChip(CS_SupportFormationModifyBaseChip request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            HashSet<uint> owned = SupportBaseChips.All(tableService).Select(chip => chip.Id).ToHashSet();
            Dictionary<uint, List<uint>> placed = [];
            HashSet<uint> seen = [];
            foreach ((uint frame, DKIBNBANDLM slots) in request.JELCIHPBECA)
            {
                foreach (uint id in slots.CCGPCPEPBEA.Where(id => id != 0))
                    if (frame is < 1 or > 6 || !owned.Contains(id) || !seen.Add(id))
                    {
                        connection.Refuse(MsgId.MsgCsSupportFormationModifyBaseChip, HNINDAOJCKP.SupportBaseChipNotFound);
                        return;
                    }

                placed[frame] = slots.CCGPCPEPBEA.ToList();
            }

            SupportBaseChips.SetPlaced(accountState, uid, request.Id, placed);
            SC_SupportFormationModifyBaseChip reply = new() { Id = request.Id };
            foreach ((uint frame, DKIBNBANDLM slots) in request.JELCIHPBECA)
                reply.JELCIHPBECA[frame] = slots;
            connection.Send(reply);
        }

        private OMBHFHECHOO FormationMessage(uint uid, uint formation)
        {
            OMBHFHECHOO message = new() { Name = "" };
            for (int slot = 0; slot < SupportTeams.Slots; slot++)
                message.CDLNJHGCDMK.Add(new EFLMEODNBLD());
            for (int property = 1; property <= 6; property++)
                message.GDLFJIJOMAL[property] = 20;
            message.ENFCKIOLJNM.AddRange(SupportChips.FormationChips(accountState, uid, formation));
            SupportTeams.Fill(accountState, uid, formation, message);
            SupportBaseChips.Fill(accountState, uid, formation, message);
            return message;
        }

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
