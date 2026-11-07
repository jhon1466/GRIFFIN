using Griffin.Common.Networking;
using Griffin.Common.Protocol;
using Griffin.GameServer.Extensions;
using Griffin.Common.Proto;
using Griffin.Common.Table;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    /// <summary>
    /// Public talent skill items. The three items of the account are the ones of the original capture (ASSUMPTION: the server has no
    /// source that grants more); which character wears each one is NOT stored here but read from the characters
    /// (<c>GunEntity.PublicTalentSkillItemsUid</c>), so it survives a relaunch and always agrees with <c>SC_Guns</c>.
    /// </summary>
    public sealed class PublicTalentHandler(IGunService gunService, IAccountStateService accountState, Griffin.Common.Table.ITableService tableService, IItemUseService itemUse, ILoveBackfillService loveBackfill) : PublicTalentHandlerBase
    {
        public static readonly (ulong Id, uint StcId)[] Items =
        [
            (1843288605358841857UL, 91025),
            (1843288605358841858UL, 93007),
            (1843288605358841856UL, 91021),
        ];

        /// <summary>A public key the account got by completing the talent tree of a character (the three base items never change).</summary>
        public sealed class ExtraItem
        {
            public ulong Id { get; set; }

            public uint StcId { get; set; }
        }

        public const string ExtraKey = "publictalent.extra";

        /// <summary>State key of the affixes of each key (uid as text -> four 16-bit pairs: high byte = DevelopProperty, low byte = level), A-75.</summary>
        public const string AffixKey = "publictalent.affixes";

        public static ulong AffixesOf(IAccountStateService state, uint uid, ulong keyId) =>
            (state.Get<Dictionary<string, ulong>>(uid, AffixKey) ?? []).GetValueOrDefault(keyId.ToString());

        public static IReadOnlyList<(ulong Id, uint StcId)> ItemsOf(IAccountStateService state, uint uid) =>
            [.. Items, .. (state.Get<List<ExtraItem>>(uid, ExtraKey) ?? []).Select(e => (e.Id, e.StcId))];

        /// <summary>
        /// An earlier build put equipped affinity keys (TalentKeyData type 4, 911xxx) into the three shared slots; move each one to the
        /// affinity-key slots of the doll so the client finds it where it looks (Gun fields 59/60).
        /// </summary>
        private void MoveAffinityKeysOutOfSharedSlots(uint uid)
        {
            lock (AccountLock.For(uid))
            {
                Dictionary<string, LoveKeySlots.Slots> all = LoveKeySlots.All(accountState, uid);
                bool changed = false;
                foreach (Database.Entities.GunEntity gun in gunService.GetPlayerGuns(uid))
                    for (int i = 0; i < gun.PublicTalentSkillItems.Length && i < gun.PublicTalentSkillItemsUid.Length; i++)
                    {
                        uint stc = gun.PublicTalentSkillItems[i];
                        if (stc < LoveBackfillService.AffinityKeyBase + 1000 || stc >= LoveBackfillService.AffinityKeyBase + 2000)
                            continue;
                        LoveKeySlots.Slots slots = LoveKeySlots.Of(accountState, uid, gun.GunId);
                        int free = Array.IndexOf(slots.Uid, 0UL);
                        if (free >= 0 && !slots.Uid.Contains(gun.PublicTalentSkillItemsUid[i]))
                        {
                            slots.Stc[free] = stc;
                            slots.Uid[free] = gun.PublicTalentSkillItemsUid[i];
                            all[gun.GunId.ToString()] = slots;
                        }

                        uint[] items = (uint[])gun.PublicTalentSkillItems.Clone();
                        ulong[] uids = (ulong[])gun.PublicTalentSkillItemsUid.Clone();
                        items[i] = 0;
                        uids[i] = 0;
                        gun.PublicTalentSkillItems = items;
                        gun.PublicTalentSkillItemsUid = uids;
                        changed = true;
                    }

                if (changed)
                {
                    LoveKeySlots.Save(accountState, uid, all);
                    gunService.Save();
                }
            }
        }

        // The names of this message come from an older build: the 4.0.5136 client (NEHFNMNLEHL) reads field 4 as the AFFIXES of the key (four 16-bit pairs)
        // and field 5 as its lock (A-75).
        public static PublicSkillItem ToProto(ulong id, uint stcId, uint gunId, ulong affixes = 0) =>
            new() { Id = id, StcId = stcId, GunId = gunId, Locked = affixes, MDFIDAFPBJN = false };

        /// <summary>State key of the copies made of each key (original uid as text -> uids of its copies).</summary>
        public const string CopiesKey = "publictalent.copies";

        private const ulong CopyIdBase = 1843289000000000000UL;

        /// <summary>
        /// 复制键 (copy a key): the client sends the uid of the key (field 1) and expects back the original and the new copy (fields 1, 2).
        /// TalentKeyCopyableCostCsData gives per key the most copies (column 2), the item paid (column 3) and its amount for the 1st, 2nd... copy
        /// (column 4). The message was unknown, so the button did nothing. Copies are kept as extra keys and listed in field 6 of the original.
        /// </summary>
        public override void HandlePublicTalentSkillItemCopy(CS_PublicTalentSkillItemCopy request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            List<Google.Protobuf.IMessage> messages = [];
            lock (AccountLock.For(uid))
            {
                (ulong Id, uint StcId) original = ItemsOf(accountState, uid).FirstOrDefault(i => i.Id == request.Id);
                Dictionary<string, List<ulong>> copies = accountState.Get<Dictionary<string, List<ulong>>>(uid, CopiesKey) ?? [];
                List<ulong> mine = copies.GetValueOrDefault(request.Id.ToString()) ?? [];
                Griffin.Common.Table.TalentKeyCopyableCostCsData? row = original.Id == 0 ? null
                    : tableService.GetTable<Griffin.Common.Table.TalentKeyCopyableCostCsData>().FirstOrDefault(r => r.Id == original.StcId);
                if (row is null || mine.Count >= row.MaxCopies)
                {
                    connection.Refuse(MsgId.MsgCsPublicTalentSkillItemCopy, HNINDAOJCKP.ClientInputInvalid);
                    return;
                }

                int price = mine.Count < row.Costs.Count ? (int)row.Costs[mine.Count] : row.Costs.Count > 0 ? (int)row.Costs[^1] : 0;
                if (row.CostItem != 0 && price > 0 && !itemUse.Spend(uid, new Dictionary<uint, int> { [row.CostItem] = price }, messages))
                {
                    connection.Refuse(MsgId.MsgCsPublicTalentSkillItemCopy, HNINDAOJCKP.ItemNotEnough);
                    return;
                }

                HashSet<ulong> used = ItemsOf(accountState, uid).Select(i => i.Id).ToHashSet();
                ulong copyId = CopyIdBase + (ulong)original.StcId * 100;
                while (used.Contains(copyId))
                    copyId++;

                List<ExtraItem> extra = accountState.Get<List<ExtraItem>>(uid, ExtraKey) ?? [];
                extra.Add(new ExtraItem { Id = copyId, StcId = original.StcId });
                accountState.Set(uid, ExtraKey, extra);
                mine.Add(copyId);
                copies[request.Id.ToString()] = mine;
                accountState.Set(uid, CopiesKey, copies);

                messages.Add(new SC_PublicTalentSkillItemCopy { Id = request.Id, CopyId = copyId });
            }

            connection.SendAutoEncrypted(messages.ToArray());
        }

        public override void HandlePublicTalentSkillItems(CS_PublicTalentSkillItems request, Connection connection)
        {
            // dolls already at love level 5+ get their affinity key (契合键) if the server never gave it
            if (connection.Account is not null)
            {
                loveBackfill.BackfillAffinityKeys(connection.Account.Uid);
                MoveAffinityKeysOutOfSharedSlots(connection.Account.Uid);
            }
            Database.Entities.GunEntity[] guns = connection.Account is null ? [] : gunService.GetPlayerGuns(connection.Account.Uid);
            SC_PublicTalentSkillItems reply = new();
            foreach ((ulong id, uint stc) in connection.Account is null ? Items : ItemsOf(accountState, connection.Account.Uid))
                reply.PublicSkillItems.Add(ToProto(id, stc, guns.FirstOrDefault(g => g.PublicTalentSkillItemsUid.Contains(id))?.GunId
                    ?? (connection.Account is null ? 0 : LoveKeySlots.HolderOf(accountState, connection.Account.Uid, id)),
                    connection.Account is null ? 0 : AffixesOf(accountState, connection.Account.Uid, id)));
            if (connection.Account is not null)
                foreach ((string original, List<ulong> made) in accountState.Get<Dictionary<string, List<ulong>>>(connection.Account.Uid, CopiesKey) ?? [])
                    if (reply.PublicSkillItems.FirstOrDefault(i => i.Id.ToString() == original) is { } item)
                        item.Copies.AddRange(made);
            connection.Send(reply);
        }

        /// <summary>
        /// The client chooses, resets or raises the affixes of a key by sending the complete new configuration in field 6 (up to four pairs, high byte =
        /// property, low byte = level) and expects the confirmed one back (<c>KADHBNLJHPP</c>). It is checked against the tables (properties of
        /// <c>EntryData</c> column 4 for the rank of the key, levels 1 to <c>EntryData</c> column 3), saved and returned; the cost of the operation
        /// (<c>EntryPropertLevelData</c> columns 5 and 6) is not charged because the table does not say which of the two applies to each type (A-75).
        /// </summary>
        public override void HandlePublicTalentItemLevelUp(CS_PublicTalentItemLevelUp request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            ulong bits = request.Locked;
            if (ItemsOf(accountState, uid).All(item => item.Id != request.Id) || !ValidAffixes(bits, ItemsOf(accountState, uid).First(item => item.Id == request.Id).StcId))
            {
                connection.Refuse(MsgId.MsgCsPublicTalentItemLevelUp, HNINDAOJCKP.ClientInputInvalid);
                return;
            }

            List<Google.Protobuf.IMessage> messages = [];
            lock (AccountLock.For(uid))
            {
                Dictionary<string, ulong> saved = accountState.Get<Dictionary<string, ulong>>(uid, AffixKey) ?? [];
                uint stc = ItemsOf(accountState, uid).First(item => item.Id == request.Id).StcId;
                if (request.Type != PEJMKCFPIDM.TypeReset)
                {
                    TalentKeyData? keyRow = tableService.GetTable<TalentKeyData>().FirstOrDefault(row => TableFields.U(row, 1) == stc);
                    IReadOnlyDictionary<int, int> keyBase = keyRow is null ? new Dictionary<int, int>() : BattleUnitBuilder.RowByDevelopProperty(tableService, TableFields.U(keyRow, 5));

                    Dictionary<uint, int> cost = KeyAffixes.Cost(tableService, saved.GetValueOrDefault(request.Id.ToString()), bits, stc, keyBase);
                    if (cost.Count > 0 && !itemUse.Spend(uid, cost, messages))
                    {
                        connection.Refuse(MsgId.MsgCsPublicTalentItemLevelUp, HNINDAOJCKP.ItemNotEnough);
                        return;
                    }
                }

                saved[request.Id.ToString()] = bits;
                accountState.Set(uid, AffixKey, saved);
            }

            messages.Add(new SC_PublicTalentItemLevelUp
            {
                Id = request.Id, Type = request.Type, Index = request.Index, Level = request.Level, NLHNEDAJMAD = request.NLHNEDAJMAD, Locked = bits,
            });
            connection.SendAutoEncrypted(messages.ToArray());
        }

        private bool ValidAffixes(ulong bits, uint keyStcId)
        {
            if (bits == 0)
                return true;

            return KeyAffixes.Valid(tableService, bits, keyStcId);
        }
    }
}
