using Griffin.Command;
using Griffin.Common.Proto;
using Griffin.Common.Table;
using Griffin.Database.Entities;
using Griffin.GameServer.Extensions;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Commands;

[Command("inventory", "Gestiona el inventario del jugador", "inventory addall [type] | inventory provision", CommandSource.Client)]
public sealed class InventoryCommand(IInventoryService inventoryService, ITableService tableService, IProvisioningService provisioning, IAccountStateService accountState) : ICommand
{
    private const int ItemsPerResponse = 7000;

    private enum InventoryType
    {
        Gun,
        Weapon,
        WeaponMod,
        WeaponSkin,
        WeaponModSkin,
        Item,
        Costume,
        CostumePart,
        Background,
        AvgDuo,
    }

    private static readonly Dictionary<string, InventoryType> TypeAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gun"] = InventoryType.Gun,
        ["char"] = InventoryType.Gun,
        ["character"] = InventoryType.Gun,
        ["weapon"] = InventoryType.Weapon,
        ["weaponmod"] = InventoryType.WeaponMod,
        ["weaponskin"] = InventoryType.WeaponSkin,
        ["weaponmodskin"] = InventoryType.WeaponModSkin,
        ["item"] = InventoryType.Item,
        ["characterskin"] = InventoryType.Costume,
        ["costume"] = InventoryType.Costume,
        ["costumepart"] = InventoryType.CostumePart,
        ["background"] = InventoryType.Background,
        ["avgduo"] = InventoryType.AvgDuo,
    };

    [Argument("provision", "Crea o sube desde 0 los objetos que listan las tablas del cliente (monedas, regalos, ingredientes, llaves, muebles); los saldos positivos se conservan", pattern: "true", flags: ArgumentFlags.Optional | ArgumentFlags.IgnoreCase)]
    public bool Provision { get; set; }

    [Argument("addall", "Añade todas las entradas de un tipo de inventario o de todos", pattern: "true", flags: ArgumentFlags.Optional | ArgumentFlags.IgnoreCase)]
    public bool AddAll { get; set; }

    [Argument("type", "Tipo de inventario (opcional)", pattern: "gun|char|character|weapon|weaponmod|weaponskin|weaponmodskin|item|characterskin|costume|costumepart|background|avgduo", flags: ArgumentFlags.Optional | ArgumentFlags.IgnoreCase)]
    public string? Type { get; set; }

    public void Execute(CommandContext ctx)
    {
        if (ctx.Connection?.Account is null)
        {
            ctx.Reply("Debes haber iniciado sesión para usar este comando.");
            return;
        }

        uint accountUid = ctx.Connection.Account.Uid;

        if (Provision || ctx.RawArgs.ContainsKey("provision"))
        {
            ProvisionReport report = provisioning.Provision(accountUid);
            if (report.ResourceUpdate is not null)
                ctx.Connection.SendAutoEncrypted(report.ResourceUpdate);
            SendInventoryResponses(ctx, [InventoryType.Item]);
            ctx.Reply($"Aprovisionado: {report.ItemsAdded} objeto(s) añadidos, {report.ItemsRepaired} subidos desde 0, {report.ResourcesRepaired} recurso(s) subidos desde 0.");
            return;
        }

        if (!AddAll && !ctx.RawArgs.ContainsKey("addall"))
        {
            ctx.Reply("Uso: /inventory addall [tipo]");
            return;
        }

        var requestedType = ResolveRequestedType(ctx.RawArgs, Type);
        InventoryType? inventoryType = null;
        if (!string.IsNullOrEmpty(requestedType))
        {
            if (!TypeAliases.TryGetValue(requestedType, out var resolvedType))
            {
                ctx.Reply($"Tipo desconocido '{requestedType}'. Tipos válidos: gun|char|character|weapon|weaponmod|weaponskin|weaponmodskin|item|characterskin|costume|costumepart|background|avgduo");
                return;
            }

            inventoryType = resolvedType;
        }

        if (requestedType is null)
        {
            AddAllTypes(accountUid, ctx);
            ctx.Reply("Se añadieron todos los tipos de inventario (sin los objetos con errores).");
        }
        else
        {
            AddSingleType(accountUid, inventoryType!.Value, ctx);
        }
    }

    private static string? ResolveRequestedType(IReadOnlyDictionary<string, string> rawArgs, string? typedTypeArg)
    {
        if (!string.IsNullOrWhiteSpace(typedTypeArg))
            return typedTypeArg;

        foreach (var (key, value) in rawArgs)
        {
            if (key.Equals("addall", StringComparison.OrdinalIgnoreCase))
                continue;
            if (key.Equals("type", StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
                return key;

            return value;
        }

        return null;
    }

    private void AddAllTypes(uint accountUid, CommandContext ctx)
    {
        InventoryType[] types = Enum.GetValues<InventoryType>()
            .Where(type => type != InventoryType.Item)
            .ToArray();

        foreach (InventoryType type in types)
        {
            AddInventoryType(accountUid, type);
            ctx.Reply($"Se añadieron todas las entradas de tipo {type.ToString().ToLowerInvariant()}.");
        }

        SendInventoryResponses(ctx, types);
    }

    private void AddSingleType(uint accountUid, InventoryType type, CommandContext ctx)
    {
        AddInventoryType(accountUid, type);
        SendInventoryResponses(ctx, [type]);

        ctx.Reply($"Se añadieron todas las entradas de tipo {type.ToString().ToLowerInvariant()}.");
    }

    private void AddInventoryType(uint accountUid, InventoryType type)
    {
        switch (type)
        {
            case InventoryType.Gun:
                inventoryService.AddAll<GunEntity>(accountUid);
                break;
            case InventoryType.Weapon:
                inventoryService.AddAll<WeaponEntity>(accountUid);
                break;
            case InventoryType.WeaponMod:
                inventoryService.AddAll<WeaponModEntity>(accountUid);
                break;
            case InventoryType.WeaponSkin:
                inventoryService.AddAll<WeaponSkinEntity>(accountUid);
                break;
            case InventoryType.WeaponModSkin:
                inventoryService.AddAll<WeaponModSkinEntity>(accountUid);
                break;
            case InventoryType.Item:
                inventoryService.AddAll<ItemEntity>(accountUid);
                break;
            case InventoryType.Costume:
                inventoryService.AddAll<CostumeEntity>(accountUid);
                break;
            case InventoryType.CostumePart:
                inventoryService.AddAll<CostumePartEntity>(accountUid);
                break;
            case InventoryType.Background:
                inventoryService.AddAll<BackgroundEntity>(accountUid);
                break;
            case InventoryType.AvgDuo:
                inventoryService.AddAll<AvgDuoEntity>(accountUid);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, "Unsupported inventory type.");
        }
    }

    private void SendInventoryResponses(
        CommandContext ctx,
        IEnumerable<InventoryType> types)
    {
        if (ctx.Connection?.Account is null)
            return;

        uint accountUid = ctx.Connection.Account.Uid;

        bool sendIndex = false;

        foreach (InventoryType type in types.Distinct())
        {
            switch (type)
            {
                case InventoryType.Gun:
                    ctx.Connection.SendAutoEncrypted(CreateGunResponse(accountUid));
                    ctx.Connection.SendAutoEncrypted(CreateGunAchievementCountSection(accountUid));
                    ctx.Reply("Personajes actualizados.");
                    break;
                case InventoryType.Weapon:
                    ctx.Connection.SendAutoEncrypted(CreateWeaponResponse(accountUid));
                    ctx.Reply("Armas actualizadas.");
                    break;
                case InventoryType.WeaponMod:
                    ctx.Connection.SendAutoEncrypted(CreateWeaponModResponse(accountUid));
                    ctx.Reply("Módulos de arma actualizados.");
                    sendIndex = true;
                    break;
                case InventoryType.WeaponSkin:
                    ctx.Connection.SendAutoEncrypted(CreateWeaponSkinResponse(accountUid));
                    ctx.Reply("Aspectos de arma actualizados.");
                    sendIndex = true;
                    break;
                case InventoryType.WeaponModSkin:
                case InventoryType.Costume:
                case InventoryType.CostumePart:
                case InventoryType.Background:
                    sendIndex = true;
                    break;
                case InventoryType.Item:
                    foreach (SC_Items response in CreateItemResponses(accountUid))
                        ctx.Connection.SendAutoEncrypted(response);
                    ctx.Reply("Objetos actualizados.");
                    sendIndex = true;
                    break;
                case InventoryType.AvgDuo:
                    ctx.Reply("Historias en pareja actualizadas.");
                    sendIndex = true;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(type), type, "Unsupported inventory type.");
            }
        }

        if (!sendIndex)
            return;

        InventoryType[] indexTypes =
        [
            InventoryType.Costume,
            InventoryType.CostumePart,
            InventoryType.Background,
            InventoryType.WeaponMod,
            InventoryType.WeaponSkin,
            InventoryType.WeaponModSkin,
            InventoryType.Item,
        ];
        ctx.Connection.SendAutoEncrypted(CreateIndexResponse(accountUid, indexTypes));
        ctx.Reply("Índice actualizado.");
    }

    private SC_CountSection CreateGunAchievementCountSection(uint accountUid)
    {
        SC_CountSection countSection = new SC_CountSection
        {
            Counters =
            {
                new CommonQuestCounters
                {
                    Type = CommonQuestCounters.Types.Type.Achievement,
                    Rewards = { },
                    PhaseRewards = { },
                    JGOJBNHMJJH = { },
                },
            },
        };

        List<GunData> gunData = tableService.GetTable<GunData>();
        foreach (GunEntity gun in inventoryService.GetPlayerInventory<GunEntity>(accountUid))
        {
            uint dormId = gunData.Where(g => g.Id == gun.GunId).FirstOrDefault().POBHEFFJGOP[0];
            countSection.Counters[0].Rewards.Add(dormId, false);
        }

        return countSection;
    }

    private SC_Guns CreateGunResponse(uint accountUid)
    {
        SC_Guns response = new();
        foreach (GunEntity gun in inventoryService.GetPlayerInventory<GunEntity>(accountUid))
            response.Guns.Add(gun.ToProtoGunCharacter());

        return response;
    }

    private SC_GunWeapons CreateWeaponResponse(uint accountUid)
    {
        SC_GunWeapons response = new();
        foreach (WeaponEntity weapon in inventoryService.GetPlayerInventory<WeaponEntity>(accountUid))
        {
            response.Weapons.Add(weapon.ToProtoWeapon());

            if (weapon.GunId != 0)
                response.FEDCFGGDNBN[weapon.Id] = weapon.GunId;
        }

        return response;
    }

    private SC_GunWeaponMods CreateWeaponModResponse(uint accountUid)
    {
        SC_GunWeaponMods response = new();
        foreach (WeaponModEntity mod in inventoryService.GetPlayerInventory<WeaponModEntity>(accountUid))
            response.Mods.Add(ModPolarity.ToProto(tableService, accountState, accountUid, mod));

        return response;
    }

    private SC_GunWeaponSkinItems CreateWeaponSkinResponse(uint accountUid)
    {
        SC_GunWeaponSkinItems response = new();
        foreach (WeaponSkinEntity weaponSkin in inventoryService.GetPlayerInventory<WeaponSkinEntity>(accountUid))
            response.PGMKLGCFAOF[weaponSkin.WeaponSkinId] = 1;

        return response;
    }

    private IEnumerable<SC_Items> CreateItemResponses(uint accountUid)
    {
        ItemEntity[] items = inventoryService.GetPlayerInventory<ItemEntity>(accountUid)
            .Where(item => item.Type != ResourceService.ResourceItemType)
            .ToArray();
        if (items.Length == 0)
        {
            yield return new SC_Items();
            yield break;
        }

        for (int offset = 0; offset < items.Length; offset += ItemsPerResponse)
        {
            SC_Items response = new();
            int end = Math.Min(offset + ItemsPerResponse, items.Length);

            for (int i = offset; i < end; i++)
                response.Items[items[i].ItemId] = items[i].Count;

            yield return response;
        }
    }

    private SC_Index CreateIndexResponse(uint accountUid, IEnumerable<InventoryType> types)
    {
        SC_Index response = new();

        foreach (InventoryType type in types.Distinct())
        {
            uint indexType = type switch
            {
                InventoryType.Costume => 13,
                InventoryType.CostumePart => 14,
                InventoryType.Background => 30,
                InventoryType.WeaponMod => 21,
                InventoryType.WeaponSkin => 60,
                InventoryType.WeaponModSkin => 61,
                InventoryType.Item => 162,
                _ => 0,
            };
            if (indexType == 0)
                continue;

            Griffin.Common.Proto.Index index = new Griffin.Common.Proto.Index { Type = indexType };
            FillIndexDetails(accountUid, type, index);
            response.Indices[indexType] = index;
        }

        return response;
    }

    private void FillIndexDetails(uint accountUid, InventoryType type, Griffin.Common.Proto.Index index)
    {
        switch (type)
        {
            case InventoryType.Costume:
                foreach (CostumeEntity costume in inventoryService.GetPlayerInventory<CostumeEntity>(accountUid))
                    index.Details[costume.CostumeId] = true;
                break;
            case InventoryType.CostumePart:
                foreach (CostumePartEntity costumePart in inventoryService.GetPlayerInventory<CostumePartEntity>(accountUid))
                    index.Details[costumePart.CostumePartId] = false;
                break;
            case InventoryType.Background:
                foreach (BackgroundEntity background in inventoryService.GetPlayerInventory<BackgroundEntity>(accountUid))
                    index.Details[background.BackgroundId] = true;
                break;
            case InventoryType.WeaponMod:
                foreach (WeaponModEntity weaponMod in inventoryService.GetPlayerInventory<WeaponModEntity>(accountUid))
                    index.Details[weaponMod.WeaponModId] = true;
                break;
            case InventoryType.WeaponSkin:
                foreach (WeaponSkinEntity weaponSkin in inventoryService.GetPlayerInventory<WeaponSkinEntity>(accountUid))
                    index.Details[weaponSkin.WeaponSkinId] = true;
                break;
            case InventoryType.WeaponModSkin:
                foreach (WeaponModSkinEntity weaponModSkin in inventoryService.GetPlayerInventory<WeaponModSkinEntity>(accountUid))
                    index.Details[weaponModSkin.WeaponModSkinId] = true;
                break;
            case InventoryType.Item:
                foreach (ItemEntity item in inventoryService.GetPlayerInventory<ItemEntity>(accountUid))
                {
                    if (item.Type != 162)
                        continue;

                    index.Details[item.ItemId] = false;
                }
                break;
        }
    }
}
