using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Protocol;
using Griffin.Common.Table;
using Griffin.GameServer.Extensions;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    /// <summary>
    /// Support chips (A-77, A-78): the account owns <see cref="SupportChips.CopiesPerChip"/> copies of every core chip (see <see cref="SupportChips"/>); the base chips of the
    /// support teams do not exist in this server. Read from the 4.0.5136 client (the break dialog): breaking a chip consumes other copies of the same chip, each one raising
    /// its break by one up to the last break row, without gold; a copy that is locked, in a formation or already broken needs the confirmation flag of the request.
    /// Decomposing a chip returns the items of <c>SupportChipCoreChipData</c> column 8 (178006 x 50 for a 6-star chip).
    /// </summary>
    public sealed class SupportChipHandler(ITableService tableService, IAccountStateService accountState, IRewardService rewardService) : SupportChipHandlerBase
    {
        private const uint CoreType = 2;

        public override void HandleSupportChipBreak(CS_SupportChipBreak request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            lock (AccountLock.For(uid))
            {
                List<SupportChips.ChipState> chips = SupportChips.Chips(tableService, accountState, uid);
                SupportChips.ChipState? chip = chips.FirstOrDefault(c => c.Id == request.Id);
                if (chip is null)
                {
                    connection.Refuse(MsgId.MsgCsSupportChipBreak, HNINDAOJCKP.SupportCoreChipNotFound);
                    return;
                }

                uint room = SupportChips.MaxBreak(tableService, chip.StcId) - Math.Min(chip.BreakTimes, SupportChips.MaxBreak(tableService, chip.StcId));
                if (room == 0)
                {
                    connection.Refuse(MsgId.MsgCsSupportChipBreak, HNINDAOJCKP.SupportCoreChipMaxBreak);
                    return;
                }

                List<SupportChips.ChipState> materials = [];
                foreach (uint id in request.NNGEBCPGILL.Distinct().Where(id => id != chip.Id))
                {
                    SupportChips.ChipState? material = chips.FirstOrDefault(c => c.Id == id && c.StcId == chip.StcId);
                    if (material is null)
                    {
                        connection.Refuse(MsgId.MsgCsSupportChipBreak, HNINDAOJCKP.SupportCoreChipNotFound);
                        return;
                    }

                    if (!request.MNIPNBBJPJL)
                    {
                        if (material.Locked)
                        {
                            connection.Refuse(MsgId.MsgCsSupportChipBreak, HNINDAOJCKP.SupportCoreChipLocked);
                            return;
                        }

                        if (SupportChips.InFormation(accountState, uid, material.Id))
                        {
                            connection.Refuse(MsgId.MsgCsSupportChipBreak, HNINDAOJCKP.SupportCoreChipInUse);
                            return;
                        }

                        if (material.BreakTimes > 0)
                        {
                            connection.Refuse(MsgId.MsgCsSupportChipBreak, HNINDAOJCKP.SupportCoreChipMaterialBreaked);
                            return;
                        }
                    }

                    materials.Add(material);
                }

                materials = materials.Take((int)room).ToList();
                if (materials.Count == 0)
                {
                    connection.Refuse(MsgId.MsgCsSupportChipBreak, HNINDAOJCKP.ClientInputInvalid);
                    return;
                }

                chip.BreakTimes += (uint)materials.Count;
                List<uint> consumed = materials.Select(m => m.Id).ToList();
                chips.RemoveAll(c => consumed.Contains(c.Id));
                SupportChips.Save(accountState, uid, chips);
                SupportChips.RemoveFromFormations(accountState, uid, consumed);
                SC_SupportChipBreak reply = new() { Id = chip.Id, BreakTimes = chip.BreakTimes };
                reply.NNGEBCPGILL.AddRange(consumed);
                connection.Send(reply);
            }
        }

        public override void HandleSupportChipDecompose(CS_SupportChipDecompose request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            if (request.BHIHPMCMOEL != CoreType)
            {
                connection.Refuse(MsgId.MsgCsSupportChipDecompose, HNINDAOJCKP.SupportBaseChipNotFound);
                return;
            }

            lock (AccountLock.For(uid))
            {
                List<SupportChips.ChipState> chips = SupportChips.Chips(tableService, accountState, uid);
                List<SupportChips.ChipState> chosen = [];
                foreach (uint id in request.Ids.Distinct())
                {
                    SupportChips.ChipState? chip = chips.FirstOrDefault(c => c.Id == id);
                    if (chip is null)
                    {
                        connection.Refuse(MsgId.MsgCsSupportChipDecompose, HNINDAOJCKP.SupportCoreChipNotFound);
                        return;
                    }

                    if (!request.MNIPNBBJPJL)
                    {
                        if (chip.Locked)
                        {
                            connection.Refuse(MsgId.MsgCsSupportChipDecompose, HNINDAOJCKP.SupportCoreChipLocked);
                            return;
                        }

                        if (SupportChips.InFormation(accountState, uid, chip.Id))
                        {
                            connection.Refuse(MsgId.MsgCsSupportChipDecompose, HNINDAOJCKP.SupportCoreChipInUse);
                            return;
                        }
                    }

                    chosen.Add(chip);
                }

                if (chosen.Count == 0)
                {
                    connection.Refuse(MsgId.MsgCsSupportChipDecompose, HNINDAOJCKP.ClientInputInvalid);
                    return;
                }

                List<Google.Protobuf.IMessage> messages = [];
                Dictionary<uint, int> yield = [];
                foreach (SupportChips.ChipState chip in chosen)
                    foreach ((uint item, uint count) in SupportChips.DecomposeYield(tableService, chip.StcId))
                        yield[item] = yield.GetValueOrDefault(item) + (int)count;

                foreach ((uint item, int count) in yield)
                    rewardService.Grant(uid, item, count, messages);

                List<uint> removed = chosen.Select(c => c.Id).ToList();
                chips.RemoveAll(c => removed.Contains(c.Id));
                SupportChips.Save(accountState, uid, chips);
                SupportChips.RemoveFromFormations(accountState, uid, removed);
                SC_SupportChipDecompose reply = new() { BHIHPMCMOEL = request.BHIHPMCMOEL };
                reply.Ids.AddRange(removed);
                messages.Add(reply);
                connection.SendAutoEncrypted(messages.ToArray());
            }
        }

        public override void HandleSupportChipLockUnlock(CS_SupportChipLockUnlock request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            lock (AccountLock.For(uid))
            {
                List<SupportChips.ChipState> chips = SupportChips.Chips(tableService, accountState, uid);
                SupportChips.ChipState? chip = request.BHIHPMCMOEL == CoreType ? chips.FirstOrDefault(c => c.Id == request.Id) : null;
                if (chip is null)
                {
                    connection.Refuse(MsgId.MsgCsSupportChipLockUnlock, request.BHIHPMCMOEL == CoreType ? HNINDAOJCKP.SupportCoreChipNotFound : HNINDAOJCKP.SupportBaseChipNotFound);
                    return;
                }

                chip.Locked = request.MDFIDAFPBJN;
                SupportChips.Save(accountState, uid, chips);
                connection.Send(new SC_SupportChipLockUnlock { BHIHPMCMOEL = request.BHIHPMCMOEL, Id = request.Id, MDFIDAFPBJN = chip.Locked });
            }
        }

        public override void HandleSupportChipBatchLockUnlock(CS_SupportChipBatchLockUnlock request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            lock (AccountLock.For(uid))
            {
                List<SupportChips.ChipState> chips = SupportChips.Chips(tableService, accountState, uid);
                SC_SupportChipBatchLockUnlock reply = new() { BHIHPMCMOEL = request.BHIHPMCMOEL, MDFIDAFPBJN = request.MDFIDAFPBJN };
                if (request.BHIHPMCMOEL == CoreType)
                    foreach (SupportChips.ChipState chip in chips.Where(c => request.Ids.Contains(c.Id)))
                    {
                        chip.Locked = request.MDFIDAFPBJN;
                        reply.Ids.Add(chip.Id);
                    }

                SupportChips.Save(accountState, uid, chips);
                connection.Send(reply);
            }
        }

        /// <summary>The lock plans of chips (auto-lock rules by property) are not stored: every change is echoed.</summary>
        public override void HandleSupportChipLockPlan(CS_SupportChipLockPlan request, Connection connection) =>
            connection.Send(new SC_SupportChipLockPlan { COFDFFJMNMA = request.COFDFFJMNMA, BHIHPMCMOEL = request.BHIHPMCMOEL });

        public override void HandleSupportChipSetLockPlan(CS_SupportChipSetLockPlan request, Connection connection)
        {
            SC_SupportChipSetLockPlan reply = new() { CIHGBNOBABL = request.CIHGBNOBABL, JMCMFMNNENL = request.JMCMFMNNENL, ODMAMCEEMBD = request.ODMAMCEEMBD, OLBOOICEAOO = request.OLBOOICEAOO };
            reply.NBNJKAGMPNK.AddRange(request.NBNJKAGMPNK);
            connection.Send(reply);
        }

        public override void HandleSupportChipDelLockPlan(CS_SupportChipDelLockPlan request, Connection connection) =>
            connection.Send(new SC_SupportChipDelLockPlan { CIHGBNOBABL = request.CIHGBNOBABL, JMCMFMNNENL = request.JMCMFMNNENL, ODMAMCEEMBD = request.ODMAMCEEMBD });

        public override void HandleSupportChip(CS_SupportChip request, Connection connection)
        {
            SC_SupportChip reply = new();
            foreach (SupportBaseChips.Chip baseChip in SupportBaseChips.All(tableService))
                reply.CCGPCPEPBEA.Add(SupportBaseChips.ToProto(baseChip));
            if (connection.Account is not null)
                foreach (SupportChips.ChipState chip in SupportChips.Chips(tableService, accountState, connection.Account.Uid))
                    reply.ENFCKIOLJNM.Add(SupportChips.ToProto(chip));
            connection.Send(reply);
        }
    }
}
