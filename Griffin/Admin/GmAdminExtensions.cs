using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Griffin.Command;
using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Table;
using Griffin.Database.Core;
using Griffin.Database.Entities;
using Griffin.GameServer.Extensions;
using Griffin.GameServer.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Griffin.Admin;

public static class GmAdminExtensions
{
    private static readonly DateTime StartedAtUtc = DateTime.UtcNow;

    public static WebApplication MapGmAdmin(this WebApplication app)
    {
        var api = app.MapGroup("/api/gm");
        api.AddEndpointFilter(async (context, next) =>
        {
            HttpContext http = context.HttpContext;
            IPAddress? remote = http.Connection.RemoteIpAddress;
            string host = http.Request.Host.Host.Trim('[', ']');
            if (remote is null || !IPAddress.IsLoopback(remote)
                || !(host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                    || IPAddress.TryParse(host, out IPAddress? hostAddress) && IPAddress.IsLoopback(hostAddress)))
                return Results.NotFound();

            if (!http.Request.IsHttps)
                return Results.StatusCode(StatusCodes.Status426UpgradeRequired);

            string? expected = Environment.GetEnvironmentVariable("GRIFFIN_GM_TOKEN")
                ?? app.Configuration["GmAdmin:Token"];
            string supplied = http.Request.Headers["X-GM-Token"].ToString();
            if (string.IsNullOrWhiteSpace(expected) || expected.Length < 24
                || !FixedTimeEquals(expected, supplied))
                return Results.Unauthorized();

            if (http.Request.Headers.TryGetValue("Origin", out var origin)
                && (!Uri.TryCreate(origin.ToString(), UriKind.Absolute, out Uri? originUri)
                    || !originUri.IsLoopback || !originUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            http.Response.Headers["Cache-Control"] = "no-store";
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            http.Response.Headers["X-Frame-Options"] = "DENY";
            return await next(context);
        });

        api.MapGet("/status", async (GriffinDbContext db, ConnectionManager connections) =>
        {
            bool dbAvailable = await db.Database.CanConnectAsync();
            return Results.Ok(new
            {
                startedAtUtc = StartedAtUtc,
                uptimeSeconds = (long)(DateTime.UtcNow - StartedAtUtc).TotalSeconds,
                database = dbAvailable ? "connected" : "unavailable",
                accounts = dbAvailable ? await db.Accounts.CountAsync() : (int?)null,
                online = connections.Connections.Count(c => c.Account != null),
                gmApi = "enabled"
            });
        });

        api.MapGet("/catalog", (ITableService tables) => Results.Ok(new
        {
            characterIds = tables.GetTable<GunData>().Where(row => row.Id != 0).Select(row => row.Id).Distinct().Order().ToArray(),
            weaponIds = tables.GetTable<GunWeaponData>().Where(row => row.Id != 0).Select(row => row.Id).Distinct().Order().ToArray(),
            itemIds = tables.GetTable<ItemData>().Where(row => row.Id != 0).Select(row => row.Id).Distinct().Order().ToArray(),
        }));

        api.MapGet("/accounts", async (string? q, int? limit, GriffinDbContext db) =>
        {
            int take = Math.Clamp(limit ?? 30, 1, 100);
            IQueryable<AccountEntity> query = db.Accounts.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(q))
            {
                string term = q.Trim();
                if (uint.TryParse(term, NumberStyles.None, CultureInfo.InvariantCulture, out uint uid))
                    query = query.Where(a => a.Uid == uid || EF.Functions.ILike(a.Email, $"%{term}%"));
                else
                    query = query.Where(a => EF.Functions.ILike(a.Email, $"%{term}%"));
            }

            var rows = await query.OrderByDescending(a => a.TimeLastLogin).Take(take)
                .Select(a => new { a.Uid, a.Email, a.IsGuest, a.TimeCreated, a.TimeLastLogin, a.BackgroundId })
                .ToListAsync();
            return Results.Ok(rows);
        });

        api.MapGet("/accounts/{uid}", async (uint uid, GriffinDbContext db, ConnectionManager connections) =>
        {
            AccountEntity? account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Uid == uid);
            if (account is null) return Results.NotFound(new { error = "No existe esa cuenta." });

            var items = await db.Items.AsNoTracking().Where(i => i.Account.Uid == uid)
                .OrderBy(i => i.Type).ThenBy(i => i.ItemId)
                .Select(i => new { i.ItemId, i.Type, i.Count }).ToListAsync();
            var guns = await db.Guns.AsNoTracking().Where(g => g.Account.Uid == uid)
                .OrderBy(g => g.GunId)
                .Select(g => new { g.GunId, g.Level, g.GunClass, g.Energy, g.LoveLevel, g.LoveExp, g.WeaponId, g.CostumeId })
                .ToListAsync();
            var weapons = await db.Weapons.AsNoTracking().Where(w => w.Account.Uid == uid)
                .OrderBy(w => w.Id).Select(w => new { w.Id, w.WeaponId, w.Level, w.GunId, w.EquippedSkinId })
                .ToListAsync();
            var stateKeys = await db.AccountStates.AsNoTracking().Where(s => s.AccountUid == uid)
                .OrderBy(s => s.Key).Select(s => new { s.Key, s.UpdatedAt }).ToListAsync();
            bool online = connections.Connections.Any(c => c.Account?.Uid == uid);

            return Results.Ok(new
            {
                account = new { account.Uid, account.Email, account.IsGuest, account.TimeCreated, account.TimeLastLogin, account.BackgroundId },
                online,
                inventory = new { items, guns, weapons,
                    costumes = await db.Costumes.AsNoTracking().Where(x => x.Account.Uid == uid).Select(x => x.CostumeId).ToListAsync(),
                    costumeParts = await db.CostumeParts.AsNoTracking().Where(x => x.Account.Uid == uid).Select(x => x.CostumePartId).ToListAsync(),
                    weaponMods = await db.WeaponMods.AsNoTracking().Where(x => x.Account.Uid == uid).Select(x => x.WeaponModId).ToListAsync(),
                    weaponSkins = await db.WeaponSkins.AsNoTracking().Where(x => x.Account.Uid == uid).Select(x => x.WeaponSkinId).ToListAsync(),
                    weaponModSkins = await db.WeaponModSkins.AsNoTracking().Where(x => x.Account.Uid == uid).Select(x => x.WeaponModSkinId).ToListAsync(),
                    backgrounds = await db.Backgrounds.AsNoTracking().Where(x => x.Account.Uid == uid).Select(x => x.BackgroundId).ToListAsync() },
                accountStateKeys = stateKeys
            });
        });

        api.MapDelete("/accounts/{uid}", async (uint uid, GriffinDbContext db, ConnectionManager connections,
            ILoggerFactory loggerFactory) =>
        {
            if (uid == 0) return Results.BadRequest(new { error = "El UID debe ser positivo." });
            lock (AccountLock.For(uid))
            {
                if (connections.Connections.Any(c => c.Account?.Uid == uid))
                    return Results.Conflict(new { error = "Cierra sesión de esa cuenta en el juego antes de eliminarla." });

                AccountEntity? account = db.Accounts.SingleOrDefault(a => a.Uid == uid);
                if (account is null) return Results.NotFound(new { error = "No existe esa cuenta." });
                db.Accounts.Remove(account);
                db.SaveChanges();
            }

            try { await AuditAsync(app, uid, "account.delete", new { deleted = true }); }
            catch (Exception ex) { loggerFactory.CreateLogger("GmAdmin").LogError(ex, "Account {Uid} was deleted but its audit file could not be written.", uid); }
            return Results.Ok(new { uid, deleted = true });
        });

        api.MapPost("/accounts/{uid}/items", async (uint uid, GrantItemRequest request,
            GriffinDbContext db, ITableService tables, IItemService itemService,
            IResourceService resources, IRewardService rewards, ConnectionManager connections, ILoggerFactory loggerFactory) =>
        {
            if (request.ItemId == 0 || request.Count is < 1 or > 1_000_000)
                return Results.BadRequest(new { error = "itemId debe ser positivo y count debe estar entre 1 y 1.000.000." });
            AccountEntity? account = await db.Accounts.SingleOrDefaultAsync(a => a.Uid == uid);
            if (account is null) return Results.NotFound(new { error = "No existe esa cuenta." });

            ItemData? definition = tables.GetTable<ItemData>().FirstOrDefault(i => i.Id == request.ItemId);
            if (definition is null || definition.Id == 0)
                return Results.BadRequest(new { error = "El ID no existe en ItemData de las tablas cargadas." });

            // Same path as every other reward (shop, mail, stages): resources, stamina (item 101, its own balance), plain items, weapon mods as
            // their own instances... and the client is told with the messages that path builds (A-150).
            // every weapon mod is its own row with its own affixes: do not create thousands of them at once
            if (request.Count > 100 && tables.GetTable<WeaponModData>().Any(m => m.Id == request.ItemId))
                return Results.BadRequest(new { error = "Los accesorios se entregan como unidades independientes: maximo 100 por operacion." });
            List<Google.Protobuf.IMessage> grantMessages = [];
            GrantResult granted = rewards.Grant(uid, request.ItemId, request.Count, grantMessages);
            if (!granted.Granted)
                return Results.BadRequest(new { error = "El servidor no pudo entregar ese ID (no es un objeto entregable)." });
            Connection? liveConnection = connections.Connections.FirstOrDefault(c => c.Account?.Uid == uid);
            if (liveConnection is not null && grantMessages.Count > 0)
            {
                try { liveConnection.SendAutoEncrypted(grantMessages.ToArray()); }
                catch (Exception ex) { loggerFactory.CreateLogger("GmAdmin").LogWarning(ex, "Could not push the item grant to account {Uid}.", uid); }
            }

            await AuditAsync(app, uid, "item.grant", new { request.ItemId, request.Count, itemType = definition.Type });
            return Results.Ok(new { uid, itemId = request.ItemId, countAdded = request.Count, itemType = definition.Type, online = connections.Connections.Any(c => c.Account?.Uid == uid) });
        });

        api.MapPost("/accounts/{uid}/characters", async (uint uid, CharacterRequest request,
            GriffinDbContext db, ITableService tables, IGunService guns, IInventoryService inventory,
            ConnectionManager connections, ILoggerFactory loggerFactory) =>
        {
            if (request.GunId == 0) return Results.BadRequest(new { error = "gunId debe ser positivo." });
            if (request.Level is < 1 or > 60) return Results.BadRequest(new { error = "El nivel de personaje debe estar entre 1 y 60." });
            GunData? definition = tables.GetTable<GunData>().FirstOrDefault(g => g.Id == request.GunId);
            if (definition is null || definition.Id == 0)
                return Results.BadRequest(new { error = "El personaje no existe en GunData de las tablas cargadas." });
            if (!await db.Accounts.AnyAsync(a => a.Uid == uid)) return Results.NotFound(new { error = "No existe esa cuenta." });

            GunEntity? existing = await db.Guns.SingleOrDefaultAsync(g => g.Account.Uid == uid && g.GunId == request.GunId);
            List<Google.Protobuf.IMessage> live = [];
            if (existing is null)
            {
                GunEntity gun = GunService.CreateDefault(definition, request.Level);
                guns.AddGun(uid, gun);
                uint defaultWeaponId = GunService.GetDefaultWeaponId(definition);
                WeaponEntity? weapon = null;
                if (defaultWeaponId != 0)
                {
                    weapon = GunService.CreateDefaultWeapon(defaultWeaponId, request.GunId);
                    inventory.Add(uid, weapon);
                    gun.WeaponId = weapon.Id;
                    guns.Save();
                }

                // Same announcements as RewardService.GrantGun: the client must learn the character's own weapon (and its collection
                // indices) before the character, otherwise the barracks screen looks up a weapon it never received and freezes (A-147).
                if (weapon is not null)
                {
                    live.Add(new SC_NewGunWeapon { Weapon = weapon.ToProtoWeapon() });
                    live.Add(new SC_GunWeaponBelong { Id = weapon.Id, GunId = request.GunId });
                    live.Add(new SC_AddIndex { Type = 20, Id = weapon.WeaponId, Ts = 0 });
                    WeaponSkinData? weaponSkin = tables.GetTable<WeaponSkinData>().Where(k => k.WeaponId == weapon.WeaponId).OrderByDescending(k => k.OpenType == 1).FirstOrDefault();
                    if (weaponSkin is not null)
                    {
                        lock (AccountLock.For(uid))
                            if (!inventory.GetPlayerInventory<WeaponSkinEntity>(uid).Any(k => k.WeaponSkinId == weaponSkin.Id))
                                inventory.Add(uid, new WeaponSkinEntity { WeaponSkinId = weaponSkin.Id });
                        live.Add(new SC_AddIndex { Type = 60, Id = weaponSkin.Id, Ts = 0 });
                    }
                }

                live.Add(new SC_NewGun { Gun = gun.ToProtoGunCharacter() });
                live.Add(new SC_AddIndex { Type = 10, Id = request.GunId, Ts = 0 });
                if (gun.CostumeId != 0)
                {
                    lock (AccountLock.For(uid))
                        if (!inventory.GetPlayerInventory<CostumeEntity>(uid).Any(c => c.CostumeId == gun.CostumeId))
                            inventory.Add(uid, new CostumeEntity { CostumeId = gun.CostumeId });
                    live.Add(new SC_AddIndex { Type = 13, Id = gun.CostumeId, Ts = 0 });
                }

                existing = gun;
            }
            else
            {
                existing.Level = request.Level;
                guns.Save();
                var current = await db.Guns.AsNoTracking().Where(g => g.Account.Uid == uid).ToListAsync();
                SC_Guns update = new();
                foreach (GunEntity gun in current) update.Guns.Add(gun.ToProtoGunCharacter());
                live.Add(update);
            }

            Connection? liveConnection = connections.Connections.FirstOrDefault(c => c.Account?.Uid == uid);
            if (liveConnection is not null)
            {
                try { liveConnection.SendAutoEncrypted(live.ToArray()); }
                catch (Exception ex) { loggerFactory.CreateLogger("GmAdmin").LogWarning(ex, "Could not push the character grant to account {Uid}.", uid); }
            }

            await AuditAsync(app, uid, "character.grant_or_level", new { request.GunId, request.Level });
            return Results.Ok(new { uid, gunId = request.GunId, level = existing.Level, online = connections.Connections.Any(c => c.Account?.Uid == uid) });
        });

        api.MapPost("/accounts/{uid}/weapons", async (uint uid, GrantWeaponRequest request,
            GriffinDbContext db, ITableService tables, IInventoryService inventory,
            IAccountStateService accountState,
            ConnectionManager connections, ILoggerFactory loggerFactory) =>
        {
            if (request.WeaponId == 0 || request.Count is < 1 or > 50)
                return Results.BadRequest(new { error = "weaponId debe ser positivo y count debe estar entre 1 y 50." });
            if (!tables.GetTable<GunWeaponData>().Any(row => row.Id == request.WeaponId))
                return Results.BadRequest(new { error = "El arma no existe en GunWeaponData de las tablas cargadas." });
            if (!await db.Accounts.AnyAsync(account => account.Uid == uid))
                return Results.NotFound(new { error = "No existe esa cuenta." });

            List<WeaponEntity> added = [];
            lock (AccountLock.For(uid))
            {
                for (int index = 0; index < request.Count; index++)
                {
                    WeaponEntity weapon = GunService.CreateDefaultWeapon(request.WeaponId, gunId: 0);
                    inventory.Add(uid, weapon);
                    added.Add(weapon);
                }
            }

            // Same bookkeeping as RewardService.GrantWeapon: the equip screen of the client looks up the collection index (20) and the default
            // skin of the weapon (index 60); without them it hits a null and the UI freezes (A-147).
            WeaponSkinData? defaultSkin = tables.GetTable<WeaponSkinData>()
                .Where(s => s.WeaponId == request.WeaponId)
                .OrderByDescending(s => s.OpenType == 1)
                .FirstOrDefault();
            if (defaultSkin is not null)
            {
                lock (AccountLock.For(uid))
                {
                    var owned = inventory.GetPlayerInventory<WeaponSkinEntity>(uid);
                    if (!owned.Any(s => s.WeaponSkinId == defaultSkin.Id))
                        inventory.Add(uid, new WeaponSkinEntity { WeaponSkinId = defaultSkin.Id });
                }
            }

            Connection? live = connections.Connections.FirstOrDefault(c => c.Account?.Uid == uid);
            bool refreshedLive = false;
            if (live is not null)
            {
                try
                {
                    // SC_GunWeapons is a complete collection snapshot. The live client merges those
                    // entries into its weapon map with Dictionary.Add instead of replacing the map,
                    // so resending existing IDs throws (observed: duplicate key 868) and freezes UI.
                    // The game uses SC_NewGunWeapon for a newly granted weapon; send only these
                    // incremental additions and keep SC_GunWeapons for the normal inventory query.
                    Google.Protobuf.IMessage[] updates = added
                        .Select(weapon => (Google.Protobuf.IMessage)new SC_NewGunWeapon
                        {
                            Weapon = weapon.ToProtoWeapon(WeaponModSkins.Of(accountState, uid, weapon.Id)),
                        })
                        .ToArray();
                    List<Google.Protobuf.IMessage> indices = [new SC_AddIndex { Type = 20, Id = request.WeaponId, Ts = 0 }];
                    if (defaultSkin is not null)
                        indices.Add(new SC_AddIndex { Type = 60, Id = defaultSkin.Id, Ts = 0 });
                    live.SendAutoEncrypted(updates.Concat(indices).ToArray());
                    refreshedLive = true;
                }
                catch (Exception ex)
                {
                    loggerFactory.CreateLogger("GmAdmin").LogWarning(ex, "Could not push the complete weapon inventory to account {Uid}; persisted grant remains available after relogin.", uid);
                }
            }
            await AuditAsync(app, uid, "weapon.grant", new { request.WeaponId, request.Count });
            return Results.Ok(new { uid, weaponId = request.WeaponId, countAdded = added.Count, online = live is not null, refreshedLiveClient = refreshedLive });
        });

        api.MapPost("/accounts/{uid}/costumes", async (uint uid, GrantCostumeRequest request,
            GriffinDbContext db, ITableService tables, IInventoryService inventory,
            ConnectionManager connections, ILoggerFactory loggerFactory) =>
        {
            if (!tables.GetTable<ClothesData>().Any(row => row.Id == request.CostumeId && request.CostumeId != 0))
                return Results.BadRequest(new { error = "La skin no existe en ClothesData de las tablas cargadas." });
            if (!await db.Accounts.AnyAsync(account => account.Uid == uid))
                return Results.NotFound(new { error = "No existe esa cuenta." });

            bool added;
            lock (AccountLock.For(uid))
            {
                added = !inventory.GetPlayerInventory<CostumeEntity>(uid).Any(c => c.CostumeId == request.CostumeId);
                if (added) inventory.Add(uid, new CostumeEntity { CostumeId = request.CostumeId });
            }

            // The client keeps owned outfits in its collection index (13), as RewardService.GrantGun announces them.
            if (added) SendLive(connections, uid, new SC_AddIndex { Type = 13, Id = request.CostumeId, Ts = 0 }, loggerFactory);
            await AuditAsync(app, uid, "costume.grant", new { request.CostumeId, added });
            return Results.Ok(new { uid, costumeId = request.CostumeId, added, online = connections.Connections.Any(c => c.Account?.Uid == uid) });
        });

        api.MapPost("/accounts/{uid}/weapon-skins", async (uint uid, GrantWeaponSkinRequest request,
            GriffinDbContext db, ITableService tables, IInventoryService inventory,
            ConnectionManager connections, ILoggerFactory loggerFactory) =>
        {
            if (!tables.GetTable<WeaponSkinData>().Any(row => row.Id == request.SkinId && request.SkinId != 0))
                return Results.BadRequest(new { error = "La skin de arma no existe en WeaponSkinData de las tablas cargadas." });
            if (!await db.Accounts.AnyAsync(account => account.Uid == uid))
                return Results.NotFound(new { error = "No existe esa cuenta." });

            bool added;
            lock (AccountLock.For(uid))
            {
                added = !inventory.GetPlayerInventory<WeaponSkinEntity>(uid).Any(s => s.WeaponSkinId == request.SkinId);
                if (added) inventory.Add(uid, new WeaponSkinEntity { WeaponSkinId = request.SkinId });
            }

            if (added) SendLive(connections, uid, new SC_AddIndex { Type = 60, Id = request.SkinId, Ts = 0 }, loggerFactory);
            await AuditAsync(app, uid, "weapon_skin.grant", new { request.SkinId, added });
            return Results.Ok(new { uid, skinId = request.SkinId, added, online = connections.Connections.Any(c => c.Account?.Uid == uid) });
        });

        api.MapPost("/accounts/{uid}/inventory/add-all", async (uint uid, AddAllRequest request,
            GriffinDbContext db, ConnectionManager connections, CommandDispatcher dispatcher,
            IInventoryService inventory, IProvisioningService provisioning) =>
        {
            if (!await db.Accounts.AnyAsync(a => a.Uid == uid)) return Results.NotFound(new { error = "No existe esa cuenta." });
            string category = (request.Category ?? "all").Trim().ToLowerInvariant();
            string[] allowed = ["all", "gun", "weapon", "weaponmod", "weaponskin", "weaponmodskin", "item", "costume", "costumepart", "background", "avgduo"];
            if (!allowed.Contains(category, StringComparer.Ordinal))
                return Results.BadRequest(new { error = "Categoría no válida.", allowed });

            Connection? live = connections.Connections.FirstOrDefault(c => c.Account?.Uid == uid);
            if (live is not null)
            {
                dispatcher.Dispatch(category == "all" ? "inventory addall" : $"inventory addall {category}", CommandSource.Client, live);
            }
            else if (category == "all")
            {
                provisioning.Provision(uid, refill: true);
                AddAllExceptItems(inventory, uid);
            }
            else if (category == "item")
            {
                provisioning.Provision(uid, refill: true);
            }
            else
            {
                AddInventoryCategory(inventory, uid, category);
            }

            await AuditAsync(app, uid, "inventory.add_all", new { category, online = live is not null });
            return Results.Ok(new { uid, category, online = live is not null, refreshedLiveClient = live is not null });
        });

        api.MapPost("/accounts/{uid}/clean", async (uint uid, CleanRequest request,
            GriffinDbContext db, ConnectionManager connections, ILoggerFactory loggerFactory) =>
        {
            if (request.ConfirmUid != uid)
                return Results.BadRequest(new { error = "Para confirmar, confirmUid debe coincidir exactamente con el UID objetivo." });
            string type = (request.Type ?? "").Trim().ToLowerInvariant();
            if (type is not ("items" or "resources"))
                return Results.BadRequest(new { error = "type debe ser 'items' o 'resources'." });

            AccountEntity? account = await db.Accounts.SingleOrDefaultAsync(a => a.Uid == uid);
            if (account is null) return Results.NotFound(new { error = "No existe esa cuenta." });
            int targetType = type == "resources" ? ResourceService.ResourceItemType : -1;
            List<ItemEntity> rows = await db.Items.Where(i => i.Account.Uid == uid
                && (targetType < 0 ? i.Type != ResourceService.ResourceItemType : i.Type == targetType)).ToListAsync();
            foreach (ItemEntity row in rows) row.Count = 0;
            await db.SaveChangesAsync();

            if (type == "resources")
            {
                SC_ResourceUpdate update = new();
                foreach (ItemEntity row in rows) update.ResourceEx[row.ItemId] = 0;
                SendLive(connections, uid, update, loggerFactory);
            }
            else
            {
                SC_Items update = new();
                foreach (ItemEntity row in rows) update.Items[row.ItemId] = 0;
                SendLive(connections, uid, update, loggerFactory);
            }

            await AuditAsync(app, uid, "inventory.clean", new { type, zeroedItems = rows.Count });
            return Results.Ok(new { uid, type, zeroedItems = rows.Count, online = connections.Connections.Any(c => c.Account?.Uid == uid) });
        });

        api.MapGet("/commands", (CommandRegistry registry) => Results.Ok(registry.GetAll()
            .Where(c => c.AllowedSources.HasFlag(CommandSource.Console))
            .OrderBy(c => c.Name)
            .Select(c => new { c.Name, c.Description, c.Usage })));

        api.MapPost("/commands", async (CommandRequest request, CommandRegistry registry,
            CommandDispatcher dispatcher) =>
        {
            if (string.IsNullOrWhiteSpace(request.Command) || request.Command.Length > 500)
                return Results.BadRequest(new { error = "Escribe un comando de hasta 500 caracteres." });
            (string? name, _) = CommandParser.Parse(request.Command);
            if (name is null || !registry.TryGet(name, out CommandRegistration? registration)
                || !registration.AllowedSources.HasFlag(CommandSource.Console))
                return Results.BadRequest(new { error = "Ese comando no está habilitado para administración por consola." });
            List<string> output = [];
            dispatcher.Dispatch(request.Command, CommandSource.Console, clientReplyCallback: output.Add);
            await AuditAsync(app, null, "command.execute", new { command = name });
            return Results.Ok(new { command = name, output });
        });

        return app;
    }

