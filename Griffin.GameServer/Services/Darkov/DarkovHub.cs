namespace Griffin.GameServer.Services.Darkov
{
    /// <summary>
    /// The requests of the lobby side of the mode that change the account's items (A-145): moving items between the backpack, the storage and the equipment, selling, buying in the
    /// store, repairing. Each one is applied to the <see cref="DarkovProfile"/> and answered the way the official server did (an <c>SC_DarkovItemUpdate</c> push that mirrors the change,
    /// then the reply).
    /// </summary>
    public static class DarkovHub
    {
        public const ushort ItemExchange = 10373, ItemExchangeReply = 10374, SortBag = 10375, SortBagReply = 10376, ItemUpdate = 10377;
        public const ushort StoreInfo = 26016, StoreInfoReply = 26017, StoreSell = 26018, StoreSellReply = 26019, Repair = 26026, RepairReply = 26027;
        public const ushort AutoFill = 26046, AutoFillReply = 26047, StoreBuy = 26042, StoreBuyReply = 26043, ReportError = 19900;

        public const ushort Craft = 26022, CraftReply = 26023, CraftPick = 26068, CraftPickReply = 26069, CraftCancel = 26070, CraftCancelReply = 26071, Exploration = 26054, ExplorationReply = 26055;

        public static readonly ushort[] Handled = [ItemExchange, SortBag, StoreInfo, StoreSell, Repair, AutoFill, StoreBuy, Craft, CraftPick, CraftCancel, Exploration,
            DarkovQuests.Get, DarkovQuests.Receive, DarkovQuests.Update, DarkovQuests.Finish, 26031, 26033, 26035, 26037, 26107, 26028, 26086, 26066, 26048];

        private const int InvalidInput = -10008;

        /// <summary>The goods the official store offered (EsGoods ids), the ones on sale every day.</summary>
        private static readonly uint[] StoreGoods = [1001, 1002, 1003, 1004, 1005, 1006, 1007, 1008, 1009, 1010, 1011, 1088, 1090, 1091, 1100, 1101, 1106];

        private const uint Money = 3041001;

        /// <returns>the packets to send, and whether the profile changed.</returns>
        public static (List<(ushort, byte[])> Packets, bool Changed) Handle(ushort id, PbMsg request, DarkovProfile profile)
        {
            profile.Normalize();
            var (packets, changed) = Dispatch(id, request, profile);
            if (packets.Count == 1 && packets[0].Item1 == ReportError)
                packets[0] = (ReportError, new PbW().V(1, id).S(2, InvalidInput).ToArray());
            return (packets, changed);
        }

        private static (List<(ushort, byte[])> Packets, bool Changed) Dispatch(ushort id, PbMsg request, DarkovProfile profile)
        {
            byte[] payloadOf(PbMsg m) => m.Original;
            switch (id)
            {
                case ItemExchange:
                    return Exchange(request, profile);
                case SortBag:
                    return Sort(request, profile);
                case AutoFill:
                    return Fill(profile);
                case StoreInfo:
                    return ([(StoreInfoReply, StoreList(profile, (uint)request.U(1)).ToArray())], false);
                case StoreBuy:
                    return Buy(request, profile);
                case StoreSell:
                    return Sell(request, profile);
                case Repair:
                    return Mend(request, profile);
                case Craft:
                    return StartCraft(request, profile);
                case CraftPick:
                    return PickCraft(request, profile);
                case CraftCancel:
                    return CancelCraft(request, profile);
                case Exploration:
                    return ([(ExplorationReply, ExplorationList(profile))], false);
                case 26031 or 26033 or 26035 or 26037 or 26107 or 26028 or 26086:
                    // talents, avg reads, level reward and settings: the answer repeats the request (what the official server did)
                    return ([((ushort)(id + 1), payloadOf(request))], false);
                case 26066:
                    return ([(26067, new PbW().M(1, new PbW().V(1, request.U(1)).V(2, request.U(2))).ToArray())], false);
                case 26048:
                    return ([(26049, new PbW().M(1, new PbW().B(1, request.Raw(1) ?? []).V(2, 3230000)).ToArray())], false);
                case DarkovQuests.Get or DarkovQuests.Receive or DarkovQuests.Update or DarkovQuests.Finish:
                    return DarkovQuests.Handle(id, request, profile);
            }

            return ([], false);
        }

        // ---- containers ----------------------------------------------------------------------------------------------------------------------------

        private static List<DItem?>? Container(DarkovProfile profile, uint type) => type switch
        {
            0 => profile.Bag,
            3 => profile.Stash,
            5 => profile.Equip,
            _ => null,
        };

        private static PbW Slot(uint type, int slot, DItem? item)
        {
            var w = new PbW();
            if (type != 0)
                w.V(1, type);
            if (slot != 0)
                w.V(4, (ulong)slot);
            if (item is not null)
                w.M(6, item.Write());
            return w;
        }

        private static byte[] Update(IEnumerable<PbW> slots, DarkovProfile? currency = null, IEnumerable<uint>? changedCurrency = null)
        {
            var w = new PbW();
            foreach (PbW slot in slots)
                w.M(1, slot);
            if (currency is not null && changedCurrency is not null)
                foreach (uint cfg in changedCurrency)
                    w.M(2, new PbW().V(1, cfg).V(2, currency.Ammo.GetValueOrDefault(cfg)));
            return w.ToArray();
        }

        // ---- moving items ----------------------------------------------------------------------------------------------------------------------------

        private static (List<(ushort, byte[])>, bool) Exchange(PbMsg request, DarkovProfile profile)
        {
            PbMsg? from = request.Msg(1);
            PbMsg? to = request.Msg(2);
            if (from is null || to is null)
                return ([(ItemExchangeReply, [])], false);
            uint fromType = (uint)from.U(1), toType = (uint)to.U(1);
            int fromSlot = (int)from.U(4), toSlot = (int)to.U(4);
            DItem? stack = DItem.Read(to.Msg(6));
            var source = Container(profile, fromType);
            var destination = Container(profile, toType);
            if (source is not null && fromSlot < source.Count)
                source[fromSlot] = null;
            if (destination is not null && toSlot < destination.Count)
                destination[toSlot] = stack;

            var packets = new List<(ushort, byte[])>
            {
                (ItemUpdate, Update([Slot(fromType, fromSlot, null), Slot(toType, toSlot, stack)])),
                (ItemExchangeReply, []),
            };
            return (packets, true);
        }

        private static (List<(ushort, byte[])>, bool) Sort(PbMsg request, DarkovProfile profile)
        {
            uint type = (uint)request.U(1);
            var container = Container(profile, type) ?? profile.Bag;
            var sorted = container.Where(i => i is not null).OrderBy(i => i!.Cfg).ThenByDescending(i => i!.Count).ToList();
            for (int i = 0; i < container.Count; i++)
                container[i] = i < sorted.Count ? sorted[i] : null;
            var body = new PbW().V(1, (ulong)container.Count);
            foreach (DItem? item in container)
                body.M(2, item is null ? new PbW() : new PbW().M(1, item.Write()).V(2, 1));
            return ([(SortBagReply, new PbW().V(1, type).M(2, body).ToArray())], true);
        }

        /// <summary>Everything in the backpack goes to the storage, in the first free slots.</summary>
        private static (List<(ushort, byte[])>, bool) Fill(DarkovProfile profile)
        {
            for (int i = 0; i < profile.Bag.Count; i++)
            {
                DItem? item = profile.Bag[i];
                if (item is null)
                    continue;
                int free = profile.Stash.FindIndex(s => s is null);
                if (free < 0)
                    break;
                profile.Stash[free] = item;
                profile.Bag[i] = null;
            }

            var grid = new PbW().V(1, (ulong)profile.Stash.Count);
            foreach (DItem? item in profile.Stash)
                grid.M(2, item is null ? new PbW() : new PbW().M(1, item.Write()));
            return ([(AutoFillReply, new PbW().M(1, grid).ToArray())], true);
        }

        // ---- store -----------------------------------------------------------------------------------------------------------------------------------

        private static PbW StoreList(DarkovProfile profile, uint store)
        {
            var w = new PbW().V(1, store);
            long refresh = new DateTimeOffset(DateTimeOffset.UtcNow.Date.AddDays(1).AddHours(5), TimeSpan.Zero).ToUnixTimeSeconds();
            foreach (uint good in StoreGoods)
            {
                var state = new PbW();
                uint bought = profile.Bought.GetValueOrDefault(good);
                if (bought > 0)
                    state.V(1, bought);
                if (good != 1088)
                    state.V(3, (ulong)refresh);
                w.M(4, new PbW().V(1, good).M(2, state.V(4, 1)));
            }

            return w;
        }

        private static (List<(ushort, byte[])>, bool) Buy(PbMsg request, DarkovProfile profile)
        {
            PbMsg? order = request.Msg(3);
            uint goodId = order is null ? 0 : (uint)order.U(1);
            uint times = order is null ? 0 : (uint)Math.Max(1, order.U(2));
            if (!RawTables.ById("EsGoodsData").TryGetValue(goodId, out PbMsg? good) || !StoreGoods.Contains(goodId))
                return Refuse();
            PbMsg? item = good.Msg(2);
            PbMsg? price = good.Msg(5);
            if (item is null)
                return Refuse();
            uint cfg = (uint)item.U(1), amount = (uint)item.U(2) * times;
            uint currency = price is null ? Money : (uint)price.U(1);
            long cost = price is null ? 0 : (long)price.U(2) * times;
            uint limit = (uint)good.U(8);
            uint bought = profile.Bought.GetValueOrDefault(goodId);
            if (cost > profile.Ammo.GetValueOrDefault(currency) || (cost == 0 && limit > 0 && bought + times > limit && limit < 10))
                return Refuse();

            var changedSlots = new List<PbW>();
            uint stackLimit = (uint)Math.Max(1, RawTables.ById("EsItemData").GetValueOrDefault(cfg)?.U(5) ?? 1);
            uint left = amount;
            for (int i = 0; i < profile.Bag.Count && left > 0; i++)
            {
                DItem? slot = profile.Bag[i];
                if (slot is null || slot.Cfg != cfg || slot.HasDur || slot.Count >= stackLimit)
                    continue;
                uint add = Math.Min(left, stackLimit - slot.Count);
                slot.Count += add;
                left -= add;
                changedSlots.Add(Slot(0, i, slot));
            }

            for (int i = 0; i < profile.Bag.Count && left > 0; i++)
            {
                if (profile.Bag[i] is not null)
                    continue;
                uint add = Math.Min(left, stackLimit);
                var fresh = new DItem { Uid = profile.NewUid(), Cfg = cfg, Count = add };
                if (RawTables.ById("EsItemData").GetValueOrDefault(cfg) is { } info && info.U(13) > 0)
                    fresh.DurCur = fresh.DurMax = (uint)info.U(13);
                profile.Bag[i] = fresh;
                left -= add;
                changedSlots.Add(Slot(0, i, fresh));
            }

            if (left > 0)
                return Refuse();
            profile.Ammo[currency] = (uint)(profile.Ammo.GetValueOrDefault(currency) - cost);
            profile.Bought[goodId] = bought + times;
            var packets = new List<(ushort, byte[])>
            {
                (ItemUpdate, Update(changedSlots, profile, cost > 0 ? [currency] : [])),
                (StoreBuyReply, StoreList(profile, (uint)request.U(1)).ToArray()),
            };
            return (packets, true);
        }

        private static (List<(ushort, byte[])>, bool) Sell(PbMsg request, DarkovProfile profile)
        {
            PbMsg? sale = request.Msg(2);
            if (sale is null)
                return Refuse();
            uint type = (uint)sale.U(1);
            ulong uid = sale.U(2);
            uint count = (uint)Math.Max(1, sale.U(3));
            int slot = (int)sale.U(4);
            var container = Container(profile, type);
            if (container is null || slot >= container.Count || container[slot] is not { } item || item.Uid != uid)
                return Refuse();
            count = Math.Min(count, item.Count);
            uint unit = (uint)(RawTables.ById("EsItemData").GetValueOrDefault(item.Cfg)?.U(9) ?? 1);
            item.Count -= count;
            DItem? remaining = item.Count == 0 ? null : item;
            container[slot] = remaining;
            profile.Ammo[Money] = profile.Ammo.GetValueOrDefault(Money) + unit * count;

            var money = new PbW();
            foreach ((uint cfg, uint amount) in profile.Ammo.OrderBy(p => p.Key))
                money.M(3, new PbW().V(1, cfg).V(2, amount));
            var reply = new PbW().V(1, request.U(1)).M(2, new PbW().V(1, type).V(2, uid).V(3, count).V(4, (ulong)slot));
            var packets = new List<(ushort, byte[])>
            {
                (ItemUpdate, Update([Slot(type, slot, remaining)], profile, [Money])),
                (StoreSellReply, reply.ToArray().Concat(money.ToArray()).ToArray()),
            };
            return (packets, true);
        }

        // ---- repair ----------------------------------------------------------------------------------------------------------------------------------

        private static (List<(ushort, byte[])>, bool) Mend(PbMsg request, DarkovProfile profile)
        {
            PbMsg? target = request.Msg(1);
            if (target is null)
                return Refuse();
            ulong uid = target.U(1);
            DItem? found = null;
            uint type = 0;
            int slot = -1;
            foreach ((uint t, List<DItem?> list) in new (uint, List<DItem?>)[] { (0, profile.Bag), (3, profile.Stash), (5, profile.Equip) })
            {
                int index = list.FindIndex(i => i is not null && i.Uid == uid);
                if (index >= 0)
                {
                    found = list[index];
                    type = t;
                    slot = index;
                    break;
                }
            }

            if (found is null || !found.HasDur)
                return Refuse();
            uint missing = found.DurMax - Math.Min(found.DurCur, found.DurMax);
            uint price = (uint)(RawTables.ById("EsItemData").GetValueOrDefault(found.Cfg)?.U(9) ?? 100);
            long cost = Math.Max(1, (long)price * missing / Math.Max(1, found.DurMax) / 2);
            if (cost > profile.Ammo.GetValueOrDefault(Money))
                return Refuse();
            profile.Ammo[Money] = (uint)(profile.Ammo.GetValueOrDefault(Money) - cost);
            found.DurCur = found.DurMax;
            return ([(ItemUpdate, Update([Slot(type, slot, found)], profile, [Money])), (RepairReply, [])], true);
        }

        // ---- workbench -------------------------------------------------------------------------------------------------------------------------------

        public static PbW CraftEntry(DCraft craft)
        {
            var w = new PbW().V(1, craft.Recipe).V(2, craft.Count).V(3, (ulong)craft.FinishAt).V(4, 2);
            var materials = new PbW();
            foreach ((uint cfg, uint count) in craft.Materials)
                materials.M(1, new PbW().V(1, cfg).V(2, count));
            return w.M(5, materials);
        }

        /// <summary>The workbench in <c>SC_GetDarkovInfo</c>: the slots, each empty or with the recipe being made.</summary>
        public static PbW Workbench(DarkovProfile profile)
        {
            var slots = new PbW().V(1, (ulong)profile.Crafts.Count);
            foreach (DCraft? craft in profile.Crafts)
                slots.M(2, craft is null ? new PbW() : CraftEntry(craft));
            return new PbW().V(1, 1).M(2, slots);
        }

        /// <summary>Takes <paramref name="amount"/> of an item from the backpack, then the storage; false (and nothing taken) when there is not enough.</summary>
        private static bool Take(DarkovProfile profile, uint cfg, uint amount, List<PbW> changed)
        {
            var containers = new (uint Type, List<DItem?> List)[] { (0, profile.Bag), (3, profile.Stash) };
            uint have = (uint)containers.Sum(c => c.List.Where(i => i is not null && i.Cfg == cfg).Sum(i => (long)i!.Count));
            if (have < amount)
                return false;
            foreach ((uint type, List<DItem?> list) in containers)
            {
                for (int i = 0; i < list.Count && amount > 0; i++)
                {
                    DItem? item = list[i];
                    if (item is null || item.Cfg != cfg)
                        continue;
                    uint used = Math.Min(amount, item.Count);
                    item.Count -= used;
                    amount -= used;
                    if (item.Count == 0)
                        list[i] = null;
                    changed.Add(Slot(type, i, list[i]));
                }
            }

            return true;
        }

        /// <summary>Puts <paramref name="amount"/> of an item into the storage (stacking first); false when it does not fit.</summary>
        public static bool Give(DarkovProfile profile, uint cfg, uint amount, List<PbW> changed)
        {
            var info = RawTables.ById("EsItemData").GetValueOrDefault(cfg);
            uint stackLimit = (uint)Math.Max(1, info?.U(5) ?? 1);
            uint left = amount;
            for (int i = 0; i < profile.Stash.Count && left > 0; i++)
            {
                DItem? stack = profile.Stash[i];
                if (stack is null || stack.Cfg != cfg || stack.HasDur || stack.Count >= stackLimit)
                    continue;
                uint add = Math.Min(left, stackLimit - stack.Count);
                stack.Count += add;
                left -= add;
                changed.Add(Slot(3, i, stack));
            }

            for (int i = 0; i < profile.Stash.Count && left > 0; i++)
            {
                if (profile.Stash[i] is not null)
                    continue;
                uint add = Math.Min(left, stackLimit);
                var fresh = new DItem { Uid = profile.NewUid(), Cfg = cfg, Count = add };
                if (info is not null && info.U(13) > 0)
                    fresh.DurCur = fresh.DurMax = (uint)info.U(13);
                profile.Stash[i] = fresh;
                left -= add;
                changed.Add(Slot(3, i, fresh));
            }

            return left == 0;
        }

        private static (List<(ushort, byte[])>, bool) StartCraft(PbMsg request, DarkovProfile profile)
        {
            PbMsg? order = request.Msg(1);
            uint recipeId = order is null ? 0 : (uint)order.U(1);
            uint times = order is null ? 0 : (uint)Math.Max(1, order.U(2));
            int slot = (int)request.U(2) - 1;
            if (!RawTables.ById("EsUnlockRecipeData").TryGetValue(recipeId, out PbMsg? recipe) || slot < 0 || slot >= profile.Crafts.Count || profile.Crafts[slot] is not null)
                return Refuse();
            var needs = recipe.Msgs(8).Select(m => ((uint)m.U(1), (uint)m.U(2) * times)).ToList();
            var changed = new List<PbW>();
            foreach ((uint cfg, uint amount) in needs)
                if (!Take(profile, cfg, amount, changed))
                    return Refuse();
            var craft = new DCraft
            {
                Recipe = recipeId, Count = times, FinishAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + Math.Max(1, (long)recipe.U(12)) * times,
                Materials = needs.ToDictionary(n => n.Item1, n => n.Item2),
            };
            profile.Crafts[slot] = craft;
            var reply = new PbW().M(1, new PbW().M(1, new PbW().V(1, recipeId).V(2, times)).V(2, (ulong)(slot + 1))).M(2, CraftEntry(craft));
            return ([(ItemUpdate, Update(changed)), (CraftReply, reply.ToArray())], true);
        }

        private static (List<(ushort, byte[])>, bool) PickCraft(PbMsg request, DarkovProfile profile)
        {
            PbMsg? pick = request.Msg(1);
            int slot = pick is null ? -1 : (int)pick.U(1) - 1;
            if (slot < 0 || slot >= profile.Crafts.Count || profile.Crafts[slot] is not { } craft || craft.FinishAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                return Refuse();
            PbMsg recipe = RawTables.ById("EsUnlockRecipeData")[craft.Recipe];
            var changed = new List<PbW>();
            if (!Give(profile, (uint)recipe.U(4), craft.Count, changed))
                return Refuse();
            profile.Crafts[slot] = null;
            return ([(ItemUpdate, Update(changed)), (CraftPickReply, new PbW().B(1, request.Raw(1) ?? []).ToArray())], true);
        }

        private static (List<(ushort, byte[])>, bool) CancelCraft(PbMsg request, DarkovProfile profile)
        {
            PbMsg? cancel = request.Msg(1);
            int slot = cancel is null ? -1 : (int)cancel.U(1) - 1;
            if (slot < 0 || slot >= profile.Crafts.Count || profile.Crafts[slot] is not { } craft)
                return Refuse();
            var changed = new List<PbW>();
            foreach ((uint cfg, uint amount) in craft.Materials)
                Give(profile, cfg, amount, changed);
            profile.Crafts[slot] = null;
            return ([(ItemUpdate, Update(changed)), (CraftCancelReply, new PbW().B(1, request.Raw(1) ?? []).ToArray())], true);
        }

        // ---- exploration -----------------------------------------------------------------------------------------------------------------------------

        public static byte[] ExplorationList(DarkovProfile profile)
        {
            var w = new PbW();
            foreach ((uint stage, DExplore progress) in profile.Stages.OrderBy(p => p.Key))
            {
                var entry = new PbW().V(1, stage);
                if (progress.Extractions > 0)
                    entry.M(2, new PbW().V(1, 9).V(2, progress.Extractions));
                if (progress.Bosses > 0)
                    entry.M(2, new PbW().V(1, 8).V(2, progress.Bosses));
                if (progress.Flags != 0)
                    entry.V(3, progress.Flags);
                entry.V(5, (ulong)progress.Since);
                w.M(1, entry);
            }

            return w.ToArray();
        }

        private static (List<(ushort, byte[])>, bool) Refuse() => ([(ReportError, new PbW().V(1, 0).S(2, InvalidInput).ToArray())], false);
    }
}
