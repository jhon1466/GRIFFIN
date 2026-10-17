using Google.Protobuf;
using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Protocol;
using Griffin.GameServer.Extensions;
using Griffin.Common.Table;
using Griffin.GameServer.Services;
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
        IRewardService rewardService) : WeaponHandlerBase
    {
        public override void HandleWeaponModAdvanceEnhance(CS_WeaponModAdvanceEnhance request, Connection connection) =>
            connection.Refuse(MsgId.MsgCsWeaponModAdvanceEnhance, HNINDAOJCKP.WeaponModAdvanceEnhanceLocked);

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
