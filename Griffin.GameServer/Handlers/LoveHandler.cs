using Google.Protobuf;
using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Protocol;
using Griffin.GameServer.Extensions;
using Griffin.Common.Table;
using Griffin.Database.Entities;
using Griffin.Database.Services;
using Griffin.GameServer.Services;
using Serilog;

namespace Griffin.GameServer.Handlers
{
    /// <summary>
    /// Affection (好感) of a character: gifts, direct upgrade and the vow. Read by field number from the 1189260 tables:
    /// <c>LoveLevelData</c> (id = level 1..9; column 2 = total experience needed to leave that level, level 8 holds the cap 41500;
    /// level 9 is the vow) and <c>LoveGiftData</c> (id = gift item, column 3 = love experience per item).
    /// Response fields: <c>SC_LoveSendGift</c> = level (2), pending love-up demand (3, always 0 here) and, in the 64-bit field, the experience;
    /// the other upgrade messages carry (level, pending demand). The obfuscated names are NOT consistent between messages: trust the field
    /// order (verified in the client: HJNOFBJBMEN.IKMBKIOIFAB / NFNMCCKMCMC). Assumptions: no gift preference multipliers, "upgrade directly" is free and fills the
    /// experience of the current level, the vow needs level 8 with the experience cap and costs nothing.
    /// </summary>
    public sealed class LoveHandler(
        IAccountService accountService,
        IGunService gunService,
        IItemService itemService,
        ITableService tableService,
        IDailyQuestProgressService questProgress,
        IRewardService rewardService,
        IItemUseService itemUse,
        ILoveService love) : LoveHandlerBase
    {
        private const uint VowLevel = 9;

        public override void HandleLoveSendGift(CS_LoveSendGift request, Connection connection)
        {
            if (connection.Account is null)
                return;

            if (Find(connection.Account.Uid, request.GunId) is not { } gun)
            {
                connection.Refuse(MsgId.MsgCsLoveSendGift, HNINDAOJCKP.LoveNotHasGun);
                return;
            }

            uint uid = connection.Account.Uid;
            if (gun.LoveLevel >= LevelCap() && gun.LoveExp >= Threshold(LevelCap()))
            {
                connection.Refuse(MsgId.MsgCsLoveSendGift, HNINDAOJCKP.LoveLevelMax);
                return;
            }

            // the bar of the level is full and the event of the level is pending: gifts are no longer accepted (the client shows the event instead)
            if (love.Pending(gun) is not null)
            {
                connection.Refuse(MsgId.MsgCsLoveSendGift, HNINDAOJCKP.LoveExpOverflow);
                return;
            }

            Dictionary<uint, int> spend = [];
            double gainedExact = 0;
            foreach ((uint item, uint count) in request.MLDDFFDLNHD)
            {
                IMessage? row = tableService.GetTable<LoveGiftData>().FirstOrDefault(g => TableFields.U(g, 1) == item);
                if (row is null || count == 0)
                {
                    Log.Warning("LoveSendGift: item {Item} is not a gift.", item);
                    connection.Refuse(MsgId.MsgCsLoveSendGift, HNINDAOJCKP.LoveGiftIdInvalid);
                    return;
                }

                spend[item] = (int)count;
                gainedExact += TableFields.U(row, 3) * GiftMultiplier(gun.GunId, item) * count;
            }

            if (spend.Count == 0 || !itemService.TrySpend(uid, spend, out SC_Items spent))
            {
                Log.Warning("LoveSendGift: account {Uid} does not own the gifts.", uid);
                connection.Refuse(MsgId.MsgCsLoveSendGift, HNINDAOJCKP.LoveGiftNumEnough);
                return;
            }

            long gained = (long)Math.Round(gainedExact, MidpointRounding.AwayFromZero);
            love.AddExperience(gun, gained);
            gunService.Save();
            List<IMessage> bundle = [spent];
            questProgress.AddCondition(uid, conditionClass: 1303, link: "4015", amount: (uint)spend.Values.Sum());
            Log.Information("LoveSendGift: account {Uid} gun {Gun} +{Gained} -> level {Level} exp {Exp}.", uid, gun.GunId, gained, gun.LoveLevel, gun.LoveExp);
            bundle.Add(new SC_LoveSendGift
            {
                GunId = gun.GunId,
                // The client applies (level, pending) from the two 32-bit fields (HJNOFBJBMEN.IKMBKIOIFAB / NFNMCCKMCMC: setters ENJHBBDMJPB(level) and
                // DNAGJPBMNGE(pending)): the SECOND one is the id of a pending love-up demand and any value other than 0 switches the affection panel to
                // the "complete the companion event" mode with the TEXT button (AKKPGODAEGM: Love.f3 != 0). This server levels up by experience alone,
                // so nothing is ever pending: it is always 0 (sending the experience there left the panel stuck on TEXT).
                JLHFICPMAEI = gun.LoveLevel,
                // the pending event (A-55): its suffix switches the panel to "event mode" (story / demand button)
                FDKIHAPHHPD = love.Pending(gun)?.Suffix ?? 0,
                // the 64-bit field is the experience the affection screen shows ("1792136112/840" appeared when it carried the clock time)
                JKGBIKEDACK = gun.LoveExp,
            });
            connection.SendAutoEncrypted(bundle.ToArray());
        }

        public override void HandleLoveUpgradeDirectly(CS_LoveUpgradeDirectly request, Connection connection)
        {
            if (connection.Account is null)
                return;

            if (Find(connection.Account.Uid, request.GunId) is not { } gun)
            {
                connection.Refuse(MsgId.MsgCsLoveUpgradeDirectly, HNINDAOJCKP.LoveNotHasGun);
                return;
            }

            if (gun.LoveLevel >= LevelCap())
            {
                connection.Refuse(MsgId.MsgCsLoveUpgradeDirectly, HNINDAOJCKP.LoveLevelMax);
                return;
            }

            // completes the pending event of the level for free (filling the bar first when needed): one level per call (A-55)
            if (gun.LoveLevel == 0)
                gun.LoveLevel = 1;
            love.AddExperience(gun, Math.Max(0, Threshold(gun.LoveLevel) - gun.LoveExp));
            List<IMessage> upgrade = [];
            love.Advance(connection.Account.Uid, gun, upgrade);
            gunService.Save();
            // client order of this message: field 2 = pending event, field 3 = level
            upgrade.Add(new SC_LoveUpgradeDirectly { GunId = gun.GunId, FDKIHAPHHPD = love.Pending(gun)?.Suffix ?? 0, JLHFICPMAEI = gun.LoveLevel });
            connection.SendAutoEncrypted(upgrade.ToArray());
        }

        public override void HandleLoveVow(CS_LoveVow request, Connection connection)
        {
            if (connection.Account is null)
                return;

            if (Find(connection.Account.Uid, request.GunId) is not { } gun)
            {
                connection.Refuse(MsgId.MsgCsLoveVow, HNINDAOJCKP.LoveNotHasGun);
                return;
            }

            if (gun.LoveLevel >= VowLevel)
            {
                connection.Refuse(MsgId.MsgCsLoveVow, HNINDAOJCKP.LoveAlreadyVowed);
                return;
            }

            if (gun.LoveLevel != LevelCap() || gun.LoveExp < Threshold(gun.LoveLevel))
            {
                Log.Information("LoveVow: gun {Gun} is not at the maximum love ({Level}/{Exp}).", gun.GunId, gun.LoveLevel, gun.LoveExp);
                connection.Refuse(MsgId.MsgCsLoveVow, HNINDAOJCKP.LoveLevelCanNotVow);
                return;
            }

            uint beforeVow = gun.LoveLevel;
            gun.LoveLevel = VowLevel;
            gunService.Save();
            List<IMessage> vow = [];
            GrantLevelRewards(connection.Account.Uid, beforeVow, VowLevel, vow);
            vow.Add(new SC_LoveVow { GunId = gun.GunId, PCPEAMOJPCF = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
            connection.SendAutoEncrypted(vow.ToArray());
        }

        /// <summary>
        /// Pays the reward of every level reached (<c>LoveLevelData</c> col 7: item and amount of that level; level 2 stamina ticket, 3 and 5 gacha tickets,
        /// 4 an accessory item, 8 diamonds). ASSUMPTION A-32: the server pays them when the level is reached (the client has no request to claim them; its
        /// reward dialog only lists them) and not retroactively for levels reached before this change.
        /// </summary>
        private void GrantLevelRewards(uint uid, uint fromLevel, uint toLevel, List<IMessage> messages)
        {
            for (uint level = fromLevel + 1; level <= toLevel; level++)
            {
                IMessage? row = tableService.GetTable<LoveLevelData>().FirstOrDefault(l => TableFields.U(l, 1) == level);
                if (row is null)
                    continue;

                foreach ((uint item, uint count) in TableFields.Map(row, 7))
                    rewardService.Grant(uid, item, (int)count, messages);
            }
        }

        /// <summary>Highest level that experience can reach (the one before the vow).</summary>
        private uint LevelCap() => love.Cap;

        /// <summary>Total experience at which the given level is completed.</summary>
        private long Threshold(uint level) => love.Threshold(level);

        /// <summary>
        /// Preference of the character for a gift (<c>LoveGiftSpecialData</c>: the character's three lists of gifts, columns 2, 3 and 4, give
        /// <c>LoveConfigData</c> columns 2, 3 and 4 percent more experience: x1.75, x1.5, x1.25). The client shows this amount on the bar
        /// when a gift is selected (<c>FPFDEFLDFDK</c>) and the server has to grant the same. ASSUMPTION A-57 (from the client formula).
        /// </summary>
        private double GiftMultiplier(uint gunId, uint item)
        {
            IMessage? special = tableService.GetTable<LoveGiftSpecialData>().FirstOrDefault(r => TableFields.U(r, 1) == gunId);
            IMessage? config = tableService.GetTable<LoveConfigData>().FirstOrDefault();
            if (special is null || config is null)
                return 1;

            foreach (int column in new[] { 2, 3, 4 })
                if (TableFields.List(special, column).Contains(item))
                    return 1 + TableFields.U(config, column) * 0.01;
            return 1;
        }

        private GunEntity? Find(uint uid, uint gunId) =>
            accountService.GetByUid(uid)?.Guns.FirstOrDefault(g => g.GunId == gunId);

        /// <summary>
        /// Pays the demand of the pending event (type 1, levels 1 to 4) and raises the level (A-55). The reply order of the client is
        /// field 2 = pending event, field 3 = level. A story/battle event (type 2) is not paid: it is completed by winning its stage.
        /// </summary>
        public override void HandleLoveSendEventGift(CS_LoveSendEventGift request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            lock (AccountLock.For(uid))
            {
                GunEntity? gun = Find(uid, request.GunId);
                if (gun is null)
                {
                    connection.Refuse(MsgId.MsgCsLoveSendEventGift, HNINDAOJCKP.LoveNotHasGun);
                    return;
                }

                if (love.Pending(gun) is not { } pending)
                {
                    connection.Refuse(MsgId.MsgCsLoveSendEventGift, HNINDAOJCKP.LoveNotInEvent);
                    return;
                }

                if (pending.Type != 1)
                {
                    connection.Refuse(MsgId.MsgCsLoveSendEventGift, HNINDAOJCKP.LoveEventTypeWrong);
                    return;
                }

                List<IMessage> messages = [];
                Dictionary<uint, int> demand = [];
                foreach ((uint item, uint count) in pending.Demands)
                    demand[item] = demand.GetValueOrDefault(item) + (int)count;
                if (demand.Count > 0 && !itemUse.Spend(uid, demand, messages))
                {
                    connection.Refuse(MsgId.MsgCsLoveSendEventGift, HNINDAOJCKP.LoveGiftNumEnough);
                    return;
                }

                love.Advance(uid, gun, messages);
                gunService.Save();
                Log.Information("LoveSendEventGift: account {Uid} gun {Gun} completed event {Event} -> level {Level}.", uid, gun.GunId, pending.Id, gun.LoveLevel);
                messages.Add(new SC_LoveSendEventGift { GunId = gun.GunId, FDKIHAPHHPD = love.Pending(gun)?.Suffix ?? 0, JLHFICPMAEI = gun.LoveLevel });
                connection.SendAutoEncrypted(messages.ToArray());
            }
        }
    }
}
