using Google.Protobuf;
using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Protocol;
using Griffin.GameServer.Extensions;
using Griffin.Common.Table;
using Griffin.GameServer.Services;
using Griffin.Database.Entities;
using Griffin.Database.Services;
using Serilog;

namespace Griffin.GameServer.Handlers
{
    /// <summary>
    /// Weapon synthesis: <c>GunWeaponData</c> column 22 is the blueprint recipe of one weapon (item → amount); the recipes of the
    /// requested weapons are charged together and the weapons are granted. A recipe that is missing refuses the whole request.
    /// </summary>
    public sealed class WeaponHandler(
        ITableService tableService,
        IItemUseService itemUse,
        IRewardService rewardService,
        IAccountStateService accountState,
        IInventoryService inventoryService,
        IWeaponModService weaponMods) : WeaponHandlerBase
    {
        /// <summary>
        /// Refinement of a polarized mod (the "polarity plus" panel): the request maps the slot of each affix to the number of steps bought. A step raises the value of
        /// the affix by one step of its row until its maximum and then one polarity level; the steps cost the <c>ModRefineData</c> rows that follow the ones
        /// already bought (items and gold). The answer is the mod with its new affixes, polarity and count (A-78).
        /// </summary>
        public override void HandleWeaponModAdvanceEnhance(CS_WeaponModAdvanceEnhance request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            lock (AccountLock.For(uid))
            {
                WeaponModEntity? mod = inventoryService.GetPlayerInventory<WeaponModEntity>(uid).FirstOrDefault(m => m.Id == request.Id);
                if (mod is null)
                {
                    connection.Refuse(MsgId.MsgCsWeaponModAdvanceEnhance, HNINDAOJCKP.GunWeaponModNotFound);
                    return;
                }

                ModRefineState refine = ModPolarity.Get(accountState, uid, mod.Id);
                ulong affixes = mod.Field7, polarity = refine.Polarity;
                int total = 0;
                foreach ((uint slot, uint steps) in request.NOADAJNECCF)
                {
                    if (steps == 0)
                        continue;

                    if (slot > 3 || !ModPolarity.Refine(tableService, mod.WeaponModId, ref affixes, ref polarity, (int)slot, (int)steps))
                    {
                        connection.Refuse(MsgId.MsgCsWeaponModAdvanceEnhance, HNINDAOJCKP.WeaponModAdvanceEnhanceLocked);
                        return;
                    }

                    total += (int)steps;
                }

                if (total == 0 || ModPolarity.RefineCost(tableService, refine.Count, total) is not { } cost)
                {
                    connection.Refuse(MsgId.MsgCsWeaponModAdvanceEnhance, HNINDAOJCKP.WeaponModAdvanceEnhanceLocked);
                    return;
                }

                List<IMessage> messages = [];
                if (!itemUse.Spend(uid, cost, messages))
                {
                    connection.Refuse(MsgId.MsgCsWeaponModAdvanceEnhance, HNINDAOJCKP.ItemNotEnough);
                    return;
                }

                mod.Field7 = affixes;
                refine.Polarity = polarity;
                refine.Count += (uint)total;
                ModPolarity.Set(accountState, uid, mod.Id, refine);
                weaponMods.Save();
                messages.Add(new SC_WeaponModAdvanceEnhance { Mod = ModPolarity.ToProto(tableService, accountState, uid, mod) });
                messages.Add(new SC_GunWeaponModLvUp { Id = mod.Id, Level = Math.Max(1, mod.Level), Exp = mod.Field6 });
                messages.AddRange(GunWeaponHandler.RefreshBatch(tableService, accountState, inventoryService, weaponMods, uid));
                connection.SendAutoEncrypted(messages.ToArray());
            }
        }

        /// <summary>
        /// Answer to the dialog that follows a polarization: "confirm" (false) keeps the result, "redo" (true) restores the polarity and the count the mod had before it
        /// (the client restores its own copy at the same time; the mod is sent again so both agree). Nothing is refunded (A-78).
        /// </summary>
        public override void HandleWeaponModPolarityRollBack(CS_WeaponModPolarityRollBack request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            lock (AccountLock.For(uid))
            {
                WeaponModEntity? mod = inventoryService.GetPlayerInventory<WeaponModEntity>(uid).FirstOrDefault(m => m.Id == request.FNKBBPNGFBC);
                if (mod is null)
                {
                    connection.Refuse(MsgId.MsgCsWeaponModPolarityRollBack, HNINDAOJCKP.ModPolarityBackUpIdNotMatch);
                    return;
                }

                ModRefineState refine = ModPolarity.Get(accountState, uid, mod.Id);
                if (!refine.HasBackup)
                {
                    connection.Refuse(MsgId.MsgCsWeaponModPolarityRollBack, HNINDAOJCKP.ModPolarityBackUpNotExist);
                    return;
                }

                if (request.NGODDODILBB)
                {
                    refine.Polarity = refine.BackupPolarity;
                    refine.Count = refine.BackupCount;
                }

                refine.HasBackup = false;
                refine.BackupPolarity = 0;
                refine.BackupCount = 0;
                ModPolarity.Set(accountState, uid, mod.Id, refine);
                List<IMessage> rollBack =
                [
                    new SC_WeaponModAdvanceEnhance { Mod = ModPolarity.ToProto(tableService, accountState, uid, mod) },
                    new SC_GunWeaponModLvUp { Id = mod.Id, Level = Math.Max(1, mod.Level), Exp = mod.Field6 },
                    new SC_WeaponModPolarityRollBack(),
                ];
                rollBack.AddRange(GunWeaponHandler.RefreshBatch(tableService, accountState, inventoryService, weaponMods, uid));
                connection.SendAutoEncrypted<IMessage>(rollBack.ToArray());
            }
        }

        public override void HandleWeaponCompose(CS_WeaponCompose request, Connection connection)
        {
            if (connection.Account is null || request.WeaponAndNum.Count == 0)
                return;

            uint uid = connection.Account.Uid;
            Dictionary<uint, int> cost = [];
            foreach ((uint weaponId, uint num) in request.WeaponAndNum)
            {
                GunWeaponData? data = tableService.GetTable<GunWeaponData>().FirstOrDefault(w => w.Id == weaponId);
                IReadOnlyList<(uint Key, uint Value)> recipe = data is null ? [] : TableFields.Map(data, 22);
                if (num == 0 || recipe.Count == 0)
                {
                    Log.Warning("WeaponCompose: weapon {Weapon} has no recipe.", weaponId);
                    connection.Refuse(MsgId.MsgCsWeaponCompose, HNINDAOJCKP.GunWeaponConfigNotExist);
                    return;
                }

                foreach ((uint item, uint amount) in recipe)
                    cost[item] = cost.GetValueOrDefault(item) + (int)(amount * num);
            }

            List<IMessage> messages = [];
            if (!itemUse.Spend(uid, cost, messages))
            {
                connection.Refuse(MsgId.MsgCsWeaponCompose, HNINDAOJCKP.ItemNotEnough);
                return;
            }

            foreach ((uint weaponId, uint num) in request.WeaponAndNum)
                for (uint i = 0; i < num; i++)
                    rewardService.Grant(uid, weaponId, 1, messages);

            SC_WeaponCompose response = new();
            foreach ((uint weaponId, uint num) in request.WeaponAndNum)
                response.WeaponAndNum[weaponId] = num;
            messages.Add(response);
            connection.SendAutoEncrypted(messages.ToArray());
        }
    }
}
