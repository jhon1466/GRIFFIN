using Google.Protobuf;
using Griffin.Common.Proto;
using Griffin.Common.Table;
using Griffin.Database.Entities;
using Griffin.Database.Services;
using Griffin.GameServer.Extensions;
using Serilog;

namespace Griffin.GameServer.Services
{
    /// <summary>What happened when one item id was granted.</summary>
    public sealed record GrantResult(
        bool Granted,
        uint Relate = 0,
        bool NewCharacter = false,
        bool DuplicateCharacter = false,
        IReadOnlyList<uint>? ModIds = null);

    /// <summary>
    /// Gives items to an account from an item id, the way the shop, gacha, mail and quests all need it: characters (with
    /// their default weapon, costume and collection indices), weapons (with their default skin), resources (currencies) and
    /// plain items. It also builds the messages that tell the client what appeared, in the order of the official captures.
    /// </summary>
    public interface IRewardService : IGameService
    {
        GrantResult Grant(uint accountUid, uint itemId, int count, List<IMessage> messages);

        /// <summary>"110030:1,110037:2;" → [(110030,1),(110037,2)].</summary>
        IReadOnlyList<(uint ItemId, int Count)> ParseRewards(string? text);
    }

    public sealed class RewardService(
        IInventoryService inventoryService,
        IAccountService accountService,
        ITableService tableService,
        IResourceService resourceService,
        IItemService itemService,
        IStaminaService staminaService,
        IWeaponModService weaponModService) : IRewardService
    {
        private const uint StaminaItemId = 101;

        public GrantResult Grant(uint accountUid, uint itemId, int count, List<IMessage> messages)
        {
            if (itemId == 0 || count <= 0)
                return new GrantResult(false);

            AccountEntity account = accountService.GetByUid(accountUid)
                ?? throw new InvalidOperationException($"Account with uid '{accountUid}' was not found.");

            if (tableService.GetTable<GunData>().Any(g => g.Id == itemId))
                return GrantGun(accountUid, account, itemId, messages);

            if (tableService.GetTable<GunWeaponData>().Any(w => w.Id == itemId))
            {
                uint relate = 0;
                for (int i = 0; i < count; i++)
                    relate = GrantWeapon(accountUid, account, itemId, messages);

                return new GrantResult(true, relate);
            }

            if (tableService.GetTable<WeaponModData>().Any(m => m.Id == itemId))
            {
                // A weapon mod is not a stackable item: every one that drops is its own instance with its own affixes (A-74), announced the way the
                // client keeps them (SC_NewGunWeaponMod); the result screen then finds it by the id in UserDropCache.relate and shows its effects (A-148).
                List<uint> ids = [];
                for (int i = 0; i < count; i++)
                {
                    WeaponModEntity mod = weaponModService.Create(accountUid, itemId);
                    ids.Add(mod.Id);
                    messages.Add(new SC_NewGunWeaponMod { Mod = Extensions.ProtoExtensions.ToProtoWeaponMod(mod, null, ModPower.EffectIdOf(tableService, mod)) });
                }

                return new GrantResult(true, ids[^1], ModIds: ids);
            }

            if (itemId == StaminaItemId)
            {
                // Stamina (item 101) is not a resource_ex entry: it lives in its own balance with a recovery clock. Purchases (store goods
                // 3006/3009 give 101:60), stamina items and rewards all arrive here. ASSUMPTION A-53.
                StaminaBalance stamina = staminaService.AddKeepingClock(accountUid, count);
                messages.Add(new SC_StaminaTypeResourceUpdate
                {
                    ItemId = StaminaItemId,
                    Resource = new StaminaTypeResource { Value = stamina.Value, RefreshTime = stamina.RefreshTime },
                });
                return new GrantResult(true);
            }

            ItemData? data = tableService.GetTable<ItemData>().FirstOrDefault(i => i.Id == itemId);
            if (data is null)
            {
                Log.Warning("Reward item {ItemId} is not in ItemData; not granted.", itemId);
                return new GrantResult(false);
            }

            if (data.Type == ResourceService.ResourceItemType)
                messages.Add(resourceService.Add(accountUid, new Dictionary<uint, int> { [itemId] = count }));
            else
            {
                itemService.Grant(accountUid, itemId, data.Type, count);
                ItemEntity? item = account.Items.FirstOrDefault(i => i.ItemId == itemId);
                if (item is not null)
                    messages.Add(new SC_Items { Items = { [itemId] = item.Count } });
            }

            return new GrantResult(true);
        }

        public IReadOnlyList<(uint ItemId, int Count)> ParseRewards(string? text)
        {
            List<(uint, int)> rewards = [];
            foreach (string part in (text ?? string.Empty).Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string[] pair = part.Split(':', StringSplitOptions.TrimEntries);
                if (pair.Length == 2 && uint.TryParse(pair[0], out uint id) && int.TryParse(pair[1], out int count))
                    rewards.Add((id, count));
            }

            return rewards;
        }

        private GrantResult GrantGun(uint uid, AccountEntity account, uint gunId, List<IMessage> messages)
        {
            if (account.Guns.Any(g => g.GunId == gunId))
            {
                // Duplicate character: the official compensation is not in the client tables, so nothing is granted.
                Log.Information("Reward: character {GunId} already owned by account {Uid}; duplicate without compensation.", gunId, uid);
                return new GrantResult(true, DuplicateCharacter: true);
            }

            GunData data = tableService.GetTable<GunData>().First(g => g.Id == gunId);
            inventoryService.Add<GunEntity>(uid, GunService.CreateDefault(data, level: 1));
            GunEntity gun = account.Guns.First(g => g.GunId == gunId);
            WeaponEntity? weapon = account.Weapons.FirstOrDefault(w => w.Id == gun.WeaponId);

            if (weapon is not null)
            {
                messages.Add(new SC_NewGunWeapon { Weapon = weapon.ToProtoWeapon() });
                messages.Add(new SC_GunWeaponBelong { Id = weapon.Id, GunId = gunId });
                AddWeaponIndices(uid, account, weapon.WeaponId, messages);
            }

            messages.Add(new SC_NewGun { Gun = gun.ToProtoGunCharacter() });
            messages.Add(new SC_AddIndex { Type = 10, Id = gunId, Ts = 0 });

            if (gun.CostumeId != 0)
            {
                if (!account.Costumes.Any(c => c.CostumeId == gun.CostumeId))
                    inventoryService.Add(uid, new CostumeEntity { CostumeId = gun.CostumeId });

                messages.Add(new SC_AddIndex { Type = 13, Id = gun.CostumeId, Ts = 0 });
            }

            return new GrantResult(true, NewCharacter: true);
        }

        private uint GrantWeapon(uint uid, AccountEntity account, uint weaponId, List<IMessage> messages)
        {
            WeaponEntity weapon = GunService.CreateDefaultWeapon(weaponId, gunId: 0);
            inventoryService.Add(uid, weapon);

            messages.Add(new SC_NewGunWeapon { Weapon = weapon.ToProtoWeapon() });
            AddWeaponIndices(uid, account, weaponId, messages);
            return weapon.Id;
        }

        /// <summary>Weapon collection index (20) and its default skin index (60), as in the official capture.</summary>
        private void AddWeaponIndices(uint uid, AccountEntity account, uint weaponId, List<IMessage> messages)
        {
            messages.Add(new SC_AddIndex { Type = 20, Id = weaponId, Ts = 0 });

            WeaponSkinData? skin = tableService.GetTable<WeaponSkinData>()
                .Where(s => s.WeaponId == weaponId)
                .OrderByDescending(s => s.OpenType == 1)
                .FirstOrDefault();
            if (skin is null)
                return;

            if (!account.WeaponSkins.Any(s => s.WeaponSkinId == skin.Id))
                inventoryService.Add(uid, new WeaponSkinEntity { WeaponSkinId = skin.Id });

            messages.Add(new SC_AddIndex { Type = 60, Id = skin.Id, Ts = 0 });
        }
    }
}
