using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Table;
using Griffin.Common.Protocol;
using Griffin.Database.Entities;
using Griffin.GameServer.Extensions;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    public sealed class DormHandler(IDailyQuestProgressService questProgress, IGunService gunService, IAccountStateService accountState, ITableService tableService, ICostumeService costumes) : DormHandlerBase
    {
        /// <summary>
        /// The dormitory character chosen with <c>DormSetLove</c> (stored in <c>dorm.love</c>); until one is chosen, the one of the original
        /// capture (1032) when the account owns it, otherwise the first owned character.
        /// </summary>
        private uint LoveGun(uint uid, GunEntity[] guns)
        {
            uint stored = accountState.Get<uint>(uid, "dorm.love");
            if (stored != 0 && guns.Any(g => g.GunId == stored))
                return stored;
            return guns.Any(g => g.GunId == 1032) ? 1032 : guns.FirstOrDefault()?.GunId ?? 0;
        }

        private const string CostumesKey = "dorm.costumes";
        public const string PartsKey = "dorm.parts";

        /// <summary>
        /// The dormitory costume of each character (<c>SC_DormInfo.info.PIOFFPPPEPK[gun].PPMIBJEJABD</c>) and the costume parts of each costume
        /// (<c>GDJOKNOBNBM[costume]</c>, 273 typically) chosen by the player replace the ones of the capture. The dormitory needs DORMITORY
        /// costume variants: the battle costume of a character put the models in T-pose (no dormitory animation), so nothing is invented here:
        /// only costumes the client itself sent are stored.
        /// </summary>
        private SC_DormInfo WithStoredCostumes(Connection connection, SC_DormInfo reply)
        {
            if (connection.Account is null || reply.Info is null)
                return reply;

            uint uid = connection.Account.Uid;
            foreach ((uint gun, uint costume) in accountState.Get<Dictionary<uint, uint>>(uid, CostumesKey) ?? [])
            {
                if (!reply.Info.PIOFFPPPEPK.TryGetValue(gun, out IIAPLDFGPNF? entry))
                    reply.Info.PIOFFPPPEPK[gun] = entry = new IIAPLDFGPNF { OFOAKMGLEPL = gun };
                entry.PPMIBJEJABD = costume;
            }

            foreach ((uint costume, uint parts) in accountState.Get<Dictionary<uint, uint>>(uid, PartsKey) ?? [])
                reply.GDJOKNOBNBM[costume] = parts;

            // A character the capture does not know has no dormitory costume, so the client loads the animations of an empty model name
            // ("c__Cloth_Idle", seen in the 2026-10-16 client log). ASSUMPTION A-15b: give it an owned costume that has a dormitory model
            // (ClothesData col 29) when it has one; characters without such a costume keep the default.
            HashSet<uint> ownedCostumes = costumes.GetPlayerCostumes(uid).Select(c => c.CostumeId).ToHashSet();
            foreach (GunEntity gun in gunService.GetPlayerGuns(uid))
            {
                if (reply.Info.PIOFFPPPEPK.TryGetValue(gun.GunId, out IIAPLDFGPNF? known) && known.PPMIBJEJABD != 0)
                    continue;

                // the base costume (type 1) already has a dormitory model: keep the default
                if (tableService.GetTable<ClothesData>().Any(c => TableFields.U(c, 21) == gun.GunId && TableFields.U(c, 2) == 1 && !string.IsNullOrEmpty(TableFields.S(c, 29))))
                    continue;

                uint dormCostume = tableService.GetTable<ClothesData>()
                    .Where(c => TableFields.U(c, 21) == gun.GunId && ownedCostumes.Contains(c.Id) && !string.IsNullOrEmpty(TableFields.S(c, 29)))
                    .Select(c => c.Id).DefaultIfEmpty(0u).Min();
                if (dormCostume == 0)
                    continue;

                if (known is null)
                    reply.Info.PIOFFPPPEPK[gun.GunId] = known = new IIAPLDFGPNF { OFOAKMGLEPL = gun.GunId };
                known.PPMIBJEJABD = dormCostume;
            }

            return reply;
        }

        public override void HandleDormSetLove(CS_DormSetLove request, Connection connection)
        {
            if (connection.Account is null)
                return;

            if (gunService.GetPlayerGuns(connection.Account.Uid).All(g => g.GunId != request.GunId))
            {
                connection.Refuse(MsgId.MsgCsDormSetLove, HNINDAOJCKP.GunNotFound);
                return;
            }

            accountState.Set(connection.Account.Uid, "dorm.love", request.GunId);
            connection.Send(new SC_DormSetLove { GunId = request.GunId });
        }

        public override void HandleDormInfo(CS_DormInfo request, Connection connection)
        {
            connection.Send(WithStoredCostumes(connection, new SC_DormInfo
            {
                Info = new NOMHELOJFLJ
                {
                    PIOFFPPPEPK =
                    {
                        [1025] = new IIAPLDFGPNF { OFOAKMGLEPL = 1025, PPMIBJEJABD = 1102501 },
                        [1034] = new IIAPLDFGPNF { OFOAKMGLEPL = 1034, PPMIBJEJABD = 0 },
                        [1039] = new IIAPLDFGPNF { OFOAKMGLEPL = 1039, PPMIBJEJABD = 1103901 },
                        [1042] = new IIAPLDFGPNF { OFOAKMGLEPL = 1042, PPMIBJEJABD = 1104201 },
                        [1045] = new IIAPLDFGPNF { OFOAKMGLEPL = 1045, PPMIBJEJABD = 0 },
                        [1047] = new IIAPLDFGPNF { OFOAKMGLEPL = 1047, PPMIBJEJABD = 1104701 },
                        [1048] = new IIAPLDFGPNF { OFOAKMGLEPL = 1048, PPMIBJEJABD = 1104801 },
                        [1052] = new IIAPLDFGPNF { OFOAKMGLEPL = 1052, PPMIBJEJABD = 1105203 },
                        [1054] = new IIAPLDFGPNF { OFOAKMGLEPL = 1054, PPMIBJEJABD = 1105401 },
                        [1056] = new IIAPLDFGPNF { OFOAKMGLEPL = 1056, PPMIBJEJABD = 1105601 },
                    },
                    // love character: the one chosen with DormSetLove; the original capture's (1032) until then. The per-character dorm costumes
                    // above are dormitory costume variants of the capture: replacing them with the battle costume of each character put the
                    // models in T-pose (no dormitory animation), so they stay as captured.
                    KBCJNGDOGKJ = connection.Account is null ? 1032 : LoveGun(connection.Account.Uid, gunService.GetPlayerGuns(connection.Account.Uid)),
                },
                GDJOKNOBNBM =
                {
                    [1103901] = 273,
                    [1104201] = 273,
                    [1104801] = 273,
                    [1105203] = 545,
                    [1105401] = 273,
                },
            }));
        }

        public override void HandleGetDormFormationInfo(CS_GetDormFormationInfo request, Connection connection)
        {
            SC_GetDormFormationInfo reply = new();
            if (connection.Account is not null)
            {
                foreach ((uint id, bool bought) in accountState.Get<Dictionary<uint, bool>>(connection.Account.Uid, GunHandler.PerformancesKey) ?? [])
                    reply.AHFNBKINDGB[id] = bought;
            }

            connection.Send(reply);
        }

        public override void HandleEnterDorm(CS_EnterDorm request, Connection connection)
        {
            if (connection.Account is not null)
                questProgress.AddCondition(connection.Account.Uid, conditionClass: 750);
            connection.Send(new SC_EnterDorm
            {
                GunId = request.GunId,
            });
        }

        /// <summary>
        /// Puts a costume on a character in the dormitory and keeps it (<c>dorm.costumes</c>, returned by <c>DormInfo</c>). The costume must be one of
        /// the character (<c>ClothesData</c> col 21 = character) and the character must be owned.
        /// </summary>
        public override void HandleDormSkinChange(CS_DormSkinChange request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            if (gunService.GetPlayerGuns(uid).All(g => g.GunId != request.GunId))
            {
                connection.Refuse(MsgId.MsgCsDormSkinChange, HNINDAOJCKP.GunNotFound);
                return;
            }

            if (request.CostumeId != 0 && !tableService.GetTable<ClothesData>().Any(c => c.Id == request.CostumeId && TableFields.U(c, 21) == request.GunId))
            {
                connection.Refuse(MsgId.MsgCsDormSkinChange, HNINDAOJCKP.GunConfigNotExist);
                return;
            }

            Dictionary<uint, uint> costumes = accountState.Get<Dictionary<uint, uint>>(uid, CostumesKey) ?? [];
            costumes[request.GunId] = request.CostumeId;
            accountState.Set(uid, CostumesKey, costumes);
            Dictionary<uint, long> costumeTimes = accountState.Get<Dictionary<uint, long>>(uid, "dorm.costumes.updatedAtMs") ?? [];
            costumeTimes[request.GunId] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            accountState.Set(uid, "dorm.costumes.updatedAtMs", costumeTimes);
            connection.Send(new SC_DormSkinChange { GunId = request.GunId, CostumeId = request.CostumeId });
        }

        // Character reward of the dormitory: no reward table is linked to the request id, so it is confirmed without paying.
        public override void HandleDormGetCharacterReward(CS_DormGetCharacterReward request, Connection connection) =>
            connection.Send(new SC_DormGetCharacterReward { Id = request.Id });
    }
}