    private static bool FixedTimeEquals(string expected, string supplied)
    {
        byte[] left = Encoding.UTF8.GetBytes(expected);
        byte[] right = Encoding.UTF8.GetBytes(supplied);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }

    private static void SendLive<T>(ConnectionManager connections, uint uid, T message, ILoggerFactory loggerFactory)
        where T : Google.Protobuf.IMessage
    {
        Connection? connection = connections.Connections.FirstOrDefault(c => c.Account?.Uid == uid);
        if (connection is null) return;
        try { connection.SendAutoEncrypted(message!); }
        catch (Exception ex) { loggerFactory.CreateLogger("GmAdmin").LogWarning(ex, "Could not push GM update to connected account {Uid}; persisted change remains available on next refresh.", uid); }
    }

    private static async Task AuditAsync(WebApplication app, uint? uid, string operation, object details)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"gm-audit-{DateTime.UtcNow:yyyy-MM-dd}.jsonl");
        string json = JsonSerializer.Serialize(new { atUtc = DateTime.UtcNow, actor = "loopback-gm", uid, operation, details });
        await File.AppendAllTextAsync(path, json + Environment.NewLine, Encoding.UTF8);
        app.Logger.LogInformation("GM operation {Operation} on account {Uid}", operation, uid);
    }

    private static void AddInventoryCategory(IInventoryService inventory, uint uid, string category)
    {
        switch (category)
        {
            case "gun": inventory.AddAll<GunEntity>(uid); break;
            case "weapon": inventory.AddAll<WeaponEntity>(uid); break;
            case "weaponmod": inventory.AddAll<WeaponModEntity>(uid); break;
            case "weaponskin": inventory.AddAll<WeaponSkinEntity>(uid); break;
            case "weaponmodskin": inventory.AddAll<WeaponModSkinEntity>(uid); break;
            case "costume": inventory.AddAll<CostumeEntity>(uid); break;
            case "costumepart": inventory.AddAll<CostumePartEntity>(uid); break;
            case "background": inventory.AddAll<BackgroundEntity>(uid); break;
            case "avgduo": inventory.AddAll<AvgDuoEntity>(uid); break;
            default: throw new ArgumentOutOfRangeException(nameof(category), category, "Unsupported inventory category.");
        }
    }

    private static void AddAllExceptItems(IInventoryService inventory, uint uid)
    {
        foreach (string category in new[] { "gun", "weapon", "weaponmod", "weaponskin", "weaponmodskin", "costume", "costumepart", "background", "avgduo" })
            AddInventoryCategory(inventory, uid, category);
    }

    public sealed record GrantItemRequest(uint ItemId, int Count);
    public sealed record CharacterRequest(uint GunId, int Level = 1);
    public sealed record GrantWeaponRequest(uint WeaponId, int Count = 1);
    public sealed record GrantCostumeRequest(uint CostumeId);
    public sealed record GrantWeaponSkinRequest(uint SkinId);
    public sealed record AddAllRequest(string? Category = "all");
    public sealed record CleanRequest(uint ConfirmUid, string? Type);
    public sealed record CommandRequest(string Command);
}
